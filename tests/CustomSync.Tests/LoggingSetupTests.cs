using CustomSync.Api.Logging;
using CustomSync.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace CustomSync.Tests;

public class LoggingSetupTests : IClassFixture<CustomSyncWebApplicationFactory>
{
    private const string EfCommand = "Microsoft.EntityFrameworkCore.Database.Command";

    private readonly CustomSyncWebApplicationFactory _factory;

    public LoggingSetupTests(CustomSyncWebApplicationFactory factory) => _factory = factory;

    private sealed class CollectingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.MessageTemplate.Text);
    }

    /// <summary>
    /// Sync yoqilgach capture har 30 s da pull qiladi va EF har SQL buyrug'ini
    /// Information darajasida yozadi -- jurnal SQL bilan to'lib, haqiqiy
    /// ogohlantirishlar ko'rinmay qolardi. EF'ning ogohlantirish va xatolari
    /// esa qolishi kerak, ilovaning o'z Information loglari ham.
    /// </summary>
    [Fact]
    public void Ef_sql_commands_are_filtered_but_warnings_errors_and_app_logs_pass()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration().ApplyLevels().WriteTo.Sink(sink).CreateLogger();

        var ef = logger.ForContext(Constants.SourceContextPropertyName, EfCommand);
        ef.Information("ef-sql");
        ef.Warning("ef-warning");
        ef.Error("ef-error");
        logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Hosting.Diagnostics")
              .Information("hosting-info");
        logger.ForContext(Constants.SourceContextPropertyName, "CustomSync.Services.SyncService")
              .Information("app-info");

        Assert.Equal(new[] { "ef-warning", "ef-error", "app-info" }, sink.Messages);
    }

    /// <summary>
    /// Darajalar Program.cs dagi logger'ga haqiqatan ulanganmi. `ApplyLevels`
    /// chaqirilmay qolsa, yuqoridagi test baribir o'tardi.
    /// </summary>
    [Fact]
    public void Application_logger_factory_uses_the_same_levels()
    {
        var loggers = _factory.Services.GetRequiredService<ILoggerFactory>();
        var ef = loggers.CreateLogger(EfCommand);

        Assert.False(ef.IsEnabled(LogLevel.Information));
        Assert.True(ef.IsEnabled(LogLevel.Warning));
        Assert.True(loggers.CreateLogger("CustomSync.Services.SyncService").IsEnabled(LogLevel.Information));
    }
}
