using System.Runtime.InteropServices;
using CustomSync.Capture.Capture;
using Microsoft.Extensions.Configuration;

namespace CustomSync.Capture.Preflight;

public record PreflightReport(bool Success, IReadOnlyList<string> Errors)
{
    public bool Passed => Success;
}

public static class CapturePreflight
{
    public static PreflightReport Check(IConfiguration config, Func<string?, bool>? nativeLibChecker = null)
    {
        var errors = new List<string>();

        // 1. Native library check
        var customPath = config["Telegram:TdJsonPath"];
        bool libOk;
        if (nativeLibChecker != null)
        {
            libOk = nativeLibChecker(customPath);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                if (!File.Exists(customPath))
                {
                    errors.Add($"Configured TDLib library path '{customPath}' does not exist.");
                    libOk = false;
                }
                else
                {
                    libOk = NativeLibrary.TryLoad(customPath, out var handle);
                    if (libOk) NativeLibrary.Free(handle);
                }
            }
            else
            {
                libOk = NativeLibrary.TryLoad("tdjson", typeof(CapturePreflight).Assembly, null, out var handle);
                if (libOk) NativeLibrary.Free(handle);
            }
        }

        if (!libOk && (string.IsNullOrWhiteSpace(customPath) || (File.Exists(customPath) && !errors.Any(e => e.Contains("does not exist")))))
        {
            errors.Add(string.IsNullOrWhiteSpace(customPath)
                ? "Native TDLib library 'tdjson' could not be loaded. Please ensure it is installed or configure Telegram:TdJsonPath."
                : $"Failed to load native TDLib library from '{customPath}'.");
        }

        // 2. ApiId / ApiHash check - Note: secret values are never printed
        var apiIdStr = config["Telegram:ApiId"];
        if (!int.TryParse(apiIdStr, out var apiId) || apiId <= 0)
        {
            errors.Add("Telegram:ApiId is not configured or invalid (must be a positive integer).");
        }

        var apiHash = config["Telegram:ApiHash"];
        if (string.IsNullOrWhiteSpace(apiHash))
        {
            errors.Add("Telegram:ApiHash is not configured.");
        }

        // 3. DatabaseDirectory / FilesDirectory check
        CheckDirectory(config["Telegram:DatabaseDirectory"], "DatabaseDirectory", errors);
        CheckDirectory(config["Telegram:FilesDirectory"], "FilesDirectory", errors);

        // 4. Cache database directory check
        var cacheDbPath = config["Capture:CacheDatabasePath"];
        if (string.IsNullOrWhiteSpace(cacheDbPath))
        {
            cacheDbPath = "/var/lib/customsync-capture/message-cache.db";
        }
        var cacheDir = Path.GetDirectoryName(cacheDbPath);
        if (string.IsNullOrWhiteSpace(cacheDir))
        {
            cacheDir = ".";
        }

