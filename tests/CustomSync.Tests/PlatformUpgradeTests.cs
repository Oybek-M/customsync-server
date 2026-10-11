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
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<CustomSync.Data.SyncDbContext>()
            .ConfigureSyncDb()
            .Options;

        using var db = new CustomSync.Data.SyncDbContext(options);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
