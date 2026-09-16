using CustomSync.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CustomSync.Tests.Fixtures;

public class CustomSyncWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptors = services.Where(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(ArchiveJobService)).ToList();
            foreach (var d in descriptors)
            {
                services.Remove(d);
            }
        });
    }
}