        try
        {
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            var testFile = Path.Combine(cacheDir, $".preflight_test_{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            errors.Add($"Capture:CacheDatabasePath directory '{cacheDir}' cannot be created or is not writable: {ex.Message}");
        }

        // 5. Scope configuration check (Capture:Scope:Block, Capture:Scope:Allow)
        var blockList = ScopeConfigReader.ReadPeerList(config, "Capture:Scope:Block");
        var allowList = ScopeConfigReader.ReadPeerList(config, "Capture:Scope:Allow");

        bool hasBlockError = false;
        foreach (var entry in blockList)
        {
            if (!ScopeConfigReader.IsCanonicalPeerId(entry))
            {
                errors.Add("Capture:Scope:Block contains an entry that is not a tdesktop peer id (positive decimal, no spaces or leading zeros; TDLib chat ids such as -100... are not accepted).");
                hasBlockError = true;
                break;
            }
        }

        bool hasAllowError = false;
        foreach (var entry in allowList)
        {
            if (!ScopeConfigReader.IsCanonicalPeerId(entry))
            {
                errors.Add("Capture:Scope:Allow contains an entry that is not a tdesktop peer id (positive decimal, no spaces or leading zeros; TDLib chat ids such as -100... are not accepted).");
                hasAllowError = true;
                break;
            }
        }

        if (!hasBlockError && !hasAllowError)
        {
            var blockSet = new HashSet<string>(blockList);
            var allowSet = new HashSet<string>(allowList);
            if (blockSet.Overlaps(allowSet))
            {
                errors.Add("Capture:Scope:Block and Capture:Scope:Allow contain overlapping peer entries.");
            }
        }

        // Xato yozilgan qiymat jimgina `false` bo'lsa, egasi "yoqdim" deb
        // o'ylaydi-yu, xizmat hech narsa ushlamaydi.
        var defaultEnabled = config["Capture:Scope:DefaultEnabled"];
        if (!string.IsNullOrEmpty(defaultEnabled) && !bool.TryParse(defaultEnabled, out _))
        {
            errors.Add("Capture:Scope:DefaultEnabled must be 'true' or 'false'.");
        }

        // 6. Activity Scope configuration check (Capture:Activity:Exclude, Capture:Activity:Include, Capture:Activity:TrackAllContacts)
        var actExcludeList = ScopeConfigReader.ReadPeerList(config, "Capture:Activity:Exclude");
        var actIncludeList = ScopeConfigReader.ReadPeerList(config, "Capture:Activity:Include");

        bool hasActExcludeError = false;
        foreach (var entry in actExcludeList)
        {
            if (!ScopeConfigReader.IsCanonicalPeerId(entry))
            {
                errors.Add("Capture:Activity:Exclude contains an entry that is not a canonical tdesktop peer id (positive decimal, no spaces or leading zeros; TDLib chat ids such as -100... are not accepted).");
                hasActExcludeError = true;
                break;
            }
        }

        bool hasActIncludeError = false;
        foreach (var entry in actIncludeList)
        {
            if (!ScopeConfigReader.IsCanonicalPeerId(entry))
            {
                errors.Add("Capture:Activity:Include contains an entry that is not a canonical tdesktop peer id (positive decimal, no spaces or leading zeros; TDLib chat ids such as -100... are not accepted).");
                hasActIncludeError = true;
                break;
            }
        }

        if (!hasActExcludeError && !hasActIncludeError)
        {
            var actExcludeSet = new HashSet<string>(actExcludeList);
            var actIncludeSet = new HashSet<string>(actIncludeList);
            if (actExcludeSet.Overlaps(actIncludeSet))
            {
                errors.Add("Capture:Activity:Exclude and Capture:Activity:Include contain overlapping peer entries.");
            }
        }

        var trackAllContacts = config["Capture:Activity:TrackAllContacts"];
        if (!string.IsNullOrEmpty(trackAllContacts) && !bool.TryParse(trackAllContacts, out _))
        {
            errors.Add("Capture:Activity:TrackAllContacts must be 'true' or 'false'.");
        }

        // 7. Sync configuration check
        var syncEnabledStr = config["Capture:Sync:Enabled"];
        if (!string.IsNullOrEmpty(syncEnabledStr))
        {
            if (!bool.TryParse(syncEnabledStr, out var syncEnabled))
            {
                errors.Add("Capture:Sync:Enabled must be 'true' or 'false'.");
            }
            else if (syncEnabled)
            {
                var serverUrl = config["Capture:Sync:ServerUrl"];
                if (string.IsNullOrWhiteSpace(serverUrl))
                {
                    errors.Add("Capture:Sync:ServerUrl must be configured when sync is enabled.");
                }
                else if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
                {
                    errors.Add("Capture:Sync:ServerUrl must be an absolute URL.");
                }
                else if (uri.Scheme == Uri.UriSchemeHttp)
                {
                    if (uri.Host != "localhost" && uri.Host != "127.0.0.1")
                    {
                        errors.Add("Capture:Sync:ServerUrl must use HTTPS, except for localhost or 127.0.0.1.");
                    }
                }
                else if (uri.Scheme != Uri.UriSchemeHttps)
                {
                    errors.Add("Capture:Sync:ServerUrl must use HTTPS.");
                }
            }
        }

        var maxWrapIterStr = config["Capture:Sync:MaxWrapIterations"];
        if (!string.IsNullOrEmpty(maxWrapIterStr))
        {
            if (!int.TryParse(maxWrapIterStr, out var maxIter) || maxIter <= 0)
            {
                errors.Add("Capture:Sync:MaxWrapIterations must be a positive integer.");
            }
        }

        var pullBatchStr = config["Capture:Sync:PullBatchSize"];
        if (!string.IsNullOrEmpty(pullBatchStr))
        {
            if (!int.TryParse(pullBatchStr, out var pb) || pb <= 0)
            {
                errors.Add("Capture:Sync:PullBatchSize must be a positive integer.");
            }
        }

        var maxPagesStr = config["Capture:Sync:MaxPullPagesPerCycle"];
        if (!string.IsNullOrEmpty(maxPagesStr))
        {
            if (!int.TryParse(maxPagesStr, out var mp) || mp <= 0)
            {
                errors.Add("Capture:Sync:MaxPullPagesPerCycle must be a positive integer.");
            }
        }

        return new PreflightReport(errors.Count == 0, errors);
    }

    private static void CheckDirectory(string? dirPath, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(dirPath))
        {
            errors.Add($"Telegram:{name} is not configured.");
            return;
        }

        try
        {
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }

            // Test write permission with temporary probe file
            var testFile = Path.Combine(dirPath, $".preflight_test_{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            errors.Add($"Telegram:{name} directory '{dirPath}' cannot be created or is not writable: {ex.Message}");
        }
    }
}
