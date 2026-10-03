using System.Security.Cryptography;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Server testlari media bloblarini standart `Storage:MediaRoot` ga
/// (`/var/lib/customsync/media`, Windows'da `C:\var\lib\customsync\media`)
/// yozib, temp tashqarisida axlat yig'ardi. Fabrika endi har safar o'z temp
/// papkasini beradi.
/// </summary>
public class ServerTestStorageTests : IClassFixture<CustomSyncWebApplicationFactory>
{
    private readonly CustomSyncWebApplicationFactory _factory;

    public ServerTestStorageTests(CustomSyncWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Test01_Factory_media_blobs_land_in_a_temp_directory()
    {
        var root = _factory.Services.GetRequiredService<IConfiguration>()["Storage:MediaRoot"];
        Assert.False(string.IsNullOrEmpty(root));
        Assert.StartsWith(
            Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root!), StringComparison.OrdinalIgnoreCase);

        // Konfiguratsiyadagi qiymatning o'zi yetmaydi: API uni build paytida
        // o'qiydi — blob haqiqatan shu papkaga tushishi kerak.
        var hash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using (var scope = _factory.Services.CreateScope())
        {
            var media = scope.ServiceProvider.GetRequiredService<MediaService>();
            await media.StoreAsync(hash, [1, 2, 3], new byte[12]);
        }

        Assert.True(File.Exists(Path.Combine(root!, hash[..2], hash)));
    }
}
