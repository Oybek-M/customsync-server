using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Xunit;

namespace CustomSync.Tests;

public class DeviceAuthTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    public DeviceAuthTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<DeviceService> CreateServiceAsync(Data.SyncDbContext db)
    {
        var settings = new SettingsService(db);
        await settings.EnsureDefaultsAsync();
        return new DeviceService(db, settings);
    }

    [Fact]
    public async Task Enrollment_code_can_be_redeemed_once()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var code = await service.CreateEnrollmentCodeAsync();

        var first = await service.RedeemAsync(code, "laptop", "desktop-win");
        var second = await service.RedeemAsync(code, "phone", "android");

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task Expired_code_is_rejected()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var code = await service.CreateEnrollmentCodeAsync();

        await service.ExpireAllCodesAsync();

        Assert.Null(await service.RedeemAsync(code, "laptop", "desktop-win"));
    }

    [Fact]
    public async Task Revoked_device_cannot_refresh()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var code = await service.CreateEnrollmentCodeAsync();
        var enrolled = await service.RedeemAsync(code, "laptop", "desktop-win");

        await service.RevokeAsync(enrolled!.DeviceId);

        Assert.Null(await service.RefreshAsync(enrolled.DeviceId, enrolled.RefreshToken));
    }

    [Fact]
    public async Task Refresh_rotates_the_token()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var code = await service.CreateEnrollmentCodeAsync();
        var enrolled = await service.RedeemAsync(code, "laptop", "desktop-win");

        var refreshed = await service.RefreshAsync(enrolled!.DeviceId, enrolled.RefreshToken);

        Assert.NotNull(refreshed);
        Assert.NotEqual(enrolled.RefreshToken, refreshed!.RefreshToken);
        Assert.Null(await service.RefreshAsync(enrolled.DeviceId, enrolled.RefreshToken));
    }

    /// <summary>
    /// Bir martalik kod ATOMAR ishlatilishi kerak. Ketma-ket tekshiruv
    /// (`Enrollment_code_can_be_redeemed_once`) buni isbotlamaydi: agar
    /// o'qish va yozish ajratilgan bo'lsa, ikki so'rov bir vaqtda
    /// tekshiruvdan o'tib, ikkalasi ham qurilma ro'yxatdan o'tkazadi.
    /// Bu esa kodning butun maqsadini yo'qqa chiqaradi.
    /// </summary>
    [Fact]
    public async Task Concurrent_redeem_of_one_code_enrolls_exactly_one_device()
    {
        await using var setup = _fixture.CreateContext();
        var code = await (await CreateServiceAsync(setup)).CreateEnrollmentCodeAsync();

        // Har urinish O'Z DbContext'ida -- aks holda bitta change
        // tracker ularni sun'iy ravishda ketma-ketlashtirar edi.
        async Task<EnrolledDevice?> Attempt(string name)
        {
            await using var db = _fixture.CreateContext();
            var service = new DeviceService(db, new SettingsService(db));
            return await service.RedeemAsync(code, name, "desktop-win");
        }

        var results = await Task.WhenAll(Attempt("birinchi"), Attempt("ikkinchi"));

        Assert.Single(results.Where(r => r is not null));

        // Fixture bazasi shu klassdagi barcha testlarga umumiy, shuning
        // uchun faqat SHU testning qurilmalari sanaladi.
        await using var check = _fixture.CreateContext();
        var devices = await new DeviceService(check, new SettingsService(check)).ListAsync();
        Assert.Single(devices.Where(d => d.Name is "birinchi" or "ikkinchi"));
    }

    /// <summary>
    /// `deviceId` PRIMARY KEY. Uzun platform nomi GUID qismini kesib
    /// tashlamasligi va ikki qurilma bir xil id olmasligi kerak.
    /// </summary>
    [Fact]
    public async Task Long_platform_names_still_produce_distinct_device_ids()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        const string longPlatform = "linux-x86_64-flatpak-nightly-build";

        var first  = await service.RedeemAsync(
            await service.CreateEnrollmentCodeAsync(), "a", longPlatform);
        var second = await service.RedeemAsync(
            await service.CreateEnrollmentCodeAsync(), "b", longPlatform);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.DeviceId, second!.DeviceId);
    }
}
