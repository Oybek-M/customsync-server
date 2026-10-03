using CustomSync.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CustomSync.Tests.Fixtures;

public class CustomSyncWebApplicationFactory : WebApplicationFactory<Program>
{
    // Standart `/var/lib/customsync/...` (Windows'da `C:/var/lib/...`) ga
    // test bloblari yozilmasin: har fabrika o'z temp papkasini oladi.
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "customsync-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting — Program.cs bu qiymatlarni build paytida o'qiydi.
        builder.UseSetting("Storage:MediaRoot", Path.Combine(_storageRoot, "media"));
        builder.UseSetting("Storage:ArchiveStagingRoot", Path.Combine(_storageRoot, "staging"));

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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_storageRoot, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
