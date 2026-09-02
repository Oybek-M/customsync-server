using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Data.Entities;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Audit iz testlari. Endpoint testlari WebApplicationFactory orqali
/// ishga tushiriladi (dev bazasi ishlatiladi).
///
/// 🔴 Faqat SHU testda yaratilgan qurilma/hodisalar bo'yicha assert
/// qiling. Dev bazasida boshqa testlardan qolgan qatorlar ham bor.
/// Global son yoki "eng yangi qator" bo'yicha tekshirish vaqti-vaqti
/// bilan yiqiladi — bu xato loyihada ikki marta takrorlandi.
/// </summary>
public class AuditTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuditTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // ----------------------------------------------------------------
    // Yordamchi: qurilmani ro'yxatdan o'tkazadi va token qaytaradi.
    // ----------------------------------------------------------------
    private async Task<(HttpClient Client, string DeviceId)> EnrolDeviceAsync(string role = "device")
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt     = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code     = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"test-{Guid.NewGuid():N}", "audit-test");
        Assert.NotNull(enrolled);

        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId);
    }

    private async Task<IReadOnlyList<AuditLogEntity>> GetAuditRowsForDevice(string deviceId)
    {
        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var all   = await audit.RecentAsync(1000);
        return all.Where(r => r.DeviceId == deviceId).ToList();
    }

    // ----------------------------------------------------------------
    // 1. enroll — device.enrolled yoziladimi?
    // ----------------------------------------------------------------
    [Fact]
    public async Task Enroll_writes_device_enrolled_audit_row()
    {
        // Enroll'ni endpoint orqali qilamiz (xuddi haqiqiy klient singari)
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();

        var code = await devices.CreateEnrollmentCodeAsync();

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/devices/enroll", new
        {
            code,
            name     = "test-laptop",
            platform = "audit-test-platform"
        });

        response.EnsureSuccessStatusCode();
        var body     = await response.Content.ReadFromJsonAsync<EnrollResponse>(TestJson.Options);
        var deviceId = body!.DeviceId;

        var rows = await GetAuditRowsForDevice(deviceId);
        Assert.Contains(rows, r => r.Action == "device.enrolled");
    }

    // ----------------------------------------------------------------
    // 2. revoke — device.revoked yoziladimi?
    // ----------------------------------------------------------------
    [Fact]
    public async Task Revoke_writes_device_revoked_audit_row()
    {
        var (client, deviceId) = await EnrolDeviceAsync();

        var response = await client.DeleteAsync($"/api/v1/devices/{deviceId}");
        response.EnsureSuccessStatusCode();

        var rows = await GetAuditRowsForDevice(deviceId);
        Assert.Contains(rows, r => r.Action == "device.revoked");
    }

    // ----------------------------------------------------------------
    // 3. settings change — settings.changed yoziladimi?
    //    Actor (admin qurilma) ham saqlangan bo'lishi kerak.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Settings_change_writes_settings_changed_audit_row()
    {
        var (adminClient, adminDeviceId) = await EnrolDeviceAsync("admin");

        var response = await adminClient.PutAsJsonAsync(
            "/api/v1/settings/sync.push_batch_size", new { value = "123" });
        response.EnsureSuccessStatusCode();

        // Restore to original value
        await adminClient.PutAsJsonAsync(
            "/api/v1/settings/sync.push_batch_size", new { value = "500" });

        // settings.changed qatori bo'lishi kerak va detail'da actor bo'lishi kerak
        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var all   = await audit.RecentAsync(1000);

        // Faqat shu admin tomonidan "sync.push_batch_size" o'zgartirilgan qatorlar
        var changed = all.Where(r =>
            r.Action == "settings.changed" &&
            r.Detail != null &&
            r.Detail.Contains("sync.push_batch_size") &&
            r.ActorDeviceId == adminDeviceId).ToList();

        Assert.NotEmpty(changed);

        // Actor alohida USTUNDA bo'lishi kerak, JSON ichida emas --
        // web app (plan 03) "falon qurilma nima qilgan?" so'rovini
        // indeks bilan bajara olishi uchun.
        Assert.All(changed, r => Assert.Equal(adminDeviceId, r.ActorDeviceId));
    }

    // ----------------------------------------------------------------
    // 4. RecentAsync — eng yangi birinchi va limit ishlaydi
    // ----------------------------------------------------------------
    [Fact]
    public async Task RecentAsync_returns_newest_first_and_honours_limit()
    {
        // DatabaseFixture o'z bazasida ishlaydi — shu yerda tekshirish
        // uchun AuditService ni to'g'ridan-to'g'ri chaqiramiz.
        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();

        // Birdan ortiq qator qo'shamiz
        var marker = Guid.NewGuid().ToString("N");
        await audit.WriteAsync("test.event", targetDeviceId: marker);
        await Task.Delay(10); // vaqt farqi kafolatlash uchun
        await audit.WriteAsync("test.event", targetDeviceId: marker);

        var recent = await audit.RecentAsync(1);

        // Limit ishlashi kerak
        Assert.Single(recent);

        // RecentAsync barcha natijalardan eng yangisi birinchi bo'lishi kerak
        var allRows = await audit.RecentAsync(1000);
        var myRows  = allRows.Where(r => r.DeviceId == marker).ToList();
        Assert.Equal(2, myRows.Count);

        // Tartib: desc (yangi birinchi). allRows desc tartibda keladi,
        // shuning uchun myRows[0].At >= myRows[1].At
        Assert.True(myRows[0].At >= myRows[1].At);
    }

    private sealed record EnrollResponse(string DeviceId, string RefreshToken, string AccessToken);
}
