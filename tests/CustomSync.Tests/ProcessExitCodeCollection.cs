using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// `Worker` muvaffaqiyatsizlikda `Environment.ExitCode = 1` qo'yadi — bu
/// butun test jarayoni uchun bitta global qiymat. Unga tegadigan testlar
/// shu kolleksiyada ketma-ket yuradi, aks holda bir test qoldirgan `1`
/// boshqasining "exit kod 1 bo'ldi" tekshiruvini hech narsa isbotlamaydigan
/// qilib qo'yadi.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ProcessExitCodeCollection
{
    public const string Name = "ProcessExitCode";
}
