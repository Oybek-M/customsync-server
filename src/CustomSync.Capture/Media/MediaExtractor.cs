using System.Text.Json;

namespace CustomSync.Capture.Media;

public static class MediaExtractor
{
    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "messagePhoto",
        "messageVideo",
        "messageDocument",
        "messageAudio",
        "messageVoiceNote",
        "messageVideoNote",
        "messageAnimation"
    };

    public static bool TryGetMediaInfo(
        JsonElement content,
        long maxBytes,
        out string? contentType,
        out long? knownSize,
        out int? selectedFileId)
    {
        contentType = null;
        knownSize = null;
        selectedFileId = null;

        if (content.ValueKind != JsonValueKind.Object)
            return false;

        if (!content.TryGetProperty("@type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        if (type == null || !AllowedContentTypes.Contains(type))
            return false;

        contentType = type;

        if (type == "messagePhoto")
        {
            if (!content.TryGetProperty("photo", out var photoProp) || photoProp.ValueKind != JsonValueKind.Object)
                return false;

            if (!photoProp.TryGetProperty("sizes", out var sizesProp) || sizesProp.ValueKind != JsonValueKind.Array)
                return false;

            long bestSize = -1;
            int? bestFileId = null;

            foreach (var sizeElem in sizesProp.EnumerateArray())
            {
                if (!sizeElem.TryGetProperty("photo", out var fileProp) || fileProp.ValueKind != JsonValueKind.Object)
                    continue;

                long size = ExtractFileSize(fileProp);
                if (size > 0 && size <= maxBytes)
                {
                    if (size > bestSize)
                    {
                        bestSize = size;
                        if (fileProp.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var fid) && fid > 0)
                        {
                            bestFileId = fid;
                        }
                    }
                }
            }

            if (bestSize > 0)
            {
                knownSize = bestSize;
                selectedFileId = bestFileId;
                return true;
            }

            return false;
        }

        if (TryGetFileElement(type, content, out var innerFile))
        {
            long size = ExtractFileSize(innerFile);
            if (size > 0 && size <= maxBytes)
            {
                knownSize = size;
                if (innerFile.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var fid) && fid > 0)
                {
                    selectedFileId = fid;
                }
                return true;
            }
        }

        return false;
    }

    public static bool TryGetFileElement(string type, JsonElement content, out JsonElement fileElem)
    {
        fileElem = default;
        switch (type)
        {
            case "messageVideo":
                return content.TryGetProperty("video", out var v) && v.TryGetProperty("video", out fileElem);
            case "messageDocument":
                return content.TryGetProperty("document", out var d) && d.TryGetProperty("document", out fileElem);
            case "messageAudio":
                return content.TryGetProperty("audio", out var a) && a.TryGetProperty("audio", out fileElem);
            case "messageVoiceNote":
                return content.TryGetProperty("voice_note", out var vn) &&
                       (vn.TryGetProperty("voice", out fileElem) || vn.TryGetProperty("voice_note", out fileElem));
            case "messageVideoNote":
                return content.TryGetProperty("video_note", out var vdn) &&
                       (vdn.TryGetProperty("video", out fileElem) || vdn.TryGetProperty("video_note", out fileElem));
            case "messageAnimation":
                return content.TryGetProperty("animation", out var anim) && anim.TryGetProperty("animation", out fileElem);
            default:
                return false;
        }
    }

    public static long ExtractFileSize(JsonElement fileElem)
    {
        if (fileElem.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var s) && s > 0)
            return s;

        if (fileElem.TryGetProperty("expected_size", out var expProp) && expProp.TryGetInt64(out var es) && es > 0)
            return es;

        return 0;
    }

    public static bool TryGetSelectedFileElement(string type, JsonElement content, long maxBytes, out JsonElement fileElem)
    {
        fileElem = default;
        if (type == "messagePhoto")
        {
            if (!content.TryGetProperty("photo", out var photoProp) || photoProp.ValueKind != JsonValueKind.Object)
                return false;

            if (!photoProp.TryGetProperty("sizes", out var sizesProp) || sizesProp.ValueKind != JsonValueKind.Array)
                return false;

            long bestSize = -1;
            JsonElement bestElem = default;

            foreach (var sizeElem in sizesProp.EnumerateArray())
            {
                if (!sizeElem.TryGetProperty("photo", out var fElem) || fElem.ValueKind != JsonValueKind.Object)
                    continue;

                long size = ExtractFileSize(fElem);
                if (size > 0 && size <= maxBytes)
                {
                    if (size > bestSize)
                    {
                        bestSize = size;
                        bestElem = fElem;
                    }
                }
            }

            if (bestSize > 0)
            {
                fileElem = bestElem;
                return true;
            }
            return false;
        }

        return TryGetFileElement(type, content, out fileElem);
    }

    public static bool TryGetLocalFile(JsonElement fileElem, out string? path, out bool isCompleted)
    {
        path = null;
        isCompleted = false;
        if (fileElem.TryGetProperty("local", out var local) && local.ValueKind == JsonValueKind.Object)
        {
            if (local.TryGetProperty("is_downloading_completed", out var comp) && comp.ValueKind == JsonValueKind.True)
            {
                isCompleted = true;
            }
            if (local.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                path = p.GetString();
            }
            return true;
        }
        return false;
    }
}
