using System.Security.Cryptography;

namespace CustomSync.Services.Storage.Targets;

public sealed record StagedArchiveInfo(
    string Name,
    string Location,
    long Size,
    DateTime CreatedAt);

/// <summary>
/// Foydalanuvchi arxivni o'zi yuklab oladigan target.
/// Arxiv serverning o'z diskida vaqtinchalik staging katalogida saqlanadi.
/// Odam tasdiqlamaguncha arxiv xavfsiz hisoblanmaydi (RequiresExplicitConfirmation = true).
/// </summary>
public class ManualDownloadTarget(string stagingRoot) : IArchiveTarget
{
    public string TargetId => "manual";
    public string DisplayName => "Qo'lda yuklab olish";

    /// <summary>
    /// Checksum tekshiruvi YETARLI emasmi. Qo'lda yuklab olishda arxiv
    /// serverning o'z diskida qoladi, ya'ni uni qayta o'qish "zaxira
    /// xavfsiz joyda" degani EMAS -- buni faqat odam tasdiqlaydi.
    /// </summary>
    public bool RequiresExplicitConfirmation => true;

    /// <summary>
    /// Staging katalogi media root ichida joylashmasligini tekshiradi.
    /// Agar arxivlar media katalogida saqlansa, ular kvota va xotira
    /// metrikalarini sun'iy oshirib yuboradi.
    /// </summary>
    public static void ValidateRoots(string stagingRoot, string mediaRoot)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
        {
            throw new InvalidOperationException("Storage:ArchiveStagingRoot sozlanmagan.");
        }

        if (string.IsNullOrWhiteSpace(mediaRoot))
        {
            return;
        }

