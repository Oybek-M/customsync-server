using System.Reflection;
using System.Runtime.Versioning;
using CustomSync.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CustomSync.Tests;

public class PlatformUpgradeTests
{
    [Fact]
    public void Test01_All_CustomSync_Assemblies_Target_Net10_And_Process_Runs_On_Net10()
    {
        Assert.Equal(10, Environment.Version.Major);

        var assemblies = new[]
        {
            typeof(Program).Assembly,
            typeof(CustomSync.Capture.Worker).Assembly,
            typeof(CustomSync.Core.CryptoPrimitives).Assembly,
            typeof(CustomSync.Data.SyncDbContext).Assembly,
            typeof(CustomSync.Services.SyncService).Assembly,
            typeof(PlatformUpgradeTests).Assembly
        };

        foreach (var asm in assemblies)
        {
            var targetFramework = asm.GetCustomAttribute<TargetFrameworkAttribute>();
            Assert.NotNull(targetFramework);
            Assert.Equal(".NETCoreApp,Version=v10.0", targetFramework.FrameworkName);
        }
    }

    [Fact]
    public void Test02_SyncDbContext_Has_No_Pending_Model_Changes()
    {
        // Production'dagi `AddDbContext` generic bo'lmagan builder beradi — test
        // aynan o'sha overload orqali sozlaydi (ulanish ochilmaydi). Ilgari test
        // generic nusxani tekshirardi: production nusxasidan snake_case olib
        // tashlansa ham bu test o'tib ketardi.
        var builder = new DbContextOptionsBuilder<SyncDbContext>();
        ((DbContextOptionsBuilder)builder).ConfigureSyncDb("Host=unused");

        using var db = new SyncDbContext(builder.Options);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Test03_Missing_connection_string_fails_instead_of_using_a_default(string? connectionString)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DbContextOptionsBuilder<SyncDbContext>().ConfigureSyncDb(connectionString));
        Assert.Contains("ConnectionStrings:Postgres", ex.Message);
    }
}
