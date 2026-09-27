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
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
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
            ";
            cmd.ExecuteNonQuery();

            using var versionCmd = conn.CreateCommand();
            versionCmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(versionCmd.ExecuteScalar());
            if (version == 0)
            {
                using var setVersionCmd = conn.CreateCommand();
                setVersionCmd.CommandText = "PRAGMA user_version = 1;";
                setVersionCmd.ExecuteNonQuery();
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

                if (emitOutbox)
                {
                    long occurredAt = cached.Date > 0 ? cached.Date : now;
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
                    SELECT id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at
                    FROM capture_outbox
                    ORDER BY id ASC;";
            }
            else
            {
                cmd.CommandText = @"
                    SELECT id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at
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
                    CreatedAt: reader.GetInt64(8)));
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
}
