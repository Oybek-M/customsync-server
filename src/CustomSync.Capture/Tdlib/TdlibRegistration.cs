using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Tdlib;

public static class TdlibRegistration
{
    public static IServiceCollection AddTdlibClient(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isInteractiveLogin = false)
    {
        services.TryAddSingleton<INativeLibraryProbe, SystemNativeLibraryProbe>();
        services.TryAddSingleton<ITdTransport, NativeTdTransport>();
        services.TryAddSingleton<ITdClient, TdClient>();
        services.TryAddSingleton<IConsolePrompt, ConsolePrompt>();
        services.TryAddSingleton(sp => new TdAuthenticator(
            sp.GetRequiredService<ITdClient>(),
            configuration,
            sp.GetRequiredService<IConsolePrompt>(),
            isInteractiveLogin,
            sp.GetService<ILogger<TdAuthenticator>>()));

        return services;
    }
}
