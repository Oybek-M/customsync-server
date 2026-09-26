using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Capture;

/// <summary>
/// Processes TDLib updates (updateNewMessage, updateDeleteMessages, updateMessageContent)
/// sequentially in exact arrival order, caching in-scope messages and emitting durable
/// events to capture_outbox.
/// </summary>
public class CaptureUpdateHandler
{
    private readonly MessageCache _cache;
    private readonly ICaptureScope _scope;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CaptureUpdateHandler>? _logger;
    private readonly object _lock = new();
    private readonly Queue<string> _earlyQueue = new();
    private string? _accountId;

    private long _uncachedDeleteCount;
    private long _uncachedEditCount;
    private long _errorCount;

    public string? AccountId => _accountId;
    public long UncachedDeleteCount => Interlocked.Read(ref _uncachedDeleteCount);
    public long UncachedEditCount => Interlocked.Read(ref _uncachedEditCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public CaptureUpdateHandler(
        MessageCache cache,
        ICaptureScope scope,
        TimeProvider? timeProvider = null,
        ILogger<CaptureUpdateHandler>? logger = null,
        string? accountId = null)
    {
        _cache = cache;
        _scope = scope;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _accountId = accountId;
    }

    public void SetAccountId(string accountId)
    {
        lock (_lock)
        {
            _accountId = accountId;
            while (_earlyQueue.Count > 0)
            {
                var queuedRaw = _earlyQueue.Dequeue();
                try
                {
                    using var doc = JsonDocument.Parse(queuedRaw);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("@type", out var t) ? t.GetString() : null;
                    ProcessUpdateElement(root, type);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _errorCount);
                    _logger?.LogError(ex, "Error processing buffered early TDLib update");
                }
            }
        }
    }

