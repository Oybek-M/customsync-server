using System.Globalization;
using System.Text;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
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
        return new DeviceService(db, settings, new DeviceRevocationCache());
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

    /// <summary>
    /// Kodni odam teradi: kichik harf, chiziqchasiz yoki bo'shliq bilan
    /// yozilgani ham o'sha kod. Aks holda to'g'ri kod ham
    /// `invalid_or_used_code` bo'lib qaytadi va foydalanuvchi sababini
    /// topa olmaydi. Normallashtirish kodni bir martalikligini buzmasligi kerak.
    /// </summary>
    [Theory]
    [InlineData("lower")]
    [InlineData("nodash")]
    [InlineData("spaces")]
    public async Task Enrollment_code_is_accepted_regardless_of_case_and_grouping(string variant)
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var code = await service.CreateEnrollmentCodeAsync();
        var typed = variant switch
        {
            "lower"  => code.ToLowerInvariant(),
            "nodash" => code.Replace("-", ""),
            _        => " " + code.Replace("-", " ") + "\t",
        };

        Assert.NotNull(await service.RedeemAsync(typed, "laptop", "desktop-win"));
        Assert.Null(await service.RedeemAsync(code, "phone", "android"));
    }

    /// <summary>
    /// Normallashtirish faqat yozilish shaklini tekislaydi: bitta belgisi
    /// tushib qolgan kod rad etiladi va asl kodni band qilib qo'ymaydi.
    /// </summary>
    [Fact]
    public async Task Truncated_code_is_rejected_and_does_not_consume_the_code()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var code = await service.CreateEnrollmentCodeAsync();

        Assert.Null(await service.RedeemAsync(code[..^1], "laptop", "desktop-win"));
        Assert.NotNull(await service.RedeemAsync(code, "laptop", "desktop-win"));
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

    /// <summary>
    /// Refresh token almashtiriladi. Lekin javob mijozga yetmasa (tarmoq uzildi,
    /// ilova yopildi), mijozda faqat eski token qoladi va bir martalik tokenda
    /// qurilma butunlay bloklanardi. Shuning uchun imtiyoz oynasida eski token
    /// AYNAN o'sha yangi tokenni qaytaradi: ikkinchi yaroqli token paydo bo'lmaydi.
    /// </summary>
    [Fact]
    public async Task Refresh_rotates_the_token_and_the_old_one_replays_it_within_the_grace_window()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var code = await service.CreateEnrollmentCodeAsync();
        var enrolled = await service.RedeemAsync(code, "laptop", "desktop-win");

        var refreshed = await service.RefreshAsync(enrolled!.DeviceId, enrolled.RefreshToken);
        Assert.NotNull(refreshed);
        Assert.NotEqual(enrolled.RefreshToken, refreshed!.RefreshToken);

        var replayed = await service.RefreshAsync(enrolled.DeviceId, enrolled.RefreshToken);
        Assert.Equal(refreshed.RefreshToken, replayed?.RefreshToken);

        // Yangi token ishlatilgach, eng eski token endi hech narsa bermaydi.
        Assert.NotNull(await service.RefreshAsync(enrolled.DeviceId, refreshed.RefreshToken));
        Assert.Null(await service.RefreshAsync(enrolled.DeviceId, enrolled.RefreshToken));
        Assert.Null(await service.RefreshAsync(enrolled.DeviceId, "NOT-A-REAL-TOKEN"));
    }

    /// <summary>
    /// Oyna `auth.refresh_grace_seconds` dan o'qiladi. 10 soniya oldin almashtirilgan
    /// token 5 soniyalik oynada rad etiladi, standart oynada (120 s) esa ishlaydi.
    /// </summary>
    [Fact]
    public async Task Old_refresh_token_is_rejected_after_the_grace_window()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var enrolled = await service.RedeemAsync(
            await service.CreateEnrollmentCodeAsync(), "laptop", "desktop-win");
        var refreshed = await service.RefreshAsync(enrolled!.DeviceId, enrolled.RefreshToken);
        Assert.NotNull(refreshed);

        var tenSecondsAgo = DateTime.UtcNow.AddSeconds(-10);
        await db.Devices.Where(d => d.DeviceId == enrolled.DeviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RefreshRotatedAt, tenSecondsAgo));

        var settings = new SettingsService(db);
        var original = await settings.GetIntAsync("auth.refresh_grace_seconds");
        await settings.SetAsync("auth.refresh_grace_seconds", "5");
        try
        {
            Assert.Null(await service.RefreshAsync(enrolled.DeviceId, enrolled.RefreshToken));
        }
        finally
        {
            await settings.SetAsync("auth.refresh_grace_seconds",
                original.ToString(CultureInfo.InvariantCulture));
        }

        var replayed = await service.RefreshAsync(enrolled.DeviceId, enrolled.RefreshToken);
        Assert.Equal(refreshed!.RefreshToken, replayed?.RefreshToken);
    }

    [Fact]
    public async Task Revoked_device_cannot_replay_the_previous_token()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var enrolled = await service.RedeemAsync(
            await service.CreateEnrollmentCodeAsync(), "laptop", "desktop-win");
        Assert.NotNull(await service.RefreshAsync(enrolled!.DeviceId, enrolled.RefreshToken));

        await service.RevokeAsync(enrolled.DeviceId);

        Assert.Null(await service.RefreshAsync(enrolled.DeviceId, enrolled.RefreshToken));
    }

    /// <summary>
    /// Bazada tokenlarning faqat hash'i turadi: baza o'g'irlansa ham undan
    /// ishlaydigan token olinmasligi kerak. Qayta berish uchun saqlangan yangi
    /// token ham ochiq holda emas -- uni faqat eski tokenni bilgan ocha oladi.
    /// </summary>
    [Fact]
    public async Task Stored_replay_does_not_reveal_the_new_token()
    {
        await using var db = _fixture.CreateContext();
        var service = await CreateServiceAsync(db);
        var enrolled = await service.RedeemAsync(
            await service.CreateEnrollmentCodeAsync(), "laptop", "desktop-win");
        var refreshed = await service.RefreshAsync(enrolled!.DeviceId, enrolled.RefreshToken);

        await using var check = _fixture.CreateContext();
        var row = await check.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == enrolled.DeviceId);
        Assert.NotNull(row.RotationReplay);
        var newToken = Encoding.UTF8.GetBytes(refreshed!.RefreshToken);
        Assert.True(row.RotationReplay.AsSpan().IndexOf(newToken) < 0);
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
            var service = new DeviceService(db, new SettingsService(db), new DeviceRevocationCache());
            return await service.RedeemAsync(code, name, "desktop-win");
        }

        var results = await Task.WhenAll(Attempt("birinchi"), Attempt("ikkinchi"));

        Assert.Single(results, r => r is not null);

        // Fixture bazasi shu klassdagi barcha testlarga umumiy, shuning
        // uchun faqat SHU testning qurilmalari sanaladi.
        await using var check = _fixture.CreateContext();
        var devices = await new DeviceService(check, new SettingsService(check), new DeviceRevocationCache()).ListAsync();
        Assert.Single(devices, d => d.Name is "birinchi" or "ikkinchi");
    }

    /// <summary>
    /// Bitta refresh token bilan parallel so'rovlar bitta almashtirish beradi:
    /// hammasi AYNAN bitta yangi tokenni oladi (yutqazganlar g'olibning natijasini
    /// imtiyoz oynasi orqali oladi). Aks holda har biri boshqa token olib, bazada
    /// oxirgisi qolardi va qolgan mijozlar bilmaydigan token bilan bloklanardi.
    /// </summary>
    [Fact]
    public async Task Concurrent_refresh_with_one_token_rotates_exactly_once()
    {
        for (var round = 0; round < 5; round++)
        {
            await using var setup = _fixture.CreateContext();
            var service = await CreateServiceAsync(setup);
            var code = await service.CreateEnrollmentCodeAsync();
            var enrolled = await service.RedeemAsync(code, $"refresh-race-{round}", "desktop-win");
            Assert.NotNull(enrolled);

            // Har urinish O'Z DbContext'ida (yuqoridagi redeem testi kabi).
            async Task<EnrolledDevice?> Attempt()
            {
                await using var db = _fixture.CreateContext();
                var s = new DeviceService(db, new SettingsService(db), new DeviceRevocationCache());
                return await s.RefreshAsync(enrolled!.DeviceId, enrolled.RefreshToken);
            }

            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Attempt)));
            Assert.All(results, r => Assert.NotNull(r));
            var issued = Assert.Single(results.Select(r => r!.RefreshToken).Distinct());

            // Berilgan token bazadagi token bo'lishi kerak.
            await using var check = _fixture.CreateContext();
            var checker = new DeviceService(check, new SettingsService(check), new DeviceRevocationCache());
            Assert.NotNull(await checker.RefreshAsync(enrolled!.DeviceId, issued));
        }
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
