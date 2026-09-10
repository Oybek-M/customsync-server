using System.Security.Cryptography;
using CustomSync.Services.Storage.Targets;
using Xunit;

namespace CustomSync.Tests;

public class ArchiveTargetTests : IDisposable
{
    private readonly string _tempStagingRoot =
        Path.Combine(Path.GetTempPath(), $"cs-archive-staging-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempStagingRoot))
        {
            try
            {
                Directory.Delete(_tempStagingRoot, recursive: true);
            }
            catch
            {
                // Test cleanup
            }
        }
    }

    [Fact]
    public async Task Test1_Round_trip_upload_and_download_matches_bytes_and_sha256()
    {
        var target = new ManualDownloadTarget(_tempStagingRoot);
        var originalContent = new byte[1024 * 32];
        Random.Shared.NextBytes(originalContent);

        var expectedSha256 = Convert.ToHexString(SHA256.HashData(originalContent)).ToLowerInvariant();

        using var uploadStream = new MemoryStream(originalContent);
        var uploadResult = await target.UploadAsync("backup_2026.cmx", uploadStream);

        Assert.True(uploadResult.Success);
        Assert.NotNull(uploadResult.Location);
        Assert.Equal(expectedSha256, uploadResult.Sha256);

        await using var downloadStream = await target.DownloadAsync(uploadResult.Location!);
        Assert.NotNull(downloadStream);

        using var memoryStream = new MemoryStream();
        await downloadStream.CopyToAsync(memoryStream);
        Assert.Equal(originalContent, memoryStream.ToArray());
    }

