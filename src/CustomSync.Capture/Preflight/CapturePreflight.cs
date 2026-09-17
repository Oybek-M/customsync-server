using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace CustomSync.Capture.Preflight;

public record PreflightReport(bool Success, IReadOnlyList<string> Errors);

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
