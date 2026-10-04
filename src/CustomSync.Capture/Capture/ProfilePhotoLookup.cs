using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Capture;

public class ProfilePhotoLookup
{
    private ITdClient? _client;
    private readonly MessageCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProfilePhotoLookup>? _logger;
    private string? _defaultAccountId;

    private readonly Channel<PhotoLookupRequest> _channel;
    private readonly ConcurrentDictionary<(string PeerId, string PhotoId), bool> _inFlight = new();
    private long _droppedCount;
    private long _processedCount;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public const int DefaultQueueCapacity = 1000;

    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public long ProcessedCount => Interlocked.Read(ref _processedCount);
    public int InFlightCount => _inFlight.Count;

    public record PhotoLookupRequest(string AccountId, string PeerId, string PhotoId);

    public ProfilePhotoLookup(
        ITdClient? client,
        MessageCache cache,
        TimeProvider? timeProvider = null,
        ILogger<ProfilePhotoLookup>? logger = null,
        int queueCapacity = DefaultQueueCapacity,
        string? defaultAccountId = null)
    {
        _client = client;
        _cache = cache;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _defaultAccountId = defaultAccountId;

        var options = new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        };
        _channel = Channel.CreateBounded<PhotoLookupRequest>(options);
    }

    public ProfilePhotoLookup(
        ITdClient client,
        MessageCache cache,
        TimeProvider? timeProvider,
        int queueCapacity)
        : this(client, cache, timeProvider, logger: null, queueCapacity: queueCapacity)
    {
    }

    public void SetClient(ITdClient client) => _client = client;

    public void SetAccountId(string accountId) => _defaultAccountId = accountId;

    public virtual bool Enqueue(string peerId, string photoId, string? accountId = null)
    {
        if (string.IsNullOrEmpty(peerId) || string.IsNullOrEmpty(photoId) || photoId == "empty" || photoId == "0")
            return false;

        var key = (peerId, photoId);
        if (!_inFlight.TryAdd(key, true))
        {
            // Already in flight for (peer, photoId) -> deduplicated
            return false;
        }

        var req = new PhotoLookupRequest(accountId ?? _defaultAccountId ?? "", peerId, photoId);
        if (!_channel.Writer.TryWrite(req))
        {
            // Queue full -> drop and increment dropped count
            _inFlight.TryRemove(key, out _);
            Interlocked.Increment(ref _droppedCount);
            _logger?.LogWarning("Profile photo lookup queue is full. Dropped lookup for peer {PeerId}, photo {PhotoId}", peerId, photoId);
            return false;
        }

        return true;
    }

    public virtual void Start(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(async () =>
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_cts.Token))
                {
                    while (_channel.Reader.TryRead(out var item))
                    {
                        await ProcessItemAsync(item, _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Profile photo lookup loop failed.");
            }
        }, _cts.Token);
    }

    public virtual async Task<bool> ProcessPendingOnceAsync(CancellationToken ct = default)
    {
        bool any = false;
        while (_channel.Reader.TryRead(out var item))
        {
            any = true;
            await ProcessItemAsync(item, ct);
        }
        return any;
    }

    private async Task ProcessItemAsync(PhotoLookupRequest item, CancellationToken ct)
    {
        try
        {
            // 1. Current photo in cache check:
            var latest = _cache.GetLatestActivity(item.PeerId, "photo");
            if (latest.Exists && latest.Value != item.PhotoId)
            {
                // Photo has already changed to another id, skip
                return;
            }

            if (_client is null)
                return;

            if (!long.TryParse(item.PeerId, NumberStyles.None, CultureInfo.InvariantCulture, out long userId) || userId <= 0)
                return;

            var requestObj = new JsonObject
            {
                ["@type"] = "getUserProfilePhotos",
                ["user_id"] = userId,
                ["offset"] = 0,
                ["limit"] = 1
            };

            string responseJson;
            try
            {
                responseJson = await _client.SendAsync(requestObj.ToJsonString(), ct: ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("TDLib getUserProfilePhotos request failed for peer {PeerId}: {Error}", item.PeerId, ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(responseJson))
                return;

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("@type", out var typeProp))
                return;

            string? type = typeProp.GetString();
            if (type == "error")
                return;

            if (type != "chatPhotos" && type != "userProfilePhotos")
                return;

            if (!root.TryGetProperty("photos", out var photosArr) || photosArr.ValueKind != JsonValueKind.Array || photosArr.GetArrayLength() == 0)
                return;

            var firstPhoto = photosArr[0];
            if (!firstPhoto.TryGetProperty("id", out var idElem) || !firstPhoto.TryGetProperty("added_date", out var dateElem))
                return;

            // Extract photo ID
            string responsePhotoId = idElem.ValueKind switch
            {
                JsonValueKind.Number => unchecked((ulong)idElem.GetInt64()).ToString(CultureInfo.InvariantCulture),
                JsonValueKind.String => ulong.TryParse(idElem.GetString(), out ulong u) ? u.ToString(CultureInfo.InvariantCulture) : (idElem.GetString() ?? ""),
                _ => ""
            };

            // Rule C: Photo ID mismatch -> skip
            if (!string.Equals(responsePhotoId, item.PhotoId, StringComparison.Ordinal))
                return;

            long addedDate = dateElem.GetInt64();
            long now = (_timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();

            // Rule A: added_date > now + 60 -> skip
            if (addedDate > now + 60)
                return;

            // Rule B: now - added_date > 2592000 (30 days) -> skip
            if (now - addedDate > 2592000)
                return;

            // Rule F: existing entry at added_date -> skip
            if (_cache.HasActivityEntryAt(item.PeerId, "status", addedDate))
                return;

            // Rule D: check once again that current photo in cache is still item.PhotoId
            var current = _cache.GetLatestActivity(item.PeerId, "photo");
            if (current.Exists && current.Value != item.PhotoId)
                return;

            // Write status online:addedDate moment
            string effectiveAccountId = !string.IsNullOrEmpty(item.AccountId) ? item.AccountId : (_defaultAccountId ?? "");
            _cache.RecordActivityMoment(effectiveAccountId, item.PeerId, "status", $"online:{addedDate}", addedDate);
            Interlocked.Increment(ref _processedCount);
        }
        finally
        {
            _inFlight.TryRemove((item.PeerId, item.PhotoId), out _);
        }
    }
}
