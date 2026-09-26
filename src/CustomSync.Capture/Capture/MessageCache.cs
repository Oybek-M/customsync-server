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
        _connectionString = $"Data Source={databasePath};Default Timeout=5;";
        _timeProvider = timeProvider;
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
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
