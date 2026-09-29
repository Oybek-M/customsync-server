using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Capture;

/// <summary>
/// Processes TDLib updates (updateNewMessage, updateDeleteMessages, updateMessageContent,
/// updateUser, updateUserStatus) sequentially in exact arrival order, caching in-scope
/// messages and emitting durable events to capture_outbox.
/// </summary>
public class CaptureUpdateHandler
{
    private readonly MessageCache _cache;
    private readonly ICaptureScope _scope;
    private readonly IActivityScope _activityScope;
    private readonly ConcurrentDictionary<string, bool> _contactMap = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CaptureUpdateHandler>? _logger;
    private readonly object _lock = new();
    private readonly Queue<string> _earlyQueue = new();
    private string? _accountId;
    private ITdClient? _client;

    private long _uncachedDeleteCount;
    private long _uncachedEditCount;
    private long _unpairedEditCount;
    private long _errorCount;
    private readonly int _editPairingTimeoutSeconds;

    public string? AccountId => _accountId;
    public long UncachedDeleteCount => Interlocked.Read(ref _uncachedDeleteCount);
    public long UncachedEditCount => Interlocked.Read(ref _uncachedEditCount);
    public long UnpairedEditCount => Interlocked.Read(ref _unpairedEditCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public CaptureUpdateHandler(
        MessageCache cache,
        ICaptureScope? scope,
        TimeProvider? timeProvider = null,
        ILogger<CaptureUpdateHandler>? logger = null,
        string? accountId = null,
        int editPairingTimeoutSeconds = 60)
        : this(cache, scope, activityScope: null, timeProvider, logger, accountId, editPairingTimeoutSeconds)
    {
    }

    public CaptureUpdateHandler(
        MessageCache cache,
        ICaptureScope? scope,
        IActivityScope? activityScope,
        TimeProvider? timeProvider = null,
        ILogger<CaptureUpdateHandler>? logger = null,
        string? accountId = null,
        int editPairingTimeoutSeconds = 60)
    {
        _cache = cache;
        // Scope berilmasa — hech narsa ushlanmaydi (fail-closed). null'ni
        // "cheklov yo'q" deb o'qish maxfiylik boshqaruvini teskari qiladi.
        _scope = scope ?? new NoneCaptureScope();
        _activityScope = activityScope ?? new NoneActivityScope();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _accountId = accountId;
        _editPairingTimeoutSeconds = editPairingTimeoutSeconds > 0 ? editPairingTimeoutSeconds : 60;
    }

    /// <summary>
    /// Handler'ni mijozga ulaydi: update'larga obuna bo'ladi va keshda
    /// yo'q xabar yoki noma'lum account_id uchun TDLib'dan so'rov yuboradi.
    /// Production'da buni AddCaptureHandlers ITdClient yaratilganda chaqiradi.
    /// </summary>
    public void Attach(ITdClient client)
    {
        _client = client;
        client.UpdateReceived += HandleUpdate;
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

                // Sweep pending edits on each handled update using TimeProvider
                long sweepNow = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
                int swept = _cache.SweepPendingEdits(sweepNow, _editPairingTimeoutSeconds);
                if (swept > 0)
                {
                    Interlocked.Add(ref _unpairedEditCount, swept);
                }

                if (updateType == "updateOption")
                {
                    ProcessUpdateOption(root);
                    return;
                }

                if (updateType == "updateAuthorizationState")
                {
                    ProcessAuthorizationState(root);
                    return;
                }

                if (updateType == "updateUser" && root.TryGetProperty("user", out var userObj) && userObj.ValueKind == JsonValueKind.Object)
                {
                    if (userObj.TryGetProperty("id", out var idElem) && userObj.TryGetProperty("is_contact", out var cElem))
                    {
                        var pId = idElem.ValueKind == JsonValueKind.Number ? idElem.GetInt64().ToString(CultureInfo.InvariantCulture) : idElem.GetString();
                        if (!string.IsNullOrEmpty(pId))
                        {
                            _contactMap[pId] = cElem.GetBoolean();
                        }
                    }
                }

                if (string.IsNullOrEmpty(_accountId))
                {
                    // Faqat ushlanadigan turlar buferlanadi: TDLib ishga
                    // tushishda minglab updateUser/updateChat* yuboradi.
                    if (IsCapturedType(updateType))
                    {
                        _earlyQueue.Enqueue(rawJson);
                    }
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

    private static bool IsCapturedType(string? type) =>
        type is "updateNewMessage" or "updateDeleteMessages" or "updateMessageContent" or "updateMessageEdited" or "updateUser" or "updateUserStatus";

    // Zaxira yo'l: my_id odatda updateOption bilan keladi, lekin u kelmasa
    // yoki obunadan oldin kelib qolsa, bufer cheksiz o'sadi va hech narsa
    // yozilmaydi. Avtorizatsiya tayyor bo'lganda getMe bilan so'raymiz.
    private void ProcessAuthorizationState(JsonElement root)
    {
        if (!string.IsNullOrEmpty(_accountId) || _client is null)
            return;

        if (!root.TryGetProperty("authorization_state", out var state)
            || !state.TryGetProperty("@type", out var stateType)
            || stateType.GetString() != "authorizationStateReady")
            return;

        _ = FetchAccountIdAsync(_client);
    }

    private async Task FetchAccountIdAsync(ITdClient client)
    {
        try
        {
            var response = await client.SendAsync("{\"@type\":\"getMe\"}");
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.TryGetProperty("@type", out var t) && t.GetString() == "user"
                && root.TryGetProperty("id", out var idProp))
            {
                var id = idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : idProp.GetInt64().ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(id))
                {
                    lock (_lock)
                    {
                        if (string.IsNullOrEmpty(_accountId))
                        {
                            SetAccountId(id);
                        }
                    }
                    return;
                }
            }

            Interlocked.Increment(ref _errorCount);
            _logger?.LogError("getMe did not return a user; account_id is still unknown and captured updates stay buffered.");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger?.LogError("getMe failed ({ErrorType}); account_id is still unknown and captured updates stay buffered.",
                ex.GetType().Name);
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
            case "updateMessageEdited":
                HandleMessageEdited(root);
                break;
            case "updateUser":
                HandleUpdateUser(root);
                break;
            case "updateUserStatus":
                HandleUpdateUserStatus(root);
                break;
        }
    }

    private void HandleNewMessage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return;

        CacheMessage(msg, addOnly: false);
    }

