using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Xunit;

namespace CustomSync.Tests;

public class MediaServiceTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"cs-media-{Guid.NewGuid():N}");

    public MediaServiceTests(DatabaseFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string ValidHash(char fill) => new string(fill, 64);

    [Fact]
    public async Task Stored_blob_can_be_read_back_byte_for_byte()
    {
        await using var db = _fixture.CreateContext();
        var service = new MediaService(db, _root);
        var content = new byte[] { 9, 8, 7, 6, 5 };
        var hash = ValidHash('a');

        await service.StoreAsync(hash, content, new byte[12]);
        var read = await service.ReadAsync(hash);

        Assert.Equal(content, read);
    }

    [Fact]
    public async Task Storing_the_same_hash_twice_is_a_no_op()
    {
        await using var db = _fixture.CreateContext();
        var service = new MediaService(db, _root);
        var hash = ValidHash('b');

        await service.StoreAsync(hash, [1, 2, 3], new byte[12]);
        await service.StoreAsync(hash, [1, 2, 3], new byte[12]);

        Assert.True(await service.ExistsAsync(hash));
        Assert.Equal(new byte[] { 1, 2, 3 }, await service.ReadAsync(hash));
    }

    [Fact]
    public async Task Missing_blob_reads_as_null_rather_than_throwing()
    {
        await using var db = _fixture.CreateContext();
        var service = new MediaService(db, _root);
        var missingHash = ValidHash('0');

        Assert.Null(await service.ReadAsync(missingHash));
        Assert.False(await service.ExistsAsync(missingHash));
    }
}
