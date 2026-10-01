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
        "downloadFile"
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

        // 4. Special validation for setOption, getOption, and downloadFile
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

        return obj;
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