    [Fact]
    public async Task Test2_Path_traversal_archive_name_is_refused_and_nothing_written_outside()
    {
        var target = new ManualDownloadTarget(_tempStagingRoot);
        using var stream = new MemoryStream([1, 2, 3, 4]);

        var result = await target.UploadAsync("../../etc/passwd", stream);

        Assert.False(result.Success);
        Assert.Null(result.Location);
        Assert.NotNull(result.Error);

        // staging ichida yoki tashqarisida hech narsa yozilmagan bo'lishi kerak
        if (Directory.Exists(_tempStagingRoot))
        {
            Assert.Empty(Directory.GetFiles(_tempStagingRoot, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task Test3_Rooted_path_as_archive_name_is_refused()
    {
        var target = new ManualDownloadTarget(_tempStagingRoot);
        using var stream = new MemoryStream([1, 2, 3, 4]);

        var rootedPath = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\malicious.cmx"
            : "/etc/malicious.cmx";

        var result = await target.UploadAsync(rootedPath, stream);

        Assert.False(result.Success);
        Assert.Null(result.Location);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Test4_DownloadAsync_on_nonexistent_location_returns_null_without_throwing()
    {
        var target = new ManualDownloadTarget(_tempStagingRoot);
        var nonExistentPath = Path.Combine(_tempStagingRoot, "does_not_exist.cmx");

        var stream = await target.DownloadAsync(nonExistentPath);

        Assert.Null(stream);
    }

    [Fact]
    public async Task Test5_Failed_upload_leaves_no_file_behind()
    {
        var target = new ManualDownloadTarget(_tempStagingRoot);
        var failingStream = new PartialFailingStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        var result = await target.UploadAsync("failing_upload.cmx", failingStream);

        Assert.False(result.Success);
        Assert.Null(result.Location);

        if (Directory.Exists(_tempStagingRoot))
        {
            Assert.Empty(Directory.GetFiles(_tempStagingRoot, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void Test6_RequiresExplicitConfirmation_is_true_for_manual_target()
    {
        IArchiveTarget target = new ManualDownloadTarget(_tempStagingRoot);
        Assert.True(target.RequiresExplicitConfirmation);
    }

    [Fact]
    public async Task Test7_HealthCheckAsync_creates_staging_directory_when_absent()
    {
        if (Directory.Exists(_tempStagingRoot))
        {
            Directory.Delete(_tempStagingRoot, recursive: true);
        }
        Assert.False(Directory.Exists(_tempStagingRoot));

        var target = new ManualDownloadTarget(_tempStagingRoot);
        var healthy = await target.HealthCheckAsync();

        Assert.True(healthy);
        Assert.True(Directory.Exists(_tempStagingRoot));
    }

    [Fact]
    public void Test8_ValidateRoots_refuses_staging_root_inside_media_root()
    {
        var mediaRoot = Path.Combine(_tempStagingRoot, "media");
        var stagingInsideMedia = Path.Combine(mediaRoot, "staging");
        var stagingOutsideMedia = Path.Combine(_tempStagingRoot, "safe_staging");

        // Staging media ichida bo'lsa xato berishi shart
        Assert.Throws<InvalidOperationException>(() =>
            ManualDownloadTarget.ValidateRoots(stagingInsideMedia, mediaRoot));

        // Media bilan aynan bir xil bo'lsa ham xato
        Assert.Throws<InvalidOperationException>(() =>
            ManualDownloadTarget.ValidateRoots(mediaRoot, mediaRoot));

        // Staging alohida bo'lsa muvaffaqiyatli
        ManualDownloadTarget.ValidateRoots(stagingOutsideMedia, mediaRoot);
    }

    [Fact]
    public async Task Test9_ListStaged_and_DeleteStaged_manage_archives()
    {
        var target = new ManualDownloadTarget(_tempStagingRoot);
        await target.UploadAsync("arch1.cmx", new MemoryStream([1, 2, 3]));
        await target.UploadAsync("arch2.cmx", new MemoryStream([4, 5, 6]));

        var list = await target.ListStagedAsync();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, a => a.Name == "arch1.cmx");
        Assert.Contains(list, a => a.Name == "arch2.cmx");

        var deleted = await target.DeleteStagedAsync("arch1.cmx");
        Assert.True(deleted);

        var listAfter = await target.ListStagedAsync();
        Assert.Single(listAfter);
        Assert.Equal("arch2.cmx", listAfter[0].Name);
    }

    private sealed class PartialFailingStream : MemoryStream
    {
        private int _calls;
        public PartialFailingStream(byte[] buffer) : base(buffer) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_calls > 1)
            {
                throw new IOException("Simulated disk error midway through transfer");
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task Test8_Delete_cannot_reach_below_the_directory_that_list_shows()
    {
        // ListStagedAsync rekursiv EMAS -- u faqat staging ildizidagi
        // fayllarni ko'rsatadi. DeleteStagedAsync esa istalgan chuqurlikka
        // yetadi. Ya'ni o'chirish ko'rinmaydigan narsaga yetib boradi.
        //
        // Bu ValidateRoots teskari tomondan himoyalanmagani bilan
        // birlashganda haqiqiy xavf: mediaRoot staging ICHIDA sozlansa,
        // shu metod media blob'larini o'chira oladi.
        var target = new ManualDownloadTarget(_tempStagingRoot);
        await target.HealthCheckAsync();

        var nested = Path.Combine(_tempStagingRoot, "nested");
        Directory.CreateDirectory(nested);
        var victim = Path.Combine(nested, "media_blob.bin");
        await File.WriteAllBytesAsync(victim, new byte[] { 1, 2, 3 });

        var deleted = await target.DeleteStagedAsync(victim);

        Assert.False(deleted);
        Assert.True(File.Exists(victim), "Staging ildizidan pastdagi fayl o'chirilmasligi kerak");
    }

    [Fact]
    public void Test9_Media_root_inside_staging_root_is_refused_too()
    {
        // ValidateRoots faqat "staging media ichida" holatini tekshiradi.
        // Teskarisi ham xuddi shunday xavfli: ikkala daraxt bir-biriga
        // kirmasligi kerak.
        var staging = Path.Combine(_tempStagingRoot, "arxiv");
        var media = Path.Combine(staging, "media");

        Assert.Throws<InvalidOperationException>(
            () => ManualDownloadTarget.ValidateRoots(staging, media));
    }

}