    public void HandleUpdate(string rawJson)
    {
        lock (_lock)
        {
            string? updateType = null;
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;
                updateType = root.TryGetProperty("@type", out var t) ? t.GetString() : null;

                if (updateType == "updateOption")
                {
                    ProcessUpdateOption(root);
                    return;
                }

                if (string.IsNullOrEmpty(_accountId))
                {
                    _earlyQueue.Enqueue(rawJson);
                    return;
                }

                ProcessUpdateElement(root, updateType);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                _logger?.LogError(ex, "Error processing TDLib update of type {UpdateType}", updateType ?? "unknown");
            }
        }
    }

    private void ProcessUpdateOption(JsonElement root)
    {
        if (root.TryGetProperty("name", out var nameProp) && nameProp.GetString() == "my_id")
        {
            if (root.TryGetProperty("value", out var valProp) && valProp.ValueKind == JsonValueKind.Object)
            {
                if (valProp.TryGetProperty("value", out var innerVal))
                {
                    string? myId = innerVal.ValueKind switch
                    {
                        JsonValueKind.Number => innerVal.GetInt64().ToString(CultureInfo.InvariantCulture),
                        JsonValueKind.String => innerVal.GetString(),
                        _ => null
                    };

                    if (!string.IsNullOrEmpty(myId))
                    {
                        SetAccountId(myId);
                    }
                }
            }
        }
    }

    private void ProcessUpdateElement(JsonElement root, string? type)
    {
        switch (type)
        {
            case "updateNewMessage":
                HandleNewMessage(root);
                break;
            case "updateDeleteMessages":
                HandleDeleteMessages(root);
                break;
            case "updateMessageContent":
                HandleMessageContent(root);
                break;
        }
    }

    private void HandleNewMessage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return;

        if (!msg.TryGetProperty("chat_id", out var chatProp) || !msg.TryGetProperty("id", out var idProp))
            return;

        long chatId = chatProp.GetInt64();
        long tdlibId = idProp.GetInt64();

        var peerId = TdIdMapper.ToPeerId(chatId);
        if (peerId is null)
            return;

        var serverMsgId = TdIdMapper.ToServerMessageId(tdlibId);
        if (serverMsgId is null)
            return;

        // Consult scope BEFORE caching
        if (!_scope.ShouldCapture(peerId))
            return;

        (string text, bool isMedia) = msg.TryGetProperty("content", out var contentProp)
            ? ExtractTextAndMedia(contentProp)
            : ("", false);

        string? senderId = msg.TryGetProperty("sender_id", out var senderProp)
            ? TdIdMapper.ToSenderId(senderProp)
            : null;

        bool isOut = msg.TryGetProperty("is_outgoing", out var outProp) && outProp.GetBoolean();
        long date = msg.TryGetProperty("date", out var dateProp) ? dateProp.GetInt64() : 0;

        // CachedAt left to clock (null), NOT date
        var cached = new CachedMessage(
            ChatId: chatId,
            MessageId: serverMsgId.Value,
            Text: text,
            SenderId: senderId,
            IsOut: isOut,
            IsMedia: isMedia,
            MediaId: null,
            Date: date,
            CachedAt: null);

        _cache.Put(cached);
    }

    private void HandleDeleteMessages(JsonElement root)
    {
        bool isPermanent = root.TryGetProperty("is_permanent", out var permProp) && permProp.GetBoolean();
        if (!isPermanent)
            return;

        bool fromCache = root.TryGetProperty("from_cache", out var cacheProp) && cacheProp.GetBoolean();
        if (fromCache)
            return;

        if (!root.TryGetProperty("chat_id", out var chatProp) || !root.TryGetProperty("message_ids", out var idsProp))
            return;

        long chatId = chatProp.GetInt64();
        var peerId = TdIdMapper.ToPeerId(chatId);
        if (peerId is null)
            return;

        // Consult scope at emit time
        if (!_scope.ShouldCapture(peerId))
            return;

        var serverMsgIds = new List<long>();
        if (idsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var idElem in idsProp.EnumerateArray())
            {
                long tdId = idElem.GetInt64();
                var srvId = TdIdMapper.ToServerMessageId(tdId);
                if (srvId.HasValue)
                {
                    serverMsgIds.Add(srvId.Value);
                }
            }
        }

        if (serverMsgIds.Count == 0)
            return;

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var result = _cache.DeleteMessagesAndRecordOutbox(chatId, peerId, _accountId!, serverMsgIds, now);
        Interlocked.Add(ref _uncachedDeleteCount, result.UncachedCount);
    }

    private void HandleMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("chat_id", out var chatProp) || !root.TryGetProperty("message_id", out var idProp))
            return;

        long chatId = chatProp.GetInt64();
        long tdlibId = idProp.GetInt64();

        var peerId = TdIdMapper.ToPeerId(chatId);
        if (peerId is null)
            return;

        var serverMsgId = TdIdMapper.ToServerMessageId(tdlibId);
        if (serverMsgId is null)
            return;

        // Consult scope at emit time
        if (!_scope.ShouldCapture(peerId))
            return;

        if (!root.TryGetProperty("new_content", out var newContent))
            return;

        (string newText, _) = ExtractTextAndMedia(newContent);

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var result = _cache.UpdateMessageContent(chatId, peerId, _accountId!, serverMsgId.Value, newText, now);
        if (result == EditResult.BaselineCreated)
        {
            Interlocked.Increment(ref _uncachedEditCount);
        }
    }

    public static (string Text, bool IsMedia) ExtractTextAndMedia(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            return ("", false);
        }

        string type = content.TryGetProperty("@type", out var typeProp) ? typeProp.GetString() ?? "" : "";
        bool isMedia = type != "messageText";

        string text = "";
        if (content.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.Object)
        {
            if (textProp.TryGetProperty("text", out var innerText) && innerText.ValueKind == JsonValueKind.String)
            {
                text = innerText.GetString() ?? "";
            }
        }
        else if (content.TryGetProperty("caption", out var captionProp) && captionProp.ValueKind == JsonValueKind.Object)
        {
            if (captionProp.TryGetProperty("text", out var innerCaption) && innerCaption.ValueKind == JsonValueKind.String)
            {
                text = innerCaption.GetString() ?? "";
            }
        }

        return (text, isMedia);
    }
}