        var fullStaging = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullMedia = Path.GetFullPath(mediaRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        static bool Contains(string outer, string inner) =>
            inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            inner.StartsWith(outer + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        // Tekshiruv IKKI TOMONLAMA. Staging media ichida bo'lsa arxivlar
        // kvotaga sanaladi va purge'ni ishga tushirgan raqamni oshiradi.
        // Media staging ichida bo'lsa esa staging fayllarini boshqaruvchi
        // metodlar media blob'lariga yetib boradi. Ikkala daraxt ham
        // bir-biridan tashqarida turishi shart.
        if (fullStaging.Equals(fullMedia, StringComparison.OrdinalIgnoreCase) ||
            Contains(fullMedia, fullStaging) ||
            Contains(fullStaging, fullMedia))
        {
            throw new InvalidOperationException(
                $"Storage:ArchiveStagingRoot ('{stagingRoot}') va Storage:MediaRoot ('{mediaRoot}') "
                + "bir-birining ichida joylashishi mumkin emas.");
        }
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var fullStagingRoot = Path.GetFullPath(stagingRoot);
            if (!Directory.Exists(fullStagingRoot))
            {
                Directory.CreateDirectory(fullStagingRoot);
            }
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Arxivni staging katalogiga xavfsiz yozadi.
    /// 1. archiveName path traversal belgilari uchun tekshiriladi.
    /// 2. Avval vaqtinchalik faylga (.tmp_) yoziladi, so'ng joyiga suriladi (atomik almashtirish).
    /// 3. Xatolik yuz bersa, chala yozilgan fayllar tozalanadi.
    /// 4. SHA-256 diskdagi fayldan qayta o'qib hisoblanadi.
    /// </summary>
    public async Task<ArchiveUploadResult> UploadAsync(
        string archiveName, Stream content, CancellationToken ct = default)
    {
        // 1. Path traversal va nom validatsiyasi
        if (string.IsNullOrWhiteSpace(archiveName) ||
            archiveName.Contains("..") ||
            Path.IsPathRooted(archiveName) ||
            archiveName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\']) >= 0 ||
            Path.GetFileName(archiveName) != archiveName)
        {
            return new ArchiveUploadResult(
                false, null, null, $"Yaroqsiz arxiv nomi: '{archiveName}'. Nomda yo'l ajratgichlar yoki '..' bo'lishi mumkin emas.");
        }

        var fullStagingRoot = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootWithSep = fullStagingRoot + Path.DirectorySeparatorChar;
        var destinationPath = Path.GetFullPath(Path.Combine(fullStagingRoot, archiveName));

        if (!destinationPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return new ArchiveUploadResult(
                false, null, null, $"Yaroqsiz arxiv yo'li: '{archiveName}'. Staging katalogidan tashqariga chiqish taqiqlangan.");
        }

        Directory.CreateDirectory(fullStagingRoot);

        var tempPath = Path.Combine(fullStagingRoot, $".tmp_{Guid.NewGuid():N}_{archiveName}");
        var moved = false;

        try
        {
            await using (var fileStream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await content.CopyToAsync(fileStream, ct);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            moved = true;

            // SHA-256 diskdagi fayldan qayta o'qib hisoblanadi (haqiqatda nima yozilganini tasdiqlash)
            string hash;
            await using (var readStream = new FileStream(
                destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                var hashBytes = await SHA256.HashDataAsync(readStream, ct);
                hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            return new ArchiveUploadResult(true, destinationPath, hash, null);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                if (moved && File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }
            }
            catch
            {
                // Ikkilamchi tozalash xatosi e'tiborsiz qoldiriladi
            }

            return new ArchiveUploadResult(false, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Tekshiruv yoki yuklab olish uchun o'qish oqimini ochadi.
    /// Fayl mavjud bo'lmasa null qaytaradi, exception tashlamaydi.
    /// </summary>
    public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return Task.FromResult<Stream?>(null);
        }

        var fullStagingRoot = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootWithSep = fullStagingRoot + Path.DirectorySeparatorChar;

        var fullPath = Path.GetFullPath(Path.IsPathRooted(location)
            ? location
            : Path.Combine(fullStagingRoot, location));

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.Equals(fullStagingRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<Stream?>(null);
        }

        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    /// <summary>
    /// Staging katalogidagi mavjud arxivlarni sanaydi.
    /// Vaqtinchalik (.tmp_) fayllar chiqarib tashlanadi.
    /// </summary>
    public Task<IReadOnlyList<StagedArchiveInfo>> ListStagedAsync(CancellationToken ct = default)
    {
        var fullStagingRoot = Path.GetFullPath(stagingRoot);
        if (!Directory.Exists(fullStagingRoot))
        {
            return Task.FromResult<IReadOnlyList<StagedArchiveInfo>>([]);
        }

        var dir = new DirectoryInfo(fullStagingRoot);
        var list = dir.GetFiles()
            .Where(f => !f.Name.StartsWith(".tmp_"))
            .Select(f => new StagedArchiveInfo(
                f.Name,
                f.FullName,
                f.Length,
                f.CreationTimeUtc))
            .OrderByDescending(f => f.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<StagedArchiveInfo>>(list);
    }

    /// <summary>
    /// Staged arxivni o'chiradi.
    /// </summary>
    public Task<bool> DeleteStagedAsync(string location, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return Task.FromResult(false);
        }

        var fullStagingRoot = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootWithSep = fullStagingRoot + Path.DirectorySeparatorChar;

        var fullPath = Path.GetFullPath(Path.IsPathRooted(location)
            ? location
            : Path.Combine(fullStagingRoot, location));

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        // Faqat staging ildizining BEVOSITA bolasi o'chiriladi.
        // ListStagedAsync rekursiv emas, ya'ni u faqat ildizdagi fayllarni
        // ko'rsatadi -- o'chirish esa undan chuqurroqqa yetsa, ko'rinmagan
        // narsani o'chirgan bo'lamiz. Bu ayniqsa media daraxti staging
        // ichiga sozlanib qolgan holatda blob'larni yo'q qilishi mumkin.
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null ||
            !parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Equals(fullStagingRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }
}
