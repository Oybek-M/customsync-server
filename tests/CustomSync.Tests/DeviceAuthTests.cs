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
}
