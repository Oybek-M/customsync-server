using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Tdlib;

/// <summary>
/// Gatekeeper for all outgoing TDLib requests.
/// Enforces a strict allow-list and inspects parameters before any payload reaches the transport.
/// </summary>
public static class TdRequestPolicy
{
    // The allow-list of TDLib request methods permitted in CustomSync.Capture.
    // Adding any method here is a critical privacy and security decision!
    // Methods that must NEVER be added include:
    //   - viewMessages, openChat, closeChat, openMessageContent (leaks read state / listened state)
    //   - readAllChatMentions, readAllChatReactions, readAllMessageThreadMentions, readChatList
    //   - toggleChatIsMarkedAsUnread, sendChatAction (leaks "typing..." status)
    //   - openStory (leaks story view state)
    //   - sendMessage, forwardMessages, deleteMessages, joinChat
    //   - logOut, destroy, terminateAllOtherSessions
    public static readonly IReadOnlySet<string> AllowedRequestTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "setTdlibParameters",
        "setAuthenticationPhoneNumber",
        "checkAuthenticationCode",
        "checkAuthenticationPassword",
        "getMe",
        "getMessage",
        "setOption",
        "getOption",
        // downloading a file does not mark it viewed or listened; openMessageContent / openStory stay forbidden
        "downloadFile",
        // Local operations for storage maintenance and logging; they make no request to Telegram's servers,
        // mark nothing as read or viewed and do not change the online status.
        "optimizeStorage",
        "getStorageStatisticsFast",
        "setLogVerbosityLevel"
    };

    public static JsonObject ValidateAndNormalize(string requestJson, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            logger?.LogWarning("TDLib request is empty or whitespace.");
            throw new TdRequestNotAllowedException("TDLib request cannot be empty.");
        }

        // 1. Strict duplicate key check & malformed check using Utf8JsonReader
        var bytes = System.Text.Encoding.UTF8.GetBytes(requestJson);
        var reader = new Utf8JsonReader(bytes);
        var objectDepthStack = new Stack<HashSet<string>>();
        bool isRootObject = false;
        bool hasReadFirstToken = false;

        try
        {
            while (reader.Read())
            {
                if (!hasReadFirstToken)
                {
                    hasReadFirstToken = true;
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        logger?.LogWarning("TDLib request must be a JSON object.");
                        throw new TdRequestNotAllowedException("TDLib request must be a JSON object.");
                    }
                    isRootObject = true;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objectDepthStack.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (objectDepthStack.Count > 0)
                        {
                            objectDepthStack.Pop();
                        }
                        break;
                    case JsonTokenType.PropertyName:
                        var propName = reader.GetString();
                        if (propName != null && objectDepthStack.Count > 0)
                        {
                            if (!objectDepthStack.Peek().Add(propName))
                            {
                                logger?.LogWarning("TDLib request contains duplicate property '{PropertyName}'.", propName);
                                throw new TdRequestNotAllowedException($"TDLib request contains duplicate property '{propName}'.");
                            }
                        }
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            logger?.LogWarning("Malformed TDLib request JSON.");
            throw new TdRequestNotAllowedException("Malformed TDLib request JSON.", ex);
        }

        if (!isRootObject)
        {
            logger?.LogWarning("TDLib request must be a JSON object.");
            throw new TdRequestNotAllowedException("TDLib request must be a JSON object.");
        }

        // 2. Parse JsonNode
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(requestJson);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning("Malformed TDLib request JSON.");
            throw new TdRequestNotAllowedException("Malformed TDLib request JSON.", ex);
        }

        if (node is not JsonObject obj)
        {
            logger?.LogWarning("TDLib request must be a JSON object.");
            throw new TdRequestNotAllowedException("TDLib request must be a JSON object.");
        }

        // 3. Extract and validate @type
        if (!obj.TryGetPropertyValue("@type", out var typeNode) || typeNode is null)
        {
            logger?.LogWarning("TDLib request is missing '@type'.");
            throw new TdRequestNotAllowedException("TDLib request is missing '@type'.");
        }

        if (typeNode is not JsonValue jVal || !jVal.TryGetValue<string>(out var requestType) || string.IsNullOrEmpty(requestType))
        {
            logger?.LogWarning("TDLib request '@type' must be a non-empty string.");
            throw new TdRequestNotAllowedException("TDLib request '@type' must be a non-empty string.");
        }

        if (!AllowedRequestTypes.Contains(requestType))
        {
            logger?.LogWarning("TDLib request '@type' is not allowed: {RequestType}", requestType);
            throw new TdRequestNotAllowedException($"TDLib request '@type' is not allowed: '{requestType}'.");
        }

        // 4. Special validation for setOption, getOption, downloadFile, optimizeStorage, getStorageStatisticsFast, setLogVerbosityLevel
        if (requestType == "setOption")
        {
            ValidateSetOption(obj, logger);
        }
        else if (requestType == "getOption")
        {
            ValidateGetOption(obj, logger);
        }
        else if (requestType == "downloadFile")
        {
            ValidateDownloadFile(obj, logger);
        }
        else if (requestType == "optimizeStorage")
        {
            ValidateOptimizeStorage(obj, logger);
        }
        else if (requestType == "getStorageStatisticsFast")
        {
            ValidateGetStorageStatisticsFast(obj, logger);
        }
        else if (requestType == "setLogVerbosityLevel")
        {
            ValidateSetLogVerbosityLevel(obj, logger);
        }

        return obj;
    }

    private static readonly HashSet<string> OptimizeStorageAllowedKeys = new(StringComparer.Ordinal)
    {
        "@type", "@extra", "size", "ttl", "count", "immunity_delay",
        "file_types", "chat_ids", "exclude_chat_ids", "return_deleted_file_statistics", "chat_limit"
    };

    private static readonly HashSet<string> GetStorageStatisticsFastAllowedKeys = new(StringComparer.Ordinal)
    {
        "@type", "@extra"
    };

    private static readonly HashSet<string> SetLogVerbosityLevelAllowedKeys = new(StringComparer.Ordinal)
    {
        "@type", "@extra", "new_verbosity_level"
    };

    private static void ValidateOptimizeStorage(JsonObject obj, ILogger? logger)
    {
        foreach (var property in obj)
        {
            if (!OptimizeStorageAllowedKeys.Contains(property.Key))
            {
                logger?.LogWarning("TDLib optimizeStorage request contains disallowed key '{PropertyName}'.", property.Key);
                throw new TdRequestNotAllowedException($"optimizeStorage does not allow key '{property.Key}'.");
            }
        }

        // Required: size >= 16777216
        if (!obj.TryGetPropertyValue("size", out var sizeNode) ||
            sizeNode is null ||
            sizeNode is not JsonValue sizeVal ||
            sizeVal.GetValueKind() != JsonValueKind.Number ||
            !long.TryParse(sizeVal.ToString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var size) ||
            size < 16777216)
        {
            logger?.LogWarning("TDLib optimizeStorage requires integer size >= 16777216.");
            throw new TdRequestNotAllowedException("optimizeStorage requires integer size >= 16777216.");
        }

        // Required: ttl >= 3600
        if (!obj.TryGetPropertyValue("ttl", out var ttlNode) ||
            ttlNode is null ||
            ttlNode is not JsonValue ttlVal ||
            ttlVal.GetValueKind() != JsonValueKind.Number ||
            !long.TryParse(ttlVal.ToString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var ttl) ||
            ttl < 3600)
        {
            logger?.LogWarning("TDLib optimizeStorage requires integer ttl >= 3600.");
            throw new TdRequestNotAllowedException("optimizeStorage requires integer ttl >= 3600.");
        }

        // Required: count == -1
        if (!obj.TryGetPropertyValue("count", out var countNode) ||
            countNode is null ||
            countNode is not JsonValue countVal ||
            countVal.GetValueKind() != JsonValueKind.Number ||
            !long.TryParse(countVal.ToString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var count) ||
            count != -1)
        {
            logger?.LogWarning("TDLib optimizeStorage requires integer count == -1.");
            throw new TdRequestNotAllowedException("optimizeStorage requires integer count == -1.");
        }

        // Required: immunity_delay >= 600
        if (!obj.TryGetPropertyValue("immunity_delay", out var immNode) ||
            immNode is null ||
            immNode is not JsonValue immVal ||
            immVal.GetValueKind() != JsonValueKind.Number ||
            !long.TryParse(immVal.ToString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var immunityDelay) ||
            immunityDelay < 600)
        {
            logger?.LogWarning("TDLib optimizeStorage requires integer immunity_delay >= 600.");
            throw new TdRequestNotAllowedException("optimizeStorage requires integer immunity_delay >= 600.");
        }

        // Optional: file_types (absent or empty array)
        if (obj.TryGetPropertyValue("file_types", out var ftNode))
        {
            if (ftNode is not JsonArray ftArr || ftArr.Count != 0)
            {
                logger?.LogWarning("TDLib optimizeStorage file_types must be an empty array if present.");
                throw new TdRequestNotAllowedException("optimizeStorage file_types must be an empty array if present.");
            }
        }

        // Optional: chat_ids (absent or empty array)
        if (obj.TryGetPropertyValue("chat_ids", out var ciNode))
        {
            if (ciNode is not JsonArray ciArr || ciArr.Count != 0)
            {
                logger?.LogWarning("TDLib optimizeStorage chat_ids must be an empty array if present.");
                throw new TdRequestNotAllowedException("optimizeStorage chat_ids must be an empty array if present.");
            }
        }

        // Optional: exclude_chat_ids (absent or empty array)
        if (obj.TryGetPropertyValue("exclude_chat_ids", out var eciNode))
        {
            if (eciNode is not JsonArray eciArr || eciArr.Count != 0)
            {
                logger?.LogWarning("TDLib optimizeStorage exclude_chat_ids must be an empty array if present.");
                throw new TdRequestNotAllowedException("optimizeStorage exclude_chat_ids must be an empty array if present.");
            }
        }

        // Optional: return_deleted_file_statistics (absent or boolean)
        if (obj.TryGetPropertyValue("return_deleted_file_statistics", out var rdfsNode))
        {
            if (rdfsNode is null ||
                rdfsNode is not JsonValue rdfsVal ||
                (rdfsVal.GetValueKind() != JsonValueKind.True && rdfsVal.GetValueKind() != JsonValueKind.False))
            {
                logger?.LogWarning("TDLib optimizeStorage return_deleted_file_statistics must be a boolean if present.");
                throw new TdRequestNotAllowedException("optimizeStorage return_deleted_file_statistics must be a boolean if present.");
            }
        }

        // Optional: chat_limit (absent or integer 0..100)
        if (obj.TryGetPropertyValue("chat_limit", out var clNode))
        {
            if (clNode is null ||
                clNode is not JsonValue clVal ||
                clVal.GetValueKind() != JsonValueKind.Number ||
                !long.TryParse(clVal.ToString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var chatLimit) ||
                chatLimit < 0 || chatLimit > 100)
            {
                logger?.LogWarning("TDLib optimizeStorage chat_limit must be an integer 0..100 if present.");
                throw new TdRequestNotAllowedException("optimizeStorage chat_limit must be an integer 0..100 if present.");
            }
        }
    }

    private static void ValidateGetStorageStatisticsFast(JsonObject obj, ILogger? logger)
    {
        foreach (var property in obj)
        {
            if (!GetStorageStatisticsFastAllowedKeys.Contains(property.Key))
            {
                logger?.LogWarning("TDLib getStorageStatisticsFast request contains disallowed key '{PropertyName}'.", property.Key);
                throw new TdRequestNotAllowedException($"getStorageStatisticsFast does not allow key '{property.Key}'.");
            }
        }
    }

    private static void ValidateSetLogVerbosityLevel(JsonObject obj, ILogger? logger)
    {
        foreach (var property in obj)
        {
            if (!SetLogVerbosityLevelAllowedKeys.Contains(property.Key))
            {
                logger?.LogWarning("TDLib setLogVerbosityLevel request contains disallowed key '{PropertyName}'.", property.Key);
                throw new TdRequestNotAllowedException($"setLogVerbosityLevel does not allow key '{property.Key}'.");
            }
        }

        if (!obj.TryGetPropertyValue("new_verbosity_level", out var lvlNode) ||
            lvlNode is null ||
            lvlNode is not JsonValue lvlVal ||
            lvlVal.GetValueKind() != JsonValueKind.Number ||
            !long.TryParse(lvlVal.ToString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var level) ||
            level < 0 || level > 2)
        {
            logger?.LogWarning("TDLib setLogVerbosityLevel requires integer new_verbosity_level in range 0..2.");
            throw new TdRequestNotAllowedException("setLogVerbosityLevel requires integer new_verbosity_level in range 0..2.");
        }
    }

    private static void ValidateDownloadFile(JsonObject obj, ILogger? logger)
    {
        if (!obj.TryGetPropertyValue("file_id", out var fileIdNode) ||
            fileIdNode is not JsonValue fileIdVal ||
            fileIdVal.TryGetValue<string>(out _) ||
            !long.TryParse(fileIdVal.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var fileId) ||
            fileId <= 0)
        {
            logger?.LogWarning("TDLib request '@type' is not allowed: downloadFile without valid file_id");
            throw new TdRequestNotAllowedException("downloadFile requires an integer file_id > 0.");
        }
    }

    private static void ValidateSetOption(JsonObject obj, ILogger? logger)
    {
        if (!obj.TryGetPropertyValue("name", out var nameNode) ||
            nameNode is not JsonValue nameVal ||
            !nameVal.TryGetValue<string>(out var name) ||
            !string.Equals(name, "online", StringComparison.Ordinal))
        {
            logger?.LogWarning("TDLib request '@type' is not allowed: setOption");
            throw new TdRequestNotAllowedException("setOption is only permitted with name='online'.");
        }

        if (!obj.TryGetPropertyValue("value", out var valNode) || valNode is not JsonObject valObj)
        {
            logger?.LogWarning("TDLib request '@type' is not allowed: setOption");
            throw new TdRequestNotAllowedException("setOption requires a value object.");
        }

        if (!valObj.TryGetPropertyValue("@type", out var valTypeNode) ||
            valTypeNode is not JsonValue valTypeVal ||
            !valTypeVal.TryGetValue<string>(out var valType) ||
            !string.Equals(valType, "optionValueBoolean", StringComparison.Ordinal))
        {
            logger?.LogWarning("TDLib request '@type' is not allowed: setOption");
            throw new TdRequestNotAllowedException("setOption value must have @type='optionValueBoolean'.");
        }

        if (!valObj.TryGetPropertyValue("value", out var boolNode) ||
            boolNode is not JsonValue boolVal ||
            !boolVal.TryGetValue<bool>(out var boolValue) ||
            boolValue != false)
        {
            logger?.LogWarning("TDLib request '@type' is not allowed: setOption");
            throw new TdRequestNotAllowedException("setOption value for 'online' must be boolean false.");
        }
    }

    private static void ValidateGetOption(JsonObject obj, ILogger? logger)
    {
        if (!obj.TryGetPropertyValue("name", out var nameNode) ||
            nameNode is not JsonValue nameVal ||
            !nameVal.TryGetValue<string>(out var name) ||
            !string.Equals(name, "online", StringComparison.Ordinal))
        {
            logger?.LogWarning("TDLib request '@type' is not allowed: getOption");
            throw new TdRequestNotAllowedException("getOption is only permitted with name='online'.");
        }
    }
}