    // addOnly: getMessage javobi uchun — bor qator ustiga yozilmaydi.
    private void CacheMessage(JsonElement msg, bool addOnly)
    {
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
        if (!_scope.ShouldCache(peerId))
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

        if (addOnly)
        {
            _cache.TryAdd(cached);
        }
        else
        {
            _cache.Put(cached);
        }
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
        if (!_scope.ShouldAntiDelete(peerId))
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

        // Consult scope: if not even cached, ignore completely (no cache update, no baseline fetch, no outbox)
        if (!_scope.ShouldCache(peerId))
            return;

        bool shouldAntiEdit = _scope.ShouldAntiEdit(peerId);

        if (!root.TryGetProperty("new_content", out var newContent))
            return;

        (string newText, _) = ExtractTextAndMedia(newContent);

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var result = _cache.UpdateMessageContent(chatId, peerId, _accountId!, serverMsgId.Value, newText, now, emitOutbox: shouldAntiEdit);
        if (result == EditResult.NotCached)
        {
            // Hodisa yozilmaydi ("oldin" matni noma'lum). Keyingi tahrir
            // uchun asos kerak — to'liq xabarni (sana, yuboruvchi, is_out)
            // TDLib'dan so'raymiz; qo'lda to'qilgan qator yozilmaydi.
            Interlocked.Increment(ref _uncachedEditCount);
            if (_client is not null)
            {
                _ = FetchBaselineAsync(_client, chatId, tdlibId);
            }
        }
    }

