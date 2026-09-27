using System.Globalization;
using System.Text;

namespace CustomSync.Capture.Capture;

/// <summary>
/// Serializes payload JSON byte-for-byte compatible with tdesktop custom_sync_payload.cpp.
/// Cyrillic, Uzbek, and emoji characters remain literal UTF-8 without unicode escaping.
/// String fields are never JSON null (absent text is "").
/// </summary>
public static class PayloadBuilder
{
    public static string EscapeString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        return sb.ToString();
    }

    public static string BuildDeleted(
        string accountId,
        string peerId,
        string? text,
        string? senderId,
        bool isOut,
        bool isMedia)
    {
        return $"{{\"account_id\":\"{EscapeString(accountId)}\",\"peer_id\":\"{EscapeString(peerId)}\",\"text\":\"{EscapeString(text)}\",\"sender_id\":\"{EscapeString(senderId)}\",\"is_out\":{(isOut ? "true" : "false")},\"is_media\":{(isMedia ? "true" : "false")}}}";
    }

    public static string BuildEdited(
        string accountId,
        string peerId,
        string? oldText,
        string? newText,
        bool isOut)
    {
        return $"{{\"account_id\":\"{EscapeString(accountId)}\",\"peer_id\":\"{EscapeString(peerId)}\",\"old_text\":\"{EscapeString(oldText)}\",\"new_text\":\"{EscapeString(newText)}\",\"is_out\":{(isOut ? "true" : "false")}}}";
    }

    public static string BuildActivity(
        string accountId,
        string peerId,
        string field,
        bool hasOldValue,
        string? oldValue,
        string newValue)
    {
        string oldValueJson = hasOldValue ? $"\"{EscapeString(oldValue)}\"" : "null";
        return $"{{\"account_id\":\"{EscapeString(accountId)}\",\"peer_id\":\"{EscapeString(peerId)}\",\"field\":\"{EscapeString(field)}\",\"old_value\":{oldValueJson},\"has_old_value\":{(hasOldValue ? "true" : "false")},\"new_value\":\"{EscapeString(newValue)}\"}}";
    }
}
