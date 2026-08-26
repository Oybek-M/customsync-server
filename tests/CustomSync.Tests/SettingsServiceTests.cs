using System.Globalization;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Xunit;

namespace CustomSync.Tests;

public class SettingsServiceTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    public SettingsServiceTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Seeds_defaults_on_first_run()
    {
        await using var db = _fixture.CreateContext();
        var service = new SettingsService(db);

        await service.EnsureDefaultsAsync();

        Assert.Equal(500, await service.GetIntAsync("sync.push_batch_size"));
        Assert.Equal(50,  await service.GetIntAsync("api.default_page_size"));
    }

    [Fact]
    public async Task Set_then_get_returns_new_value_without_restart()
    {
        await using var db = _fixture.CreateContext();
        var service = new SettingsService(db);
        await service.EnsureDefaultsAsync();

        // IClassFixture -- bitta DatabaseFixture, demak bitta baza --
        // shu klassdagi barcha testlarga baham qilinadi. Qiymatni
        // asl holiga qaytarmasak, testlar tartibiga qarab boshqa
        // testlar buzilib qolardi (masalan Seeds_defaults_on_first_run
        // 500 o'rniga shu yerda qo'yilgan qiymatni ko'rardi).
        var original = await service.GetIntAsync("sync.push_batch_size");
        try
        {
            await service.SetAsync("sync.push_batch_size", "250");

            Assert.Equal(250, await service.GetIntAsync("sync.push_batch_size"));
        }
        finally
        {
            await service.SetAsync(
                "sync.push_batch_size", original.ToString(CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task Unknown_key_throws_rather_than_returning_a_silent_default()
    {
        await using var db = _fixture.CreateContext();
        var service = new SettingsService(db);
        await service.EnsureDefaultsAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetIntAsync("does.not.exist"));
    }

    /// <summary>
    /// Spec §0.3: mijozda retention 30 kun, serverda UZUNROQ (90) --
    /// aks holda mijoz o'chirgan yozuvni server qaytarib, cheksiz
    /// sikl yuzaga keladi.
    /// </summary>
    [Fact]
    public async Task Retention_activity_days_defaults_to_90()
    {
        await using var db = _fixture.CreateContext();
        var service = new SettingsService(db);

        await service.EnsureDefaultsAsync();

        Assert.Equal(90, await service.GetIntAsync("retention.activity_days"));
    }
}
