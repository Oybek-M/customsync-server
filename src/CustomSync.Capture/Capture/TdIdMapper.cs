using System.Globalization;
using System.Text.Json;

namespace CustomSync.Capture.Capture;

/// <summary>
/// Pure static mapper converting TDLib identifiers to tdesktop form.
///
/// TDLib uses chat_id:
///   user = user_id (positive)
///   basic group = -group_id (ChatId Shift 1 &lt;&lt; 48)
///   supergroup/channel = -(1000000000000 + channel_id) (ChannelId Shift 2 &lt;&lt; 48)
///   secret chat = &lt;= -2000000000000 -> no mapping (null)
///
/// Server message_id: tdlib_id &gt;&gt; 20, valid only when low 20 bits are 0.
/// </summary>
public static class TdIdMapper
{
    private const long BasicGroupMaxId = -1L;
    private const long BasicGroupMinId = -999999999999L;
    private const long ChannelOffset = 1000000000000L;
    private const long SupergroupMaxId = -1000000000001L;
    private const long SupergroupMinId = -1999999999999L;

    public static string? ToPeerId(long chatId)
    {
        if (chatId > 0)
        {
            // User: Shift = 0
            return chatId.ToString(CultureInfo.InvariantCulture);
        }

        if (chatId <= BasicGroupMaxId && chatId >= BasicGroupMinId)
        {
            // Basic group: ChatId Shift = 1
            ulong bare = (ulong)(-chatId);
            ulong peerId = bare | (1UL << 48);
            return peerId.ToString(CultureInfo.InvariantCulture);
        }

        if (chatId <= SupergroupMaxId && chatId >= SupergroupMinId)
        {
            // Supergroup / Channel: ChannelId Shift = 2 (2UL << 48 == 1UL << 49)
            ulong bare = (ulong)(-chatId - ChannelOffset);
            ulong peerId = bare | (2UL << 48);
            return peerId.ToString(CultureInfo.InvariantCulture);
        }

        // Secret chats or invalid/zero -> no mapping
        return null;
    }

    public static long? ToServerMessageId(long tdlibMessageId)
    {
        // Only server messages have the low 20 bits zero
        if ((tdlibMessageId & 0xFFFFF) != 0)
        {
            return null;
        }

        long id = tdlibMessageId >> 20;
        return id > 0 ? id : null;
    }

    public static string? ToSenderId(JsonElement senderElement)
    {
        if (senderElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!senderElement.TryGetProperty("@type", out var typeProp))
        {
            return null;
        }

        var type = typeProp.GetString();
        if (type == "messageSenderUser" && senderElement.TryGetProperty("user_id", out var userProp))
        {
            return userProp.GetInt64().ToString(CultureInfo.InvariantCulture);
        }

        if (type == "messageSenderChat" && senderElement.TryGetProperty("chat_id", out var chatProp))
        {
            return ToPeerId(chatProp.GetInt64());
        }

        return null;
    }
}