    private async Task FetchBaselineAsync(ITdClient client, long chatId, long tdlibId)
    {
        try
        {
            var response = await client.SendAsync(string.Create(CultureInfo.InvariantCulture,
                $"{{\"@type\":\"getMessage\",\"chat_id\":{chatId},\"message_id\":{tdlibId}}}"));
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (!root.TryGetProperty("@type", out var t) || t.GetString() != "message")
                return;

            lock (_lock)
            {
                CacheMessage(root, addOnly: true);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger?.LogError("getMessage for edit baseline failed ({ErrorType}) chat {ChatId} message {MessageId}",
                ex.GetType().Name, chatId, tdlibId);
        }
    }

    private void HandleMessageEdited(JsonElement root)
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

        if (!_scope.ShouldCache(peerId))
            return;

        if (!_scope.ShouldAntiEdit(peerId))
            return;

        long editDate = root.TryGetProperty("edit_date", out var editProp) ? editProp.GetInt64() : 0;
        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        _cache.PairMessageEdited(chatId, peerId, _accountId!, serverMsgId.Value, editDate, now);
    }

    private void HandleUpdateUser(JsonElement root)
    {
        if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            return;

        if (!user.TryGetProperty("id", out var idProp))
            return;

        string peerId = idProp.ValueKind switch
        {
            JsonValueKind.Number => idProp.GetInt64().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => idProp.GetString() ?? "",
            _ => ""
        };

        if (string.IsNullOrEmpty(peerId))
            return;

        bool isContact = false;
        if (user.TryGetProperty("is_contact", out var contactProp))
        {
            isContact = contactProp.GetBoolean();
            _contactMap[peerId] = isContact;
        }
        else if (_contactMap.TryGetValue(peerId, out var existingContact))
        {
            isContact = existingContact;
        }

        if (!_activityScope.ShouldTrackActivity(peerId, isContact))
            return;

        if (string.IsNullOrEmpty(_accountId))
            return;

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        // 1. Name
        string? firstName = user.TryGetProperty("first_name", out var fnProp) ? fnProp.GetString() : null;
        string? lastName = user.TryGetProperty("last_name", out var lnProp) ? lnProp.GetString() : null;
        string fullName = ActivityMapper.MapName(firstName, lastName);
        _cache.RecordActivity(_accountId, peerId, "name", fullName, now);

        // 2. Username
        if (user.TryGetProperty("usernames", out var usernamesProp) && usernamesProp.ValueKind == JsonValueKind.Object)
        {
            string username = ActivityMapper.MapUsername(usernamesProp);
            _cache.RecordActivity(_accountId, peerId, "username", username, now);
        }

        // 3. Status
        if (user.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.Object)
        {
            string status = ActivityMapper.MapStatus(statusProp, now);
            _cache.RecordActivity(_accountId, peerId, "status", status, now);
        }
    }

    private void HandleUpdateUserStatus(JsonElement root)
    {
        if (!root.TryGetProperty("user_id", out var idProp))
            return;

        string peerId = idProp.ValueKind switch
        {
            JsonValueKind.Number => idProp.GetInt64().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => idProp.GetString() ?? "",
            _ => ""
        };

        if (string.IsNullOrEmpty(peerId))
            return;

        bool isContact = _contactMap.TryGetValue(peerId, out var c) && c;

        if (!_activityScope.ShouldTrackActivity(peerId, isContact))
            return;

        if (string.IsNullOrEmpty(_accountId))
            return;

        if (!root.TryGetProperty("status", out var statusProp) || statusProp.ValueKind != JsonValueKind.Object)
            return;

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        string status = ActivityMapper.MapStatus(statusProp, now);
        _cache.RecordActivity(_accountId, peerId, "status", status, now);
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
