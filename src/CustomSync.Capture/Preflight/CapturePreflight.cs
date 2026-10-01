using System.Runtime.InteropServices;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Tdlib;
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

        // 1. Native library check. Yuklash mantig'i bitta joyda —
        // SystemNativeLibraryProbe: avval bu yerda uning nusxasi turardi va
        // tekshiruvchi berilganda sozlangan-u mavjud bo'lmagan yo'l uchun
        // xato umuman qo'shilmasdi (Task 7 dan beri Worker shu yo'ldan
        // yuradi va keyin native DllNotFoundException bilan yiqilardi).
        var customPath = config["Telegram:TdJsonPath"];
        var canLoad = nativeLibChecker ?? new SystemNativeLibraryProbe().CanLoad;
        if (!canLoad(customPath))
        {
            if (string.IsNullOrWhiteSpace(customPath))
            {
                errors.Add("Native TDLib library 'tdjson' could not be loaded. Please ensure it is installed or configure Telegram:TdJsonPath.");
            }
            else if (!File.Exists(customPath))
            {
                errors.Add($"Configured TDLib library path '{customPath}' does not exist.");
            }
            else
            {
                errors.Add($"Failed to load native TDLib library from '{customPath}'.");
            }
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

        // 8. Media configuration check (Capture:Media:Enabled, PeerIds, MaxBytes, DownloadTimeoutSeconds, MaxAttempts)
        var mediaEnabledStr = config["Capture:Media:Enabled"];
        if (!string.IsNullOrEmpty(mediaEnabledStr) && !bool.TryParse(mediaEnabledStr, out _))
        {
            errors.Add("Capture:Media:Enabled must be 'true' or 'false'.");
        }

        var mediaPeerList = ScopeConfigReader.ReadPeerList(config, "Capture:Media:PeerIds");
        foreach (var entry in mediaPeerList)
        {
            if (!ScopeConfigReader.IsCanonicalPeerId(entry))
            {
                errors.Add("Capture:Media:PeerIds contains an entry that is not a canonical tdesktop peer id (positive decimal, no spaces or leading zeros; TDLib chat ids such as -100... are not accepted).");
                break;
            }
        }

        var maxBytesStr = config["Capture:Media:MaxBytes"];
        long effectiveMaxBytes = CustomSync.Capture.Media.MediaCaptureConfig.DefaultMaxBytes;
        if (!string.IsNullOrEmpty(maxBytesStr))
        {
            if (!long.TryParse(maxBytesStr, out var mb) || mb < 1 || mb > 26214400)
            {
                errors.Add("Capture:Media:MaxBytes must be an integer between 1 and 26214400.");
            }
            else
            {
                effectiveMaxBytes = mb;
            }
        }

        var maxTotalBytesStr = config["Capture:Media:MaxTotalBytes"];
        if (!string.IsNullOrEmpty(maxTotalBytesStr))
        {
            if (!long.TryParse(maxTotalBytesStr, out var mtb) || mtb < effectiveMaxBytes || mtb > 1099511627776L)
            {
                errors.Add($"Capture:Media:MaxTotalBytes must be an integer between {effectiveMaxBytes} and 1099511627776.");
            }
        }

        int effectiveDownloadTimeout = CustomSync.Capture.Media.MediaCaptureConfig.DefaultDownloadTimeoutSeconds;
        var mediaTimeoutStr = config["Capture:Media:DownloadTimeoutSeconds"];
        if (!string.IsNullOrEmpty(mediaTimeoutStr))
        {
            if (!int.TryParse(mediaTimeoutStr, out var to) || to <= 0)
            {
                errors.Add("Capture:Media:DownloadTimeoutSeconds must be a positive integer.");
            }
            else
            {
                effectiveDownloadTimeout = to;
            }
        }

        var mediaAttemptsStr = config["Capture:Media:MaxAttempts"];
        if (!string.IsNullOrEmpty(mediaAttemptsStr))
        {
            if (!int.TryParse(mediaAttemptsStr, out var ma) || ma <= 0)
            {
                errors.Add("Capture:Media:MaxAttempts must be a positive integer.");
            }
        }

        // 9. Storage maintenance configuration check
        var minFreeBytesStr = config["Capture:Storage:MinFreeBytes"];
        if (!string.IsNullOrEmpty(minFreeBytesStr))
        {
            if (!long.TryParse(minFreeBytesStr, out var mfb) || mfb < 0)
            {
                errors.Add("Capture:Storage:MinFreeBytes must be a non-negative integer.");
            }
        }

        var maintIntervalStr = config["Capture:Storage:MaintenanceIntervalMinutes"];
        if (!string.IsNullOrEmpty(maintIntervalStr))
        {
            if (!int.TryParse(maintIntervalStr, out var mim) || mim < 1 || mim > 1440)
            {
                errors.Add("Capture:Storage:MaintenanceIntervalMinutes must be an integer between 1 and 1440.");
            }
        }

        var tdFilesMaxBytesStr = config["Capture:Storage:TdlibFilesMaxBytes"];
        if (!string.IsNullOrEmpty(tdFilesMaxBytesStr))
        {
            if (!long.TryParse(tdFilesMaxBytesStr, out var tfmb) || tfmb < 16777216L || tfmb > 1099511627776L)
            {
                errors.Add("Capture:Storage:TdlibFilesMaxBytes must be an integer between 16777216 and 1099511627776.");
            }
        }

        var tdFilesTtlStr = config["Capture:Storage:TdlibFilesTtlHours"];
        if (!string.IsNullOrEmpty(tdFilesTtlStr))
        {
            if (!int.TryParse(tdFilesTtlStr, out var ttlh) || ttlh < 1 || ttlh > 8760)
            {
                errors.Add("Capture:Storage:TdlibFilesTtlHours must be an integer between 1 and 8760.");
            }
        }

        var tdImmunityStr = config["Capture:Storage:TdlibImmunitySeconds"];
        long effectiveImmunity = CustomSync.Capture.Media.MediaCaptureConfig.DefaultTdlibImmunitySeconds;
        bool immunityValid = true;
        if (!string.IsNullOrEmpty(tdImmunityStr))
        {
            if (!int.TryParse(tdImmunityStr, out var imm) || imm < 600 || imm > 604800)
            {
                errors.Add("Capture:Storage:TdlibImmunitySeconds must be an integer between 600 and 604800.");
                immunityValid = false;
            }
            else
            {
                effectiveImmunity = imm;
            }
        }

        // Standart qiymat ham tekshiriladi: DownloadTimeoutSeconds'ning yuqori
        // chegarasi yo'q, ya'ni 3600 ham uning ikki baravaridan kam bo'lishi mumkin.
        if (immunityValid && effectiveImmunity < 2L * effectiveDownloadTimeout)
        {
            errors.Add($"Capture:Storage:TdlibImmunitySeconds ({effectiveImmunity}) must be at least twice Capture:Media:DownloadTimeoutSeconds ({2L * effectiveDownloadTimeout}).");
        }

        var logVerbosityStr = config["Capture:Tdlib:LogVerbosity"];
        if (!string.IsNullOrEmpty(logVerbosityStr))
        {
            if (!int.TryParse(logVerbosityStr, out var lv) || lv < 0 || lv > 2)
            {
                errors.Add("Capture:Tdlib:LogVerbosity must be an integer between 0 and 2.");
            }
        }

        // StorageDirectory check when media is enabled
        bool isMediaEnabled = false;
        if (!string.IsNullOrEmpty(mediaEnabledStr))
        {
            bool.TryParse(mediaEnabledStr, out isMediaEnabled);
        }

        if (isMediaEnabled)
        {
            var storageDir = config["Capture:Media:StorageDirectory"];
            if (string.IsNullOrWhiteSpace(storageDir))
            {
                storageDir = "/var/lib/customsync-capture/media";
            }

            if (!Path.IsPathRooted(storageDir))
            {
                errors.Add($"Capture:Media:StorageDirectory '{storageDir}' must be an absolute (rooted) path.");
            }
            else
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                string fullStore = NormalizeDirPath(storageDir);

                CheckConflictingDir(config["Telegram:DatabaseDirectory"], "Telegram:DatabaseDirectory", fullStore, comparison, errors);
                CheckConflictingDir(config["Telegram:FilesDirectory"], "Telegram:FilesDirectory", fullStore, comparison, errors);

                CheckConflictingFile(cacheDbPath, "Capture:CacheDatabasePath", fullStore, comparison, errors);
                CheckConflictingFile(config["Capture:Sync:StatePath"] ?? "/var/lib/customsync-capture/device-state.json", "Capture:Sync:StatePath", fullStore, comparison, errors);
                CheckConflictingFile(config["Capture:Sync:MasterKeyPath"] ?? "/var/lib/customsync-capture/master.key", "Capture:Sync:MasterKeyPath", fullStore, comparison, errors);

                if (!errors.Any(e => e.Contains("Capture:Media:StorageDirectory")))
                {
                    try
                    {
                        var store = new CustomSync.Capture.Media.MediaStore(storageDir);
                        store.EnsureDirectoryCreated();
                        var testFile = Path.Combine(storageDir, $".preflight_test_{Guid.NewGuid():N}");
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Capture:Media:StorageDirectory '{storageDir}' cannot be created or is not writable: {ex.Message}");
                    }
                }
            }
        }

        return new PreflightReport(errors.Count == 0, errors);
    }

    private static string NormalizeDirPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void CheckConflictingDir(string? otherDir, string configName, string fullStore, StringComparison comparison, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(otherDir)) return;
        try
        {
            string fullOther = NormalizeDirPath(otherDir);
            if (string.Equals(fullStore, fullOther, comparison) ||
                fullStore.StartsWith(fullOther + Path.DirectorySeparatorChar, comparison) ||
                fullOther.StartsWith(fullStore + Path.DirectorySeparatorChar, comparison))
            {
                errors.Add($"Capture:Media:StorageDirectory must not be equal to, inside, or contain {configName} ('{otherDir}').");
            }
        }
        catch
        {
        }
    }

    private static void CheckConflictingFile(string? filePath, string configName, string fullStore, StringComparison comparison, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            string fullFile = Path.GetFullPath(filePath);
            if (string.Equals(fullStore, fullFile, comparison) ||
                fullFile.StartsWith(fullStore + Path.DirectorySeparatorChar, comparison))
            {
                errors.Add($"Capture:Media:StorageDirectory must not contain {configName} file ('{filePath}').");
            }
        }
        catch
        {
        }
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
