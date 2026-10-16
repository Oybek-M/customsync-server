using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CustomSync.Api.Auth;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Oxirgi kalit o'rami o'chirilsa, yangi qurilma va web panel arxivni
/// hech qachon ocholmaydi -- buni faqat UI emas, server rad etadi
/// (admin tokeni sizsa ham, UI xato qilsa ham).
/// </summary>
/// Web factory'lar bitta dev bazani bo'lishadi, `key_wraps` soni esa global --
/// boshqa klasslar parallel o'ram qo'shsa/o'chirsa, "oxirgisi" testlari buzilardi.
[CollectionDefinition(Name, DisableParallelization = true)]
public class KeyWrapGuardCollection { public const string Name = "key-wrap-guard"; }

[Collection(KeyWrapGuardCollection.Name)]
public class KeyWrapGuardTests : IClassFixture<CustomSyncWebApplicationFactory>
{
    private readonly CustomSyncWebApplicationFactory _factory;

    public KeyWrapGuardTests(CustomSyncWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtIssuer>();
        var code = await devices.CreateEnrollmentCodeAsync("admin");
        var enrolled = await devices.RedeemAsync(code, $"kw-{Guid.NewGuid():N}", "test-platform");
        Assert.NotNull(enrolled);
        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> CreateWrapAsync(HttpClient client, string label)
    {
        var response = await client.PostAsJsonAsync("/api/v1/keys/wraps", new
        {
            wrap_type = "passphrase", label,
            salt = Convert.ToBase64String(new byte[16]),
            nonce = Convert.ToBase64String(new byte[12]),
            wrapped_key = Convert.ToBase64String(new byte[48]),
            iterations = 600000
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        return body!["wrap_id"];
    }

    private async Task<int> WrapCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<KeyWrapService>().ListAsync()).Count;
    }

    private async Task DeleteAllButAsync(HttpClient client, int keep)
    {
        using var scope = _factory.Services.CreateScope();
        var wraps = await scope.ServiceProvider.GetRequiredService<KeyWrapService>().ListAsync();
        foreach (var w in wraps.Skip(keep))
            (await client.DeleteAsync($"/api/v1/keys/wraps/{w.WrapId}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Deleting_the_last_wrap_is_refused()
    {
        var client = await AdminAsync();
        await CreateWrapAsync(client, "a");
        await CreateWrapAsync(client, "b");
        await DeleteAllButAsync(client, keep: 2);

        using (var scope = _factory.Services.CreateScope())
        {
            var wraps = await scope.ServiceProvider.GetRequiredService<KeyWrapService>().ListAsync();
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.DeleteAsync($"/api/v1/keys/wraps/{wraps[0].WrapId}")).StatusCode);

            var last = await client.DeleteAsync($"/api/v1/keys/wraps/{wraps[1].WrapId}");
            Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
            Assert.Contains("last_wrap", await last.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, await WrapCountAsync());
    }

    /// Ikki parallel o'chirish ikkalasi ham "2 ta bor" deb ko'rmasligi kerak.
    [Fact]
    public async Task Two_concurrent_deletes_cannot_remove_both_remaining_wraps()
    {
        var client = await AdminAsync();
        await CreateWrapAsync(client, "c");
        await CreateWrapAsync(client, "d");
        await DeleteAllButAsync(client, keep: 2);

        IReadOnlyList<WrapSummary> wraps;
        using (var scope = _factory.Services.CreateScope())
            wraps = await scope.ServiceProvider.GetRequiredService<KeyWrapService>().ListAsync();

        var results = await Task.WhenAll(
            client.DeleteAsync($"/api/v1/keys/wraps/{wraps[0].WrapId}"),
            client.DeleteAsync($"/api/v1/keys/wraps/{wraps[1].WrapId}"));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await WrapCountAsync());
    }

    [Fact]
    public async Task Autolock_setting_exists_with_a_safe_default()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        Assert.Equal(15, await settings.GetIntAsync("web.autolock_minutes"));
    }
}
