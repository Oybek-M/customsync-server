using CustomSync.Tests.Fixtures;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CustomSync.Tests;

public class HealthTests : IClassFixture<CustomSyncWebApplicationFactory>
{
    private readonly CustomSyncWebApplicationFactory _factory;

    public HealthTests(CustomSyncWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_ok_with_version()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        Assert.Equal("ok", body!.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.Version));
    }

    private sealed record HealthBody(string Status, string Version);
}
