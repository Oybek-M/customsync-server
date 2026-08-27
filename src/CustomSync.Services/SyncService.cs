using CustomSync.Core.Contracts;
using CustomSync.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CustomSync.Services;

/// <summary>
/// Sync yadrosi. Raw SQL ishlatiladi, chunki bu yerdagi ikki narsani
/// EF Core'ning LINQ qatlamida ifodalab bo'lmaydi: bitta tranzaksiya
/// ichida cursor ajratish va shartli upsert (ON CONFLICT ... WHERE).
/// </summary>
public class SyncService(SyncDbContext db)
{
    /// <summary>
    /// MUHIM: cursor BIGSERIAL bilan berilmaydi.
    ///
    /// BIGSERIAL da ikki parallel tranzaksiya seq oladi (A=5, B=6) va B
    /// avval commit qilishi mumkin. Shu payt pull qilgan klient cursor'ini
    /// 6 ga surib qo'yadi, keyin A commit bo'ladi — va 5-yozuv hech qachon
    /// olinmaydi. Yozuv jimgina yo'qoladi.
    ///
    /// sync_counter qatorini UPDATE qilish qator lockini commit'gacha
    /// ushlaydi, shuning uchun seq tartibi = commit tartibi.
    ///
    /// Dublikat push'da seq behuda sarflanadi — bu ataylab qabul qilingan.
    /// Cursor faqat monotonlikni talab qiladi, zichlikni emas, shuning
    /// uchun bo'shliqlar zararsiz.
    /// </summary>
    private const string UpsertSql = """
        WITH allocated AS (
          UPDATE sync_counter SET value = value + 1 WHERE id = 1 RETURNING value
        )
        INSERT INTO records (
            record_id, seq, kind, account_hash, peer_hash, msg_id, occurred_at,
            observed_at, device_id, nonce, payload, payload_size, received_at)
        SELECT @record_id, allocated.value, @kind, @account_hash, @peer_hash, @msg_id,
               @occurred_at, @observed_at, @device_id, @nonce, @payload,
               @payload_size, now()
        FROM allocated
        ON CONFLICT (record_id) DO UPDATE SET
            seq          = EXCLUDED.seq,
            observed_at  = EXCLUDED.observed_at,
            device_id    = EXCLUDED.device_id,
            nonce        = EXCLUDED.nonce,
            payload      = EXCLUDED.payload,
            payload_size = EXCLUDED.payload_size,
            received_at  = EXCLUDED.received_at
        WHERE records.observed_at > EXCLUDED.observed_at
           OR (records.observed_at = EXCLUDED.observed_at
               AND records.device_id > EXCLUDED.device_id)
        RETURNING seq, (xmax::text = '0') AS was_insert;
        """;

    public async Task<IReadOnlyList<PushResult>> PushAsync(
        string deviceId,
        IReadOnlyList<SyncRecord> records,
        CancellationToken ct = default)
    {
        var results = new List<PushResult>(records.Count);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        foreach (var record in records)
        {
            if (!RecordKind.IsValid(record.Kind))
            {
                results.Add(new PushResult(
                    record.RecordId, PushOutcome.Error, Message: "unknown_kind"));
                continue;
            }

            var expected = Core.RecordId.Compute(
                record.Kind, record.AccountHash, record.PeerHash, record.MsgId, record.OccurredAt);
            if (!string.Equals(expected, record.RecordId, StringComparison.Ordinal))
            {
                // Klient record_id ni noto'g'ri hisoblagan — bu interop bug'i.
                // Qabul qilsak dedup butunlay buziladi, shuning uchun rad etamiz.
                results.Add(new PushResult(
                    record.RecordId, PushOutcome.Error, Message: "record_id_mismatch"));
                continue;
            }

            await using var cmd = new NpgsqlCommand(UpsertSql, connection);
            cmd.Parameters.AddWithValue("record_id",    record.RecordId);
            cmd.Parameters.AddWithValue("kind",         record.Kind);
            cmd.Parameters.AddWithValue("account_hash", record.AccountHash);
            cmd.Parameters.AddWithValue("peer_hash",    record.PeerHash);
            cmd.Parameters.AddWithValue("msg_id",       record.MsgId);
            cmd.Parameters.AddWithValue("occurred_at",  record.OccurredAt);
            cmd.Parameters.AddWithValue("observed_at",  record.ObservedAt);
            cmd.Parameters.AddWithValue("device_id",    deviceId);
            cmd.Parameters.Add("nonce",   NpgsqlDbType.Bytea).Value = record.Nonce;
            cmd.Parameters.Add("payload", NpgsqlDbType.Bytea).Value = record.Payload;
            // Hajm serverda hisoblanadi — klientga ishonilmaydi.
            cmd.Parameters.AddWithValue("payload_size", record.Payload.Length);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var seq = reader.GetInt64(0);
                var wasInsert = reader.GetBoolean(1);
                results.Add(new PushResult(
                    record.RecordId,
                    wasInsert ? PushOutcome.Created : PushOutcome.Superseded,
                    seq));
            }
            else
            {
                // Qator qaytmadi = ON CONFLICT DO UPDATE ning WHERE sharti
                // bajarilmadi = mavjud yozuv yaxshiroq. Klient buni
                // muvaffaqiyat deb hisoblaydi va outbox'dan o'chiradi.
                results.Add(new PushResult(record.RecordId, PushOutcome.Duplicate));
            }
        }

        return results;
    }

    public async Task<PullResponse> PullAsync(
        long since, int limit, CancellationToken ct = default)
    {
        var rows = await db.Records.AsNoTracking()
            .Where(r => r.Seq > since)
            .OrderBy(r => r.Seq)
            .Take(limit)
            .Select(r => new StoredRecord
            {
                Seq         = r.Seq,
                RecordId    = r.RecordId,
                Kind        = r.Kind,
                AccountHash = r.AccountHash,
                PeerHash    = r.PeerHash,
                MsgId       = r.MsgId,
                OccurredAt  = r.OccurredAt,
                ObservedAt  = r.ObservedAt,
                DeviceId    = r.DeviceId,
                Nonce       = r.Nonce,
                Payload     = r.Payload
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return new PullResponse { Records = rows, NextSince = since, HasMore = false };

        var ids = rows.Select(r => r.RecordId).ToList();
        var links = await db.RecordMedia.AsNoTracking()
            .Where(m => ids.Contains(m.RecordId))
            .ToListAsync(ct);

        var withMedia = rows.Select(r => r with
        {
            MediaHashes = links.Where(l => l.RecordId == r.RecordId)
                               .Select(l => l.Hash).ToList()
        }).ToList();

        var next = withMedia[^1].Seq;
        return new PullResponse
        {
            Records   = withMedia,
            NextSince = next,
            HasMore   = withMedia.Count == limit
        };
    }
}
