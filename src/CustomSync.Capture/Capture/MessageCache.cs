using Microsoft.Data.Sqlite;

namespace CustomSync.Capture.Capture;

public class MessageCache
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly TimeProvider? _timeProvider;

    public MessageCache(string databasePath, TimeProvider? timeProvider = null)
    {
        _databasePath = databasePath;
        _connectionString = $"Data Source={databasePath};Default Timeout=30;";
        _timeProvider = timeProvider;
    }

    // Kutish 30 soniya: yozuvchi bitta (update handler) + pruner. Qulf
    // band bo'lsa uzoqroq kutish arzon, "database is locked" bilan yiqilgan
    // o'chirish tranzaksiyasi esa hodisani butunlay yo'qotadi — Telegram
    // updateDeleteMessages ni qayta yubormaydi.
    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 30000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public virtual void Initialize()
    {
        try
        {
            var dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var conn = OpenConnection();

            // PRAGMA alohida: u qator qaytaradi va Microsoft.Data.Sqlite shu
            // qatordan keyingi statement xatosini JIMGINA yutib, skriptning
            // qolganini bajarmay qo'yadi (eski bazada jadvallar yaratilmay
            // qolardi).
            using (var walCmd = conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode = WAL;";
                walCmd.ExecuteScalar();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS message_cache (
                    chat_id INTEGER NOT NULL,
                    message_id INTEGER NOT NULL,
                    text TEXT,
                    sender_id TEXT,
                    is_out INTEGER NOT NULL,
                    is_media INTEGER NOT NULL,
                    media_id TEXT,
                    date INTEGER NOT NULL,
                    cached_at INTEGER NOT NULL,
                    PRIMARY KEY (chat_id, message_id)
                );
                CREATE INDEX IF NOT EXISTS idx_message_cache_cached_at ON message_cache (cached_at);
                CREATE TABLE IF NOT EXISTS capture_outbox (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    peer_id TEXT NOT NULL,
                    msg_id INTEGER NOT NULL,
                    occurred_at INTEGER NOT NULL,
                    observed_at INTEGER NOT NULL,
                    payload_json TEXT NOT NULL,
                    created_at INTEGER NOT NULL,
                    retry_count INTEGER NOT NULL DEFAULT 0,
                    next_retry_at INTEGER,
                    last_error TEXT,
                    UNIQUE (kind, account_id, peer_id, msg_id, occurred_at)
                );
                CREATE INDEX IF NOT EXISTS idx_capture_outbox_created_at ON capture_outbox (created_at);
                CREATE TABLE IF NOT EXISTS activity_latest (
                    peer_id TEXT NOT NULL,
                    field TEXT NOT NULL,
                    value TEXT NOT NULL,
                    observed_at INTEGER NOT NULL,
                    PRIMARY KEY (peer_id, field)
                );
                CREATE TABLE IF NOT EXISTS pending_edits (
                    chat_id INTEGER NOT NULL,
                    message_id INTEGER NOT NULL,
                    peer_id TEXT,
                    account_id TEXT,
                    old_text TEXT,
                    new_text TEXT,
                    is_out INTEGER,
                    msg_date INTEGER,
                    edit_date INTEGER,
                    observed_at INTEGER NOT NULL,
                    PRIMARY KEY (chat_id, message_id)
                );
                CREATE INDEX IF NOT EXISTS idx_pending_edits_observed_at ON pending_edits (observed_at);
                CREATE TABLE IF NOT EXISTS synced_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    occurred_at INTEGER NOT NULL,
                    record_id TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS sync_state (
                    name TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS captured_media (
                    peer_id TEXT NOT NULL,
                    msg_id INTEGER NOT NULL,
                    chat_id INTEGER NOT NULL,
                    message_id INTEGER NOT NULL,
                    content_type TEXT NOT NULL,
                    status TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at INTEGER,
                    local_path TEXT,
                    sha256 TEXT,
                    size INTEGER,
                    created_at INTEGER NOT NULL,
                    PRIMARY KEY (peer_id, msg_id)
                );
                CREATE INDEX IF NOT EXISTS idx_captured_media_status_next ON captured_media (status, next_attempt_at);
            ";
            cmd.ExecuteNonQuery();

            using var versionCmd = conn.CreateCommand();
            versionCmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(versionCmd.ExecuteScalar());
            if (version == 0)
            {
                using var setVersionCmd = conn.CreateCommand();
                setVersionCmd.CommandText = "PRAGMA user_version = 5;";
                setVersionCmd.ExecuteNonQuery();
            }
            else
            {
                if (version == 1)
                {
                    // Bitta tranzaksiya: yarim qo'shilgan ustunlar bilan qolgan
                    // baza keyingi startda "duplicate column" bilan yiqilardi.
                    using var upgradeTx = conn.BeginTransaction();
                    using var upgradeCmd = conn.CreateCommand();
                    upgradeCmd.Transaction = upgradeTx;
                    upgradeCmd.CommandText = @"
                        ALTER TABLE capture_outbox ADD COLUMN retry_count INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE capture_outbox ADD COLUMN next_retry_at INTEGER;
                        ALTER TABLE capture_outbox ADD COLUMN last_error TEXT;
                        PRAGMA user_version = 2;
                    ";
                    upgradeCmd.ExecuteNonQuery();
                    upgradeTx.Commit();
                    version = 2;
                }

                if (version == 2)
                {
                    using var upgradeTx2 = conn.BeginTransaction();
                    using var upgradeCmd2 = conn.CreateCommand();
                    upgradeCmd2.Transaction = upgradeTx2;
                    upgradeCmd2.CommandText = @"
                        CREATE TABLE IF NOT EXISTS synced_settings (
                            key TEXT PRIMARY KEY,
                            value TEXT NOT NULL,
                            occurred_at INTEGER NOT NULL,
                            record_id TEXT NOT NULL
                        );
                        CREATE TABLE IF NOT EXISTS sync_state (
                            name TEXT PRIMARY KEY,
                            value TEXT NOT NULL
                        );
                        PRAGMA user_version = 3;
                    ";
                    upgradeCmd2.ExecuteNonQuery();
                    upgradeTx2.Commit();
                    version = 3;
                }

                if (version == 3)
                {
                    using var upgradeTx3 = conn.BeginTransaction();
                    using var upgradeCmd3 = conn.CreateCommand();
                    upgradeCmd3.Transaction = upgradeTx3;
                    upgradeCmd3.CommandText = @"
                        CREATE TABLE IF NOT EXISTS captured_media (
                            peer_id TEXT NOT NULL,
                            msg_id INTEGER NOT NULL,
                            chat_id INTEGER NOT NULL,
                            message_id INTEGER NOT NULL,
                            content_type TEXT NOT NULL,
                            status TEXT NOT NULL,
                            attempts INTEGER NOT NULL DEFAULT 0,
                            next_attempt_at INTEGER,
                            local_path TEXT,
                            sha256 TEXT,
                            size INTEGER,
                            created_at INTEGER NOT NULL,
                            PRIMARY KEY (peer_id, msg_id)
                        );
                        CREATE INDEX IF NOT EXISTS idx_captured_media_status_next ON captured_media (status, next_attempt_at);
                        PRAGMA user_version = 4;
                    ";
                    upgradeCmd3.ExecuteNonQuery();
                    upgradeTx3.Commit();
                    version = 4;
                }

                if (version == 4)
                {
                    using var upgradeTx4 = conn.BeginTransaction();
                    using var upgradeCmd4 = conn.CreateCommand();
                    upgradeCmd4.Transaction = upgradeTx4;

                    long now = (_timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();

                    // Tuzoq: Initialize CREATE TABLE IF NOT EXISTS ni versiya
                    // qadamlaridan oldin yurgizadi, shuning uchun v1-v3 bazalar
                    // yangi ta'rifni allaqachon olgan bo'lishi mumkin.
                    // Faqat created_at ustuni yo'q bo'lsa ALTER TABLE qilinadi.
                    bool hasCreatedAt = false;
                    using (var infoCmd = conn.CreateCommand())
                    {
                        infoCmd.Transaction = upgradeTx4;
                        infoCmd.CommandText = "PRAGMA table_info(captured_media);";
                        using var reader = infoCmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var colName = reader.GetString(1);
                            if (string.Equals(colName, "created_at", StringComparison.OrdinalIgnoreCase))
                            {
                                hasCreatedAt = true;
                                break;
                            }
                        }
                    }

                    if (!hasCreatedAt)
                    {
                        upgradeCmd4.CommandText = $@"
                            ALTER TABLE captured_media ADD COLUMN created_at INTEGER NOT NULL DEFAULT {now};
                            PRAGMA user_version = 5;
                        ";
                        upgradeCmd4.ExecuteNonQuery();
                    }
                    else
                    {
                        upgradeCmd4.CommandText = $@"
                            UPDATE captured_media SET created_at = {now} WHERE created_at = 0;
                            PRAGMA user_version = 5;
                        ";
                        upgradeCmd4.ExecuteNonQuery();
                    }

                    upgradeTx4.Commit();
                    version = 5;
                }
            }

            // Yangi ustunga bog'liq indeks migratsiyadan KEYIN.
            using (var retryIdxCmd = conn.CreateCommand())
            {
                retryIdxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_capture_outbox_retry ON capture_outbox (next_retry_at);";
                retryIdxCmd.ExecuteNonQuery();
            }

            using (var mediaCreatedIdxCmd = conn.CreateCommand())
            {
                mediaCreatedIdxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_captured_media_created_at ON captured_media (created_at);";
                mediaCreatedIdxCmd.ExecuteNonQuery();
            }
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to initialize message cache.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to initialize message cache.", ex);
        }
    }

    public virtual CachedMessage? Put(CachedMessage message)
    {
        try
        {
            using var conn = OpenConnection();
            using (var beginCmd = conn.CreateCommand())
            {
                beginCmd.CommandText = "BEGIN IMMEDIATE;";
                beginCmd.ExecuteNonQuery();
            }

            try
            {
                CachedMessage? previous = null;
                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.CommandText = @"
                        SELECT chat_id, message_id, text, sender_id, is_out, is_media, media_id, date, cached_at
                        FROM message_cache
                        WHERE chat_id = @chat_id AND message_id = @message_id;";
                    selCmd.Parameters.AddWithValue("@chat_id", message.ChatId);
                    selCmd.Parameters.AddWithValue("@message_id", message.MessageId);

                    using var reader = selCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        previous = ReadRow(reader);
                    }
                }

                long now = (_timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
                long cachedAt = message.CachedAt ?? now;

                using (var insCmd = conn.CreateCommand())
                {
                    insCmd.CommandText = @"
                        INSERT INTO message_cache (chat_id, message_id, text, sender_id, is_out, is_media, media_id, date, cached_at)
                        VALUES (@chat_id, @message_id, @text, @sender_id, @is_out, @is_media, @media_id, @date, @cached_at)
                        ON CONFLICT (chat_id, message_id) DO UPDATE SET
                            text = excluded.text,
                            sender_id = excluded.sender_id,
                            is_out = excluded.is_out,
                            is_media = excluded.is_media,
                            media_id = excluded.media_id,
                            date = excluded.date,
                            cached_at = excluded.cached_at;";

                    insCmd.Parameters.AddWithValue("@chat_id", message.ChatId);
                    insCmd.Parameters.AddWithValue("@message_id", message.MessageId);
                    insCmd.Parameters.AddWithValue("@text", (object?)message.Text ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("@sender_id", (object?)message.SenderId ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("@is_out", message.IsOut ? 1 : 0);
                    insCmd.Parameters.AddWithValue("@is_media", message.IsMedia ? 1 : 0);
                    insCmd.Parameters.AddWithValue("@media_id", (object?)message.MediaId ?? DBNull.Value);
                    insCmd.Parameters.AddWithValue("@date", message.Date);
                    insCmd.Parameters.AddWithValue("@cached_at", cachedAt);

                    insCmd.ExecuteNonQuery();
                }

                using (var commitCmd = conn.CreateCommand())
                {
                    commitCmd.CommandText = "COMMIT;";
                    commitCmd.ExecuteNonQuery();
                }

                return previous;
            }
            catch
            {
                try
                {
                    using var rollbackCmd = conn.CreateCommand();
                    rollbackCmd.CommandText = "ROLLBACK;";
                    rollbackCmd.ExecuteNonQuery();
                }
                catch
                {
                    // Ignore rollback failures
                }
                throw;
            }
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to put message into cache.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to put message into cache.", ex);
        }
    }

    /// <summary>
    /// Qator yo'q bo'lsagina qo'shadi; bor qatorni hech qachon yangilamaydi.
    /// Kechikib kelgan getMessage javobi yangiroq ma'lumot ustiga yozmasligi
    /// uchun.
    /// </summary>
    public virtual bool TryAdd(CachedMessage message)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO message_cache (chat_id, message_id, text, sender_id, is_out, is_media, media_id, date, cached_at)
                VALUES (@chat_id, @message_id, @text, @sender_id, @is_out, @is_media, @media_id, @date, @cached_at)
                ON CONFLICT (chat_id, message_id) DO NOTHING;";
            cmd.Parameters.AddWithValue("@chat_id", message.ChatId);
            cmd.Parameters.AddWithValue("@message_id", message.MessageId);
            cmd.Parameters.AddWithValue("@text", (object?)message.Text ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sender_id", (object?)message.SenderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@is_out", message.IsOut ? 1 : 0);
            cmd.Parameters.AddWithValue("@is_media", message.IsMedia ? 1 : 0);
            cmd.Parameters.AddWithValue("@media_id", (object?)message.MediaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@date", message.Date);
            cmd.Parameters.AddWithValue("@cached_at",
                message.CachedAt ?? (_timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to add message to cache.", ex);
        }
    }

    public virtual CachedMessage? Get(long chatId, long messageId)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT chat_id, message_id, text, sender_id, is_out, is_media, media_id, date, cached_at
                FROM message_cache
                WHERE chat_id = @chat_id AND message_id = @message_id;";
            cmd.Parameters.AddWithValue("@chat_id", chatId);
            cmd.Parameters.AddWithValue("@message_id", messageId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadRow(reader);
            }

            return null;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to get message from cache.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to get message from cache.", ex);
        }
    }

    public virtual int Prune(int olderThanDays)
    {
        try
        {
            long now = (_timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
            long cutoff = now - (olderThanDays * 86400L);

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            // strictly < cutoff, boundary row cached_at == cutoff survives
            cmd.CommandText = "DELETE FROM message_cache WHERE cached_at < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);

            return cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to prune message cache.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to prune message cache.", ex);
        }
    }

    public virtual CacheStats Stats()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM message_cache;";
            long rowCount = Convert.ToInt64(cmd.ExecuteScalar());

            // WAL rejimida yangi yozuvlar `-wal` faylida turadi va u MB
            // larga yetishi mumkin. Faqat asosiy faylni sanash diskdagi
            // haqiqiy hajmni kam ko'rsatadi (Task 9 dagi kvota shu
            // raqamga tayanadi).
            long fileSizeBytes = 0;
            foreach (var part in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            {
                if (File.Exists(part))
                {
                    fileSizeBytes += new FileInfo(part).Length;
                }
            }

            return new CacheStats(rowCount, fileSizeBytes);
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to retrieve cache stats.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to retrieve cache stats.", ex);
        }
    }

    public virtual DeleteResult DeleteMessagesAndRecordOutbox(
        long chatId,
        string peerId,
        string accountId,
        IReadOnlyList<long> serverMessageIds,
        long? observedAt = null,
        Action<int>? onInsertAction = null)
    {
        try
        {
            long now = observedAt ?? (_timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            int deletedCount = 0;
            int uncachedCount = 0;

            try
            {
                foreach (var serverMsgId in serverMessageIds)
                {
                    CachedMessage? cached = null;
                    using (var selCmd = conn.CreateCommand())
                    {
                        selCmd.Transaction = tx;
                        selCmd.CommandText = @"
                            SELECT chat_id, message_id, text, sender_id, is_out, is_media, media_id, date, cached_at
                            FROM message_cache
                            WHERE chat_id = @chat_id AND message_id = @message_id;";
                        selCmd.Parameters.AddWithValue("@chat_id", chatId);
                        selCmd.Parameters.AddWithValue("@message_id", serverMsgId);

                        using var reader = selCmd.ExecuteReader();
                        if (reader.Read())
                        {
                            cached = ReadRow(reader);
                        }
                    }

                    if (cached is null)
                    {
                        uncachedCount++;
                        continue;
                    }

                    long occurredAt = cached.Date > 0 ? cached.Date : now;
                    string payloadJson = PayloadBuilder.BuildDeleted(
                        accountId,
                        peerId,
                        cached.Text,
                        cached.SenderId,
                        cached.IsOut,
                        cached.IsMedia);

                    onInsertAction?.Invoke(deletedCount);

                    using (var insCmd = conn.CreateCommand())
                    {
                        insCmd.Transaction = tx;
                        insCmd.CommandText = @"
                            INSERT OR REPLACE INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                            VALUES ('deleted', @account_id, @peer_id, @msg_id, @occurred_at, @observed_at, @payload_json, @created_at);";
                        insCmd.Parameters.AddWithValue("@account_id", accountId);
                        insCmd.Parameters.AddWithValue("@peer_id", peerId);
                        insCmd.Parameters.AddWithValue("@msg_id", serverMsgId);
                        insCmd.Parameters.AddWithValue("@occurred_at", occurredAt);
                        insCmd.Parameters.AddWithValue("@observed_at", now);
                        insCmd.Parameters.AddWithValue("@payload_json", payloadJson);
                        insCmd.Parameters.AddWithValue("@created_at", now);
                        insCmd.ExecuteNonQuery();
                    }

                    using (var delCmd = conn.CreateCommand())
                    {
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM message_cache WHERE chat_id = @chat_id AND message_id = @message_id;";
                        delCmd.Parameters.AddWithValue("@chat_id", chatId);
                        delCmd.Parameters.AddWithValue("@message_id", serverMsgId);
                        delCmd.ExecuteNonQuery();
                    }

                    // edit_date ni kutayotgan tahrir tashlanmaydi: eski matn
                    // faqat o'sha qatorda qolgan (kesh va `deleted` yozuvida
                    // yangi matn). Zaxira occurred_at bilan chiqariladi.
                    FlushPendingEdit(conn, tx, chatId, serverMsgId, now);

                    deletedCount++;
                }

                tx.Commit();
                return new DeleteResult(deletedCount, uncachedCount);
            }
            catch
            {
                try
                {
                    tx.Rollback();
                }
                catch
                {
                    // Ignore rollback failure
                }
                throw;
            }
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to delete messages and record outbox.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to delete messages and record outbox.", ex);
        }
    }

    public virtual EditResult UpdateMessageContent(
        long chatId,
        string peerId,
        string accountId,
        long serverMsgId,
        string newText,
        long? observedAt = null,
        bool emitOutbox = true)
    {
        try
        {
            long now = observedAt ?? (_timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            try
            {
                CachedMessage? cached = null;
                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.Transaction = tx;
                    selCmd.CommandText = @"
                        SELECT chat_id, message_id, text, sender_id, is_out, is_media, media_id, date, cached_at
                        FROM message_cache
                        WHERE chat_id = @chat_id AND message_id = @message_id;";
                    selCmd.Parameters.AddWithValue("@chat_id", chatId);
                    selCmd.Parameters.AddWithValue("@message_id", serverMsgId);

                    using var reader = selCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        cached = ReadRow(reader);
                    }
                }

                if (cached is null)
                {
                    // Bu yerda qator YOZILMAYDI: updateMessageContent da
                    // sana, yuboruvchi va is_out yo'q. Ularni to'qib yozish
                    // keyingi `deleted` yozuviga yolg'on maydonlar va
                    // tdesktop'nikidan boshqa record_id (occurred_at = now)
                    // beradi. To'liq xabarni handler getMessage bilan oladi.
                    tx.Commit();
                    return EditResult.NotCached;
                }

                string oldText = cached.Text ?? "";
                string effectiveNewText = newText ?? "";

                if (oldText == effectiveNewText)
                {
                    tx.Commit();
                    return EditResult.Unchanged;
                }

                using (var updCmd = conn.CreateCommand())
                {
                    updCmd.Transaction = tx;
                    updCmd.CommandText = "UPDATE message_cache SET text = @text WHERE chat_id = @chat_id AND message_id = @message_id;";
                    updCmd.Parameters.AddWithValue("@chat_id", chatId);
                    updCmd.Parameters.AddWithValue("@message_id", serverMsgId);
                    updCmd.Parameters.AddWithValue("@text", (object?)effectiveNewText ?? DBNull.Value);
                    updCmd.ExecuteNonQuery();
                }

                if (!emitOutbox)
                {
                    using (var delPendingCmd = conn.CreateCommand())
                    {
                        delPendingCmd.Transaction = tx;
                        delPendingCmd.CommandText = "DELETE FROM pending_edits WHERE chat_id = @chat_id AND message_id = @message_id AND new_text IS NULL;";
                        delPendingCmd.Parameters.AddWithValue("@chat_id", chatId);
                        delPendingCmd.Parameters.AddWithValue("@message_id", serverMsgId);
                        delPendingCmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                    return EditResult.Edited;
                }

                bool hasPending = false;
                string? pendingOldText = null;
                string? pendingNewText = null;
                bool? pendingIsOut = null;
                long? pendingMsgDate = null;
                long? pendingEditDate = null;
                long pendingObservedAt = 0;

                using (var selPendingCmd = conn.CreateCommand())
                {
                    selPendingCmd.Transaction = tx;
                    selPendingCmd.CommandText = @"
                        SELECT old_text, new_text, is_out, msg_date, edit_date, observed_at
                        FROM pending_edits
                        WHERE chat_id = @chat_id AND message_id = @message_id;";
                    selPendingCmd.Parameters.AddWithValue("@chat_id", chatId);
                    selPendingCmd.Parameters.AddWithValue("@message_id", serverMsgId);

                    using var reader = selPendingCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        hasPending = true;
                        pendingOldText = reader.IsDBNull(0) ? null : reader.GetString(0);
                        pendingNewText = reader.IsDBNull(1) ? null : reader.GetString(1);
                        pendingIsOut = reader.IsDBNull(2) ? null : reader.GetInt32(2) != 0;
                        pendingMsgDate = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                        pendingEditDate = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                        pendingObservedAt = reader.GetInt64(5);
                    }
                }

                if (hasPending && pendingNewText == null)
                {
                    long editDateVal = pendingEditDate ?? 0;
                    long occurredAt = editDateVal > 0 ? editDateVal : (cached.Date > 0 ? cached.Date : now);
                    string payloadJson = PayloadBuilder.BuildEdited(
                        accountId,
                        peerId,
                        oldText,
                        effectiveNewText,
                        cached.IsOut);

                    using (var insCmd = conn.CreateCommand())
                    {
                        insCmd.Transaction = tx;
                        insCmd.CommandText = @"
                            INSERT OR REPLACE INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                            VALUES ('edited', @account_id, @peer_id, @msg_id, @occurred_at, @observed_at, @payload_json, @created_at);";
                        insCmd.Parameters.AddWithValue("@account_id", accountId);
                        insCmd.Parameters.AddWithValue("@peer_id", peerId);
                        insCmd.Parameters.AddWithValue("@msg_id", serverMsgId);
                        insCmd.Parameters.AddWithValue("@occurred_at", occurredAt);
                        insCmd.Parameters.AddWithValue("@observed_at", now);
                        insCmd.Parameters.AddWithValue("@payload_json", payloadJson);
                        insCmd.Parameters.AddWithValue("@created_at", now);
                        insCmd.ExecuteNonQuery();
                    }

                    using (var delCmd = conn.CreateCommand())
                    {
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM pending_edits WHERE chat_id = @chat_id AND message_id = @message_id;";
                        delCmd.Parameters.AddWithValue("@chat_id", chatId);
                        delCmd.Parameters.AddWithValue("@message_id", serverMsgId);
                        delCmd.ExecuteNonQuery();
                    }
                }
                else if (hasPending && pendingNewText != null)
                {
                    long prevEditDateVal = pendingEditDate ?? 0;
                    long prevOccurredAt = prevEditDateVal > 0 ? prevEditDateVal : ((pendingMsgDate ?? 0) > 0 ? pendingMsgDate!.Value : pendingObservedAt);
                    string prevPayloadJson = PayloadBuilder.BuildEdited(
                        accountId,
                        peerId,
                        pendingOldText ?? "",
                        pendingNewText,
                        pendingIsOut ?? cached.IsOut);

                    using (var insCmd = conn.CreateCommand())
                    {
                        insCmd.Transaction = tx;
                        insCmd.CommandText = @"
                            INSERT OR REPLACE INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                            VALUES ('edited', @account_id, @peer_id, @msg_id, @occurred_at, @observed_at, @payload_json, @created_at);";
                        insCmd.Parameters.AddWithValue("@account_id", accountId);
                        insCmd.Parameters.AddWithValue("@peer_id", peerId);
                        insCmd.Parameters.AddWithValue("@msg_id", serverMsgId);
                        insCmd.Parameters.AddWithValue("@occurred_at", prevOccurredAt);
                        insCmd.Parameters.AddWithValue("@observed_at", now);
                        insCmd.Parameters.AddWithValue("@payload_json", prevPayloadJson);
                        insCmd.Parameters.AddWithValue("@created_at", now);
                        insCmd.ExecuteNonQuery();
                    }

                    using (var updPendingCmd = conn.CreateCommand())
                    {
                        updPendingCmd.Transaction = tx;
                        updPendingCmd.CommandText = @"
                            UPDATE pending_edits
                            SET peer_id = @peer_id,
                                account_id = @account_id,
                                old_text = @old_text,
                                new_text = @new_text,
                                is_out = @is_out,
                                msg_date = @msg_date,
                                edit_date = NULL,
                                observed_at = @observed_at
                            WHERE chat_id = @chat_id AND message_id = @message_id;";
                        updPendingCmd.Parameters.AddWithValue("@chat_id", chatId);
                        updPendingCmd.Parameters.AddWithValue("@message_id", serverMsgId);
                        updPendingCmd.Parameters.AddWithValue("@peer_id", peerId);
                        updPendingCmd.Parameters.AddWithValue("@account_id", accountId);
                        updPendingCmd.Parameters.AddWithValue("@old_text", oldText);
                        updPendingCmd.Parameters.AddWithValue("@new_text", effectiveNewText);
                        updPendingCmd.Parameters.AddWithValue("@is_out", cached.IsOut ? 1 : 0);
                        updPendingCmd.Parameters.AddWithValue("@msg_date", cached.Date);
                        updPendingCmd.Parameters.AddWithValue("@observed_at", now);
                        updPendingCmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    using (var insPendingCmd = conn.CreateCommand())
                    {
                        insPendingCmd.Transaction = tx;
                        insPendingCmd.CommandText = @"
                            INSERT INTO pending_edits (chat_id, message_id, peer_id, account_id, old_text, new_text, is_out, msg_date, edit_date, observed_at)
                            VALUES (@chat_id, @message_id, @peer_id, @account_id, @old_text, @new_text, @is_out, @msg_date, NULL, @observed_at);";
                        insPendingCmd.Parameters.AddWithValue("@chat_id", chatId);
                        insPendingCmd.Parameters.AddWithValue("@message_id", serverMsgId);
                        insPendingCmd.Parameters.AddWithValue("@peer_id", peerId);
                        insPendingCmd.Parameters.AddWithValue("@account_id", accountId);
                        insPendingCmd.Parameters.AddWithValue("@old_text", oldText);
                        insPendingCmd.Parameters.AddWithValue("@new_text", effectiveNewText);
                        insPendingCmd.Parameters.AddWithValue("@is_out", cached.IsOut ? 1 : 0);
                        insPendingCmd.Parameters.AddWithValue("@msg_date", cached.Date);
                        insPendingCmd.Parameters.AddWithValue("@observed_at", now);
                        insPendingCmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
                return EditResult.Edited;
            }
            catch
            {
                try
                {
                    tx.Rollback();
                }
                catch
                {
                    // Ignore rollback failure
                }
                throw;
            }
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to update message content.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to update message content.", ex);
        }
    }

    public virtual IReadOnlyList<OutboxRow> GetOutboxRows(string? kind = null)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            if (string.IsNullOrEmpty(kind))
            {
                cmd.CommandText = @"
                    SELECT id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at, retry_count, next_retry_at, last_error
                    FROM capture_outbox
                    ORDER BY id ASC;";
            }
            else
            {
                cmd.CommandText = @"
                    SELECT id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at, retry_count, next_retry_at, last_error
                    FROM capture_outbox
                    WHERE kind = @kind
                    ORDER BY id ASC;";
                cmd.Parameters.AddWithValue("@kind", kind);
            }

            var list = new List<OutboxRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new OutboxRow(
                    Id: reader.GetInt64(0),
                    Kind: reader.GetString(1),
                    AccountId: reader.GetString(2),
                    PeerId: reader.GetString(3),
                    MsgId: reader.GetInt64(4),
                    OccurredAt: reader.GetInt64(5),
                    ObservedAt: reader.GetInt64(6),
                    PayloadJson: reader.GetString(7),
                    CreatedAt: reader.GetInt64(8),
                    RetryCount: reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetInt32(9) : 0,
                    NextRetryAt: reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetInt64(10) : null,
                    LastError: reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null));
            }

            return list;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to get outbox rows.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to get outbox rows.", ex);
        }
    }

    public virtual IReadOnlyList<OutboxRow> GetEligibleOutboxRows(int limit, long now)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at, retry_count, next_retry_at, last_error
                FROM capture_outbox
                WHERE next_retry_at IS NULL OR next_retry_at <= @now
                ORDER BY occurred_at ASC, id ASC
                LIMIT @limit;";
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = new List<OutboxRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new OutboxRow(
                    Id: reader.GetInt64(0),
                    Kind: reader.GetString(1),
                    AccountId: reader.GetString(2),
                    PeerId: reader.GetString(3),
                    MsgId: reader.GetInt64(4),
                    OccurredAt: reader.GetInt64(5),
                    ObservedAt: reader.GetInt64(6),
                    PayloadJson: reader.GetString(7),
                    CreatedAt: reader.GetInt64(8),
                    RetryCount: reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetInt32(9) : 0,
                    NextRetryAt: reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetInt64(10) : null,
                    LastError: reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null));
            }

            return list;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to get eligible outbox rows.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to get eligible outbox rows.", ex);
        }
    }

    public virtual bool DeleteOutboxRowById(long id)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM capture_outbox WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to delete outbox row by id.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to delete outbox row by id.", ex);
        }
    }

    public virtual void MarkOutboxRowError(long id, string errorMessage, long nextRetryAt)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE capture_outbox
                SET retry_count = retry_count + 1,
                    last_error = @last_error,
                    next_retry_at = @next_retry_at
                WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@last_error", errorMessage);
            cmd.Parameters.AddWithValue("@next_retry_at", nextRetryAt);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to mark outbox row error.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to mark outbox row error.", ex);
        }
    }

    public virtual bool RecordActivity(string accountId, string peerId, string field, string newValue, long now)
    {
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                bool hadPrevious = false;
                string? oldValue = null;

                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.Transaction = tx;
                    selCmd.CommandText = "SELECT value FROM activity_latest WHERE peer_id = @peer_id AND field = @field;";
                    selCmd.Parameters.AddWithValue("@peer_id", peerId);
                    selCmd.Parameters.AddWithValue("@field", field);
                    using var reader = selCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        hadPrevious = true;
                        oldValue = reader.GetString(0);
                    }
                }

                if (hadPrevious)
                {
                    if (oldValue == newValue)
                    {
                        tx.Rollback();
                        return false;
                    }

                    if (field == "status" && ActivityMapper.IsStatusNoise(oldValue!, newValue, now))
                    {
                        tx.Rollback();
                        return false;
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(newValue))
                    {
                        tx.Rollback();
                        return false;
                    }
                }

                using (var upsertCmd = conn.CreateCommand())
                {
                    upsertCmd.Transaction = tx;
                    upsertCmd.CommandText = @"
                        INSERT INTO activity_latest (peer_id, field, value, observed_at)
                        VALUES (@peer_id, @field, @value, @observed_at)
                        ON CONFLICT(peer_id, field) DO UPDATE SET
                            value = excluded.value,
                            observed_at = excluded.observed_at;";
                    upsertCmd.Parameters.AddWithValue("@peer_id", peerId);
                    upsertCmd.Parameters.AddWithValue("@field", field);
                    upsertCmd.Parameters.AddWithValue("@value", newValue);
                    upsertCmd.Parameters.AddWithValue("@observed_at", now);
                    upsertCmd.ExecuteNonQuery();
                }

                var payloadJson = PayloadBuilder.BuildActivity(accountId, peerId, field, hadPrevious, oldValue, newValue);
                long msgId = ActivityMapper.DiscriminatorFor(field);

                using (var insCmd = conn.CreateCommand())
                {
                    insCmd.Transaction = tx;
                    insCmd.CommandText = @"
                        INSERT OR REPLACE INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                        VALUES ('activity', @account_id, @peer_id, @msg_id, @occurred_at, @observed_at, @payload_json, @created_at);";
                    insCmd.Parameters.AddWithValue("@account_id", accountId);
                    insCmd.Parameters.AddWithValue("@peer_id", peerId);
                    insCmd.Parameters.AddWithValue("@msg_id", msgId);
                    insCmd.Parameters.AddWithValue("@occurred_at", now);
                    insCmd.Parameters.AddWithValue("@observed_at", now);
                    insCmd.Parameters.AddWithValue("@payload_json", payloadJson);
                    insCmd.Parameters.AddWithValue("@created_at", now);
                    insCmd.ExecuteNonQuery();
                }

                tx.Commit();
                return true;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to record activity in cache.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to record activity in cache.", ex);
        }
    }

    public virtual (bool Exists, string? Value, long ObservedAt) GetLatestActivity(string peerId, string field)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value, observed_at FROM activity_latest WHERE peer_id = @peer_id AND field = @field;";
            cmd.Parameters.AddWithValue("@peer_id", peerId);
            cmd.Parameters.AddWithValue("@field", field);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (true, reader.GetString(0), reader.GetInt64(1));
            }
            return (false, null, 0);
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to get latest activity.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to get latest activity.", ex);
        }
    }

    public virtual bool PairMessageEdited(
        long chatId,
        string peerId,
        string accountId,
        long serverMsgId,
        long editDate,
        long now)
    {
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                bool hasPending = false;
                string? pendingOldText = null;
                string? pendingNewText = null;
                bool? pendingIsOut = null;
                long? pendingMsgDate = null;
                long pendingObservedAt = 0;

                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.Transaction = tx;
                    selCmd.CommandText = @"
                        SELECT old_text, new_text, is_out, msg_date, observed_at
                        FROM pending_edits
                        WHERE chat_id = @chat_id AND message_id = @message_id;";
                    selCmd.Parameters.AddWithValue("@chat_id", chatId);
                    selCmd.Parameters.AddWithValue("@message_id", serverMsgId);

                    using var reader = selCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        hasPending = true;
                        pendingOldText = reader.IsDBNull(0) ? null : reader.GetString(0);
                        pendingNewText = reader.IsDBNull(1) ? null : reader.GetString(1);
                        pendingIsOut = reader.IsDBNull(2) ? null : reader.GetInt32(2) != 0;
                        pendingMsgDate = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                        pendingObservedAt = reader.GetInt64(4);
                    }
                }

                if (hasPending && pendingNewText != null)
                {
                    long occurredAt = editDate > 0 ? editDate : ((pendingMsgDate ?? 0) > 0 ? pendingMsgDate!.Value : now);
                    string payloadJson = PayloadBuilder.BuildEdited(
                        accountId,
                        peerId,
                        pendingOldText ?? "",
                        pendingNewText,
                        pendingIsOut ?? false);

                    using (var insCmd = conn.CreateCommand())
                    {
                        insCmd.Transaction = tx;
                        insCmd.CommandText = @"
                            INSERT OR REPLACE INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                            VALUES ('edited', @account_id, @peer_id, @msg_id, @occurred_at, @observed_at, @payload_json, @created_at);";
                        insCmd.Parameters.AddWithValue("@account_id", accountId);
                        insCmd.Parameters.AddWithValue("@peer_id", peerId);
                        insCmd.Parameters.AddWithValue("@msg_id", serverMsgId);
                        insCmd.Parameters.AddWithValue("@occurred_at", occurredAt);
                        insCmd.Parameters.AddWithValue("@observed_at", now);
                        insCmd.Parameters.AddWithValue("@payload_json", payloadJson);
                        insCmd.Parameters.AddWithValue("@created_at", now);
                        insCmd.ExecuteNonQuery();
                    }

                    using (var delCmd = conn.CreateCommand())
                    {
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM pending_edits WHERE chat_id = @chat_id AND message_id = @message_id;";
                        delCmd.Parameters.AddWithValue("@chat_id", chatId);
                        delCmd.Parameters.AddWithValue("@message_id", serverMsgId);
                        delCmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                    return true;
                }

                using (var upsertCmd = conn.CreateCommand())
                {
                    upsertCmd.Transaction = tx;
                    upsertCmd.CommandText = @"
                        INSERT INTO pending_edits (chat_id, message_id, peer_id, account_id, old_text, new_text, is_out, msg_date, edit_date, observed_at)
                        VALUES (@chat_id, @message_id, @peer_id, @account_id, NULL, NULL, NULL, NULL, @edit_date, @observed_at)
                        ON CONFLICT(chat_id, message_id) DO UPDATE SET
                            edit_date = excluded.edit_date,
                            observed_at = excluded.observed_at;";
                    upsertCmd.Parameters.AddWithValue("@chat_id", chatId);
                    upsertCmd.Parameters.AddWithValue("@message_id", serverMsgId);
                    upsertCmd.Parameters.AddWithValue("@peer_id", peerId);
                    upsertCmd.Parameters.AddWithValue("@account_id", accountId);
                    upsertCmd.Parameters.AddWithValue("@edit_date", editDate);
                    upsertCmd.Parameters.AddWithValue("@observed_at", now);
                    upsertCmd.ExecuteNonQuery();
                }

                tx.Commit();
                return false;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to pair message edited.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to pair message edited.", ex);
        }
    }

    public virtual int SweepPendingEdits(long now, int timeoutSeconds = 60)
    {
        try
        {
            long cutoff = now - timeoutSeconds;
            using var conn = OpenConnection();

            // Odatda muddati o'tgan qator yo'q: yozish qulfini (BEGIN
            // IMMEDIATE) faqat ish bo'lganda olamiz.
            using (var existsCmd = conn.CreateCommand())
            {
                existsCmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pending_edits WHERE observed_at <= @cutoff);";
                existsCmd.Parameters.AddWithValue("@cutoff", cutoff);
                if (Convert.ToInt64(existsCmd.ExecuteScalar()) == 0)
                    return 0;
            }

            using var tx = conn.BeginTransaction();

            int emittedCount = 0;
            try
            {
                var expiredRows = new List<(long ChatId, long MessageId, string PeerId, string AccountId, string OldText, string? NewText, bool IsOut, long MsgDate, long? EditDate, long ObservedAt)>();

                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.Transaction = tx;
                    selCmd.CommandText = @"
                        SELECT chat_id, message_id, peer_id, account_id, old_text, new_text, is_out, msg_date, edit_date, observed_at
                        FROM pending_edits
                        WHERE observed_at <= @cutoff;";
                    selCmd.Parameters.AddWithValue("@cutoff", cutoff);

                    using var reader = selCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        expiredRows.Add((
                            ChatId: reader.GetInt64(0),
                            MessageId: reader.GetInt64(1),
                            PeerId: reader.IsDBNull(2) ? "" : reader.GetString(2),
                            AccountId: reader.IsDBNull(3) ? "" : reader.GetString(3),
                            OldText: reader.IsDBNull(4) ? "" : reader.GetString(4),
                            NewText: reader.IsDBNull(5) ? null : reader.GetString(5),
                            IsOut: !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
                            MsgDate: reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                            EditDate: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                            ObservedAt: reader.GetInt64(9)
                        ));
                    }
                }

                foreach (var row in expiredRows)
                {
                    if (row.NewText != null)
                    {
                        long editDateVal = row.EditDate ?? 0;
                        long occurredAt = editDateVal > 0 ? editDateVal : (row.MsgDate > 0 ? row.MsgDate : row.ObservedAt);
                        string payloadJson = PayloadBuilder.BuildEdited(
                            row.AccountId,
                            row.PeerId,
                            row.OldText,
                            row.NewText,
                            row.IsOut);

                        using (var insCmd = conn.CreateCommand())
                        {
                            insCmd.Transaction = tx;
                            insCmd.CommandText = @"
                                INSERT OR REPLACE INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                                VALUES ('edited', @account_id, @peer_id, @msg_id, @occurred_at, @observed_at, @payload_json, @created_at);";
                            insCmd.Parameters.AddWithValue("@account_id", row.AccountId);
                            insCmd.Parameters.AddWithValue("@peer_id", row.PeerId);
                            insCmd.Parameters.AddWithValue("@msg_id", row.MessageId);
                            insCmd.Parameters.AddWithValue("@occurred_at", occurredAt);
                            insCmd.Parameters.AddWithValue("@observed_at", now);
                            insCmd.Parameters.AddWithValue("@payload_json", payloadJson);
                            insCmd.Parameters.AddWithValue("@created_at", now);
                            insCmd.ExecuteNonQuery();
                        }

                        emittedCount++;
                    }

                    using (var delCmd = conn.CreateCommand())
                    {
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM pending_edits WHERE chat_id = @chat_id AND message_id = @message_id;";
                        delCmd.Parameters.AddWithValue("@chat_id", row.ChatId);
                        delCmd.Parameters.AddWithValue("@message_id", row.MessageId);
                        delCmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
                return emittedCount;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to sweep pending edits.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to sweep pending edits.", ex);
        }
    }

    /// <summary>
    /// Berilgan xabarning kutayotgan tahririni (matni bo'lsa) `edited`
    /// yozuvi sifatida chiqaradi va pending qatorni o'chiradi. Chaqiruvchi
    /// tranzaksiyasi ichida ishlaydi.
    /// </summary>
    private static void FlushPendingEdit(SqliteConnection conn, SqliteTransaction tx, long chatId, long messageId, long now)
    {
        string? peerId = null, accountId = null, oldText = null, newText = null;
        bool isOut = false;
        long msgDate = 0, editDate = 0, observedAt = 0;
        bool found = false;

        using (var selCmd = conn.CreateCommand())
        {
            selCmd.Transaction = tx;
            selCmd.CommandText = @"
                SELECT peer_id, account_id, old_text, new_text, is_out, msg_date, edit_date, observed_at
                FROM pending_edits
                WHERE chat_id = @chat_id AND message_id = @message_id;";
            selCmd.Parameters.AddWithValue("@chat_id", chatId);
            selCmd.Parameters.AddWithValue("@message_id", messageId);

            using var reader = selCmd.ExecuteReader();
            if (reader.Read())
            {
                found = true;
                peerId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                accountId = reader.IsDBNull(1) ? "" : reader.GetString(1);
                oldText = reader.IsDBNull(2) ? "" : reader.GetString(2);
                newText = reader.IsDBNull(3) ? null : reader.GetString(3);
                isOut = !reader.IsDBNull(4) && reader.GetInt32(4) != 0;
                msgDate = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
                editDate = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
                observedAt = reader.GetInt64(7);
            }
        }

        if (!found)
            return;

        if (newText != null)
        {
            long occurredAt = editDate > 0 ? editDate : (msgDate > 0 ? msgDate : observedAt);
            using var insCmd = conn.CreateCommand();
            insCmd.Transaction = tx;
            insCmd.CommandText = @"
                INSERT OR REPLACE INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES ('edited', @account_id, @peer_id, @msg_id, @occurred_at, @observed_at, @payload_json, @created_at);";
            insCmd.Parameters.AddWithValue("@account_id", accountId!);
            insCmd.Parameters.AddWithValue("@peer_id", peerId!);
            insCmd.Parameters.AddWithValue("@msg_id", messageId);
            insCmd.Parameters.AddWithValue("@occurred_at", occurredAt);
            insCmd.Parameters.AddWithValue("@observed_at", now);
            insCmd.Parameters.AddWithValue("@payload_json", PayloadBuilder.BuildEdited(accountId!, peerId!, oldText!, newText, isOut));
            insCmd.Parameters.AddWithValue("@created_at", now);
            insCmd.ExecuteNonQuery();
        }

        using var delCmd = conn.CreateCommand();
        delCmd.Transaction = tx;
        delCmd.CommandText = "DELETE FROM pending_edits WHERE chat_id = @chat_id AND message_id = @message_id;";
        delCmd.Parameters.AddWithValue("@chat_id", chatId);
        delCmd.Parameters.AddWithValue("@message_id", messageId);
        delCmd.ExecuteNonQuery();
    }

    private static CachedMessage ReadRow(SqliteDataReader reader)
    {
        long chatId = reader.GetInt64(0);
        long messageId = reader.GetInt64(1);
        string? text = reader.IsDBNull(2) ? null : reader.GetString(2);
        string? senderId = reader.IsDBNull(3) ? null : reader.GetString(3);
        bool isOut = reader.GetInt32(4) != 0;
        bool isMedia = reader.GetInt32(5) != 0;
        string? mediaId = reader.IsDBNull(6) ? null : reader.GetString(6);
        long date = reader.GetInt64(7);
        long cachedAt = reader.GetInt64(8);

        return new CachedMessage(
            ChatId: chatId,
            MessageId: messageId,
            Text: text,
            SenderId: senderId,
            IsOut: isOut,
            IsMedia: isMedia,
            MediaId: mediaId,
            Date: date,
            CachedAt: cachedAt);
    }

    public virtual long GetPullCursor(string name = "pull_cursor")
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM sync_state WHERE name = @name;";
            cmd.Parameters.AddWithValue("@name", name);
            var result = cmd.ExecuteScalar();
            if (result != null && long.TryParse(result.ToString(), out long cursor))
            {
                return cursor;
            }
            return 0;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to read pull cursor from sync_state.", ex);
        }
    }

    public virtual bool MergeSyncedSettingsAndCommitCursor(
        IReadOnlyList<SyncedSettingRow> candidates,
        long newCursor,
        Action? onBeforeCommit = null)
    {
        try
        {
            using var conn = OpenConnection();
            using (var beginCmd = conn.CreateCommand())
            {
                beginCmd.CommandText = "BEGIN IMMEDIATE;";
                beginCmd.ExecuteNonQuery();
            }

            try
            {
                bool anyChanged = false;

                foreach (var candidate in candidates)
                {
                    long? existingOccurredAt = null;
                    string? existingRecordId = null;
                    string? existingValue = null;

                    using (var selCmd = conn.CreateCommand())
                    {
                        selCmd.CommandText = "SELECT occurred_at, record_id, value FROM synced_settings WHERE key = @key;";
                        selCmd.Parameters.AddWithValue("@key", candidate.Key);
                        using var reader = selCmd.ExecuteReader();
                        if (reader.Read())
                        {
                            existingOccurredAt = reader.GetInt64(0);
                            existingRecordId = reader.GetString(1);
                            existingValue = reader.GetString(2);
                        }
                    }

                    bool isNewer;
                    if (!existingOccurredAt.HasValue)
                    {
                        isNewer = true;
                    }
                    else
                    {
                        // Spec §3.2.1a: greatest occurred_at wins; equal occurred_at -> greater record_id wins
                        if (candidate.OccurredAt > existingOccurredAt.Value)
                        {
                            isNewer = true;
                        }
                        else if (candidate.OccurredAt == existingOccurredAt.Value)
                        {
                            isNewer = string.CompareOrdinal(candidate.RecordId, existingRecordId) > 0;
                        }
                        else
                        {
                            isNewer = false;
                        }
                    }

                    if (isNewer)
                    {
                        using (var upsertCmd = conn.CreateCommand())
                        {
                            upsertCmd.CommandText = @"
                                INSERT INTO synced_settings (key, value, occurred_at, record_id)
                                VALUES (@key, @value, @occurred_at, @record_id)
                                ON CONFLICT (key) DO UPDATE SET
                                    value = excluded.value,
                                    occurred_at = excluded.occurred_at,
                                    record_id = excluded.record_id;";
                            upsertCmd.Parameters.AddWithValue("@key", candidate.Key);
                            upsertCmd.Parameters.AddWithValue("@value", candidate.Value);
                            upsertCmd.Parameters.AddWithValue("@occurred_at", candidate.OccurredAt);
                            upsertCmd.Parameters.AddWithValue("@record_id", candidate.RecordId);
                            upsertCmd.ExecuteNonQuery();
                        }

                        if (existingValue != candidate.Value)
                        {
                            anyChanged = true;
                        }
                    }
                }

                // Always update cursor in the SAME transaction
                using (var cursorCmd = conn.CreateCommand())
                {
                    cursorCmd.CommandText = @"
                        INSERT INTO sync_state (name, value)
                        VALUES ('pull_cursor', @cursor)
                        ON CONFLICT (name) DO UPDATE SET
                            value = excluded.value;";
                    cursorCmd.Parameters.AddWithValue("@cursor", newCursor.ToString());
                    cursorCmd.ExecuteNonQuery();
                }

                onBeforeCommit?.Invoke();

                using (var commitCmd = conn.CreateCommand())
                {
                    commitCmd.CommandText = "COMMIT;";
                    commitCmd.ExecuteNonQuery();
                }

                return anyChanged;
            }
            catch
            {
                try
                {
                    using var rollbackCmd = conn.CreateCommand();
                    rollbackCmd.CommandText = "ROLLBACK;";
                    rollbackCmd.ExecuteNonQuery();
                }
                catch { }
                throw;
            }
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to merge synced settings and commit cursor.", ex);
        }
        catch (Exception ex) when (ex is not MessageCacheException)
        {
            throw new MessageCacheException("Failed to merge synced settings and commit cursor.", ex);
        }
    }

    public virtual IReadOnlyList<SyncedSettingRow> GetAllSyncedSettings()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value, occurred_at, record_id FROM synced_settings;";
            using var reader = cmd.ExecuteReader();
            var list = new List<SyncedSettingRow>();
            while (reader.Read())
            {
                list.Add(new SyncedSettingRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3)));
            }
            return list;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to read synced settings from database.", ex);
        }
    }

    public virtual SyncedSettingRow? GetSyncedSetting(string key)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value, occurred_at, record_id FROM synced_settings WHERE key = @key;";
            cmd.Parameters.AddWithValue("@key", key);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new SyncedSettingRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3));
            }
            return null;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to read synced setting from database.", ex);
        }
    }

    public virtual bool QueueCapturedMedia(
        string peerId,
        long msgId,
        long chatId,
        long messageId,
        string contentType,
        long? size = null,
        long? nextAttemptAt = null)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO captured_media
                (peer_id, msg_id, chat_id, message_id, content_type, status, attempts, next_attempt_at, size, created_at)
                VALUES (@peer_id, @msg_id, @chat_id, @message_id, @content_type, 'pending', 0, @next_attempt_at, @size, @created_at);";
            long now = (_timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
            cmd.Parameters.AddWithValue("@peer_id", peerId);
            cmd.Parameters.AddWithValue("@msg_id", msgId);
            cmd.Parameters.AddWithValue("@chat_id", chatId);
            cmd.Parameters.AddWithValue("@message_id", messageId);
            cmd.Parameters.AddWithValue("@content_type", contentType);
            cmd.Parameters.AddWithValue("@next_attempt_at", (object?)nextAttemptAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@size", (object?)size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", now);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to queue captured media.", ex);
        }
    }

    public virtual CapturedMediaRow? GetCapturedMedia(string peerId, long msgId)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT peer_id, msg_id, chat_id, message_id, content_type, status, attempts, next_attempt_at, local_path, sha256, size, created_at
                FROM captured_media
                WHERE peer_id = @peer_id AND msg_id = @msg_id;";
            cmd.Parameters.AddWithValue("@peer_id", peerId);
            cmd.Parameters.AddWithValue("@msg_id", msgId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadCapturedMediaRow(reader);
            }
            return null;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to get captured media.", ex);
        }
    }

    public virtual CapturedMediaRow? GetNextDuePendingMedia(long now)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT peer_id, msg_id, chat_id, message_id, content_type, status, attempts, next_attempt_at, local_path, sha256, size, created_at
                FROM captured_media
                WHERE status = 'pending' AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
                ORDER BY (next_attempt_at IS NULL) DESC, next_attempt_at ASC, attempts ASC
                LIMIT 1;";
            cmd.Parameters.AddWithValue("@now", now);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadCapturedMediaRow(reader);
            }
            return null;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to get due pending media.", ex);
        }
    }

    public virtual void DeferMediaRow(string peerId, long msgId, long nextAttemptAt)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE captured_media
                SET next_attempt_at = @next_attempt_at
                WHERE peer_id = @peer_id AND msg_id = @msg_id;";
            cmd.Parameters.AddWithValue("@peer_id", peerId);
            cmd.Parameters.AddWithValue("@msg_id", msgId);
            cmd.Parameters.AddWithValue("@next_attempt_at", nextAttemptAt);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to defer media row.", ex);
        }
    }

    public virtual long GetDownloadedMediaTotalSize()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(size), 0) FROM captured_media WHERE status = 'downloaded' AND local_path IS NOT NULL;";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to calculate downloaded media size.", ex);
        }
    }

    public virtual bool IsMediaReferenced(string localPath)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM captured_media WHERE local_path = @local_path LIMIT 1;";
            cmd.Parameters.AddWithValue("@local_path", localPath);
            return cmd.ExecuteScalar() != null;
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to check if media is referenced.", ex);
        }
    }

    public virtual (int RowsPruned, int FilesDeleted) ApplyMediaRetention(
        long now,
        int retentionDays,
        CustomSync.Capture.Media.MediaStore mediaStore,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var filesToDelete = new List<string>();
        int rowsPruned = 0;
        long cutoff = now - (retentionDays * 86400L);

        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            var candidates = new List<(string PeerId, long MsgId, long ChatId, string Status, string? LocalPath, long CreatedAt)>();
            using (var readCmd = conn.CreateCommand())
            {
                readCmd.Transaction = tx;
                readCmd.CommandText = @"
                    SELECT peer_id, msg_id, chat_id, status, local_path, created_at
                    FROM captured_media;";
                using var reader = readCmd.ExecuteReader();
                while (reader.Read())
                {
                    candidates.Add((
                        reader.GetString(0),
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetInt64(5)));
                }
            }

            foreach (var row in candidates)
            {
                // Rule 4 (OVERRIDE): Media of a pending deletion is never lost.
                // A captured_media row whose deleted record is still in capture_outbox is never pruned
                // and its file is never deleted - whatever its status or age.
                bool hasPendingDeletedOutbox = false;
                using (var outboxCmd = conn.CreateCommand())
                {
                    outboxCmd.Transaction = tx;
                    outboxCmd.CommandText = @"
                        SELECT 1 FROM capture_outbox
                        WHERE kind = 'deleted' AND peer_id = @peer_id AND msg_id = @msg_id
                        LIMIT 1;";
                    outboxCmd.Parameters.AddWithValue("@peer_id", row.PeerId);
                    outboxCmd.Parameters.AddWithValue("@msg_id", row.MsgId);
                    hasPendingDeletedOutbox = outboxCmd.ExecuteScalar() != null;
                }

                if (hasPendingDeletedOutbox)
                {
                    continue; // Rule 4: untouched
                }

                // message_cache server ID'sini saqlaydi, captured_media.message_id esa
                // TDLib ID'sini (server ID << 20, faqat getMessage uchun). Xabar
                // msg_id bo'yicha qidiriladi: message_id bilan har qator "yetim"
                // bo'lib, yuklangan media keyingi yurishdayoq o'chib ketardi.
                bool isMessageCached = false;
                using (var cacheCmd = conn.CreateCommand())
                {
                    cacheCmd.Transaction = tx;
                    cacheCmd.CommandText = @"
                        SELECT 1 FROM message_cache
                        WHERE chat_id = @chat_id AND message_id = @message_id
                        LIMIT 1;";
                    cacheCmd.Parameters.AddWithValue("@chat_id", row.ChatId);
                    cacheCmd.Parameters.AddWithValue("@message_id", row.MsgId);
                    isMessageCached = cacheCmd.ExecuteScalar() != null;
                }

                bool isOrphan = !isMessageCached;

                if (row.Status is "uploaded" or "failed" or "skipped")
                {
                    // Rule 1: uploaded, failed, skipped: the file is deleted, local_path set to NULL;
                    // the row itself is deleted once created_at < now - window.
                    if (!string.IsNullOrEmpty(row.LocalPath))
                    {
                        if (mediaStore.IsManaged(row.LocalPath))
                        {
                            filesToDelete.Add(row.LocalPath);
                        }
                        using var updateCmd = conn.CreateCommand();
                        updateCmd.Transaction = tx;
                        updateCmd.CommandText = @"
                            UPDATE captured_media SET local_path = NULL
                            WHERE peer_id = @peer_id AND msg_id = @msg_id;";
                        updateCmd.Parameters.AddWithValue("@peer_id", row.PeerId);
                        updateCmd.Parameters.AddWithValue("@msg_id", row.MsgId);
                        updateCmd.ExecuteNonQuery();
                    }

                    if (row.CreatedAt < cutoff)
                    {
                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = @"
                            DELETE FROM captured_media
                            WHERE peer_id = @peer_id AND msg_id = @msg_id;";
                        delCmd.Parameters.AddWithValue("@peer_id", row.PeerId);
                        delCmd.Parameters.AddWithValue("@msg_id", row.MsgId);
                        delCmd.ExecuteNonQuery();
                        rowsPruned++;
                    }
                }
                else if (row.Status == "pending")
                {
                    // Rule 2: pending: deleted when created_at < now - window, or when orphan.
                    if (row.CreatedAt < cutoff || isOrphan)
                    {
                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = @"
                            DELETE FROM captured_media
                            WHERE peer_id = @peer_id AND msg_id = @msg_id;";
                        delCmd.Parameters.AddWithValue("@peer_id", row.PeerId);
                        delCmd.Parameters.AddWithValue("@msg_id", row.MsgId);
                        delCmd.ExecuteNonQuery();
                        rowsPruned++;
                    }
                }
                else if (row.Status == "downloaded")
                {
                    // Rule 3: downloaded and orphan: file and row deleted
                    if (isOrphan)
                    {
                        if (!string.IsNullOrEmpty(row.LocalPath) && mediaStore.IsManaged(row.LocalPath))
                        {
                            filesToDelete.Add(row.LocalPath);
                        }

                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = @"
                            DELETE FROM captured_media
                            WHERE peer_id = @peer_id AND msg_id = @msg_id;";
                        delCmd.Parameters.AddWithValue("@peer_id", row.PeerId);
                        delCmd.Parameters.AddWithValue("@msg_id", row.MsgId);
                        delCmd.ExecuteNonQuery();
                        rowsPruned++;
                    }
                }
            }

            tx.Commit();
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to apply media retention.", ex);
        }

        // Rule 1, 3: Delete files only AFTER transaction commits
        int filesDeleted = 0;
        foreach (var file in filesToDelete)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    filesDeleted++;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning("Failed to delete media file during retention ({ErrorType}).", ex.GetType().Name);
            }
        }

        return (rowsPruned, filesDeleted);
    }

    public virtual void UpdateMediaDownloaded(string peerId, long msgId, string localPath, string sha256, long size)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE captured_media
                SET status = 'downloaded', local_path = @local_path, sha256 = @sha256, size = @size
                WHERE peer_id = @peer_id AND msg_id = @msg_id;";
            cmd.Parameters.AddWithValue("@peer_id", peerId);
            cmd.Parameters.AddWithValue("@msg_id", msgId);
            cmd.Parameters.AddWithValue("@local_path", localPath);
            cmd.Parameters.AddWithValue("@sha256", sha256);
            cmd.Parameters.AddWithValue("@size", size);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to update media downloaded.", ex);
        }
    }

    public virtual void UpdateMediaSkipped(string peerId, long msgId)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE captured_media
                SET status = 'skipped'
                WHERE peer_id = @peer_id AND msg_id = @msg_id;";
            cmd.Parameters.AddWithValue("@peer_id", peerId);
            cmd.Parameters.AddWithValue("@msg_id", msgId);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to update media skipped.", ex);
        }
    }

    public virtual void UpdateMediaFailed(string peerId, long msgId, int attempts, long? nextAttemptAt)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            if (nextAttemptAt.HasValue)
            {
                cmd.CommandText = @"
                    UPDATE captured_media
                    SET status = 'pending', attempts = @attempts, next_attempt_at = @next_attempt_at
                    WHERE peer_id = @peer_id AND msg_id = @msg_id;";
                cmd.Parameters.AddWithValue("@next_attempt_at", nextAttemptAt.Value);
            }
            else
            {
                cmd.CommandText = @"
                    UPDATE captured_media
                    SET status = 'failed', attempts = @attempts, next_attempt_at = NULL
                    WHERE peer_id = @peer_id AND msg_id = @msg_id;";
            }
            cmd.Parameters.AddWithValue("@peer_id", peerId);
            cmd.Parameters.AddWithValue("@msg_id", msgId);
            cmd.Parameters.AddWithValue("@attempts", attempts);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to update media failed.", ex);
        }
    }

    public virtual void UpdateMediaUploaded(string peerId, long msgId)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE captured_media
                SET status = 'uploaded'
                WHERE peer_id = @peer_id AND msg_id = @msg_id;";
            cmd.Parameters.AddWithValue("@peer_id", peerId);
            cmd.Parameters.AddWithValue("@msg_id", msgId);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new MessageCacheException("Failed to update media uploaded.", ex);
        }
    }

    private static CapturedMediaRow ReadCapturedMediaRow(SqliteDataReader reader)
    {
        return new CapturedMediaRow(
            PeerId: reader.GetString(0),
            MsgId: reader.GetInt64(1),
            ChatId: reader.GetInt64(2),
            MessageId: reader.GetInt64(3),
            ContentType: reader.GetString(4),
            Status: reader.GetString(5),
            Attempts: reader.GetInt32(6),
            NextAttemptAt: reader.IsDBNull(7) ? null : reader.GetInt64(7),
            LocalPath: reader.IsDBNull(8) ? null : reader.GetString(8),
            Sha256: reader.IsDBNull(9) ? null : reader.GetString(9),
            Size: reader.IsDBNull(10) ? null : reader.GetInt64(10),
            CreatedAt: reader.GetInt64(11));
    }
}

public record SyncedSettingRow(string Key, string Value, long OccurredAt, string RecordId);

public record CapturedMediaRow(
    string PeerId,
    long MsgId,
    long ChatId,
    long MessageId,
    string ContentType,
    string Status,
    int Attempts,
    long? NextAttemptAt,
    string? LocalPath,
    string? Sha256,
    long? Size,
    long CreatedAt);
