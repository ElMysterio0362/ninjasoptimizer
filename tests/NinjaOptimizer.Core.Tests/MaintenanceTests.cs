using NinjaOptimizer.Core.Maintenance;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Tests;

public sealed class MaintenanceTests : IDisposable
{
    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    private string Root => Path.Combine(_paths.DataFolder, "temp");

    private string NewFile(string relative, int bytes, TimeSpan age)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        var t = DateTime.UtcNow - age;
        File.SetLastWriteTimeUtc(path, t);
        File.SetLastAccessTimeUtc(path, t);
        return path;
    }

    [Fact]
    public void Cleans_only_old_files_and_empty_subfolders()
    {
        var old1 = NewFile("a.tmp", 1000, TimeSpan.FromDays(3));
        var old2 = NewFile("sub/b.tmp", 500, TimeSpan.FromDays(3));
        var fresh = NewFile("c.tmp", 100, TimeSpan.FromMinutes(5));

        Assert.Equal(1500, FolderCleaner.Measure([Root], TimeSpan.FromDays(1)).Bytes);
        var s = FolderCleaner.Clean([Root], TimeSpan.FromDays(1), CancellationToken.None);

        Assert.Equal(1500, s.Bytes);
        Assert.Equal(2, s.Files);
        Assert.False(File.Exists(old1));
        Assert.False(File.Exists(old2));
        Assert.True(File.Exists(fresh));
        Assert.False(Directory.Exists(Path.Combine(Root, "sub")));
        Assert.True(Directory.Exists(Root)); // a pasta raiz nunca é apagada
    }

    [Fact]
    public void Never_follows_folder_links_out_of_the_cleaned_folder()
    {
        var outside = Path.Combine(_paths.DataFolder, "importante");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "documento.txt");
        File.WriteAllText(precious, "não apagar");
        File.SetLastWriteTimeUtc(precious, DateTime.UtcNow.AddDays(-30));
        Directory.CreateDirectory(Root);
        Directory.CreateSymbolicLink(Path.Combine(Root, "link"), outside);

        FolderCleaner.Clean([Root], TimeSpan.Zero, CancellationToken.None);

        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void Missing_folders_are_ignored()
    {
        var s = FolderCleaner.Clean([Path.Combine(Root, "nao-existe")], TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(0, s.Files);
    }

    [Fact]
    public async Task Folder_task_estimates_and_reports_freed_bytes()
    {
        NewFile("x.tmp", 2048, TimeSpan.FromDays(2));
        var task = new FolderCleanupTask("t", "Temporários", "d", TweakCategory.Disk, () => [Root], TimeSpan.FromDays(1));
        Assert.Equal(2048, await task.EstimateAsync(CancellationToken.None));
        var r = await task.RunAsync(null, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Equal(2048, r.BytesFreed);
    }

    [Fact]
    public void Store_keeps_last_run_per_task()
    {
        var store = new MaintenanceStore(Path.Combine(_paths.DataFolder, "maintenance.json"));
        store.Record("temp", new MaintenanceStore.Entry(DateTimeOffset.Now, 10, true, "ok"));
        store.Record("temp", new MaintenanceStore.Entry(DateTimeOffset.Now, 20, true, "ok"));
        Assert.Equal(20, store.Load()["temp"].BytesFreed);
    }

    [Theory]
    [InlineData("True\r\n", true)]
    [InlineData("\r\nFalse\r\n", false)]
    [InlineData("erro", null)]
    public void Parses_powershell_bool(string output, bool? expected) => Assert.Equal(expected, CommandStateAction.ParseBool(output));

    [Theory]
    [InlineData("    HibernateEnabled    REG_DWORD    0x1\r\n", true)]
    [InlineData("    HibernateEnabled    REG_DWORD    0x0\r\n", false)]
    [InlineData("ERRO: valor não encontrado", null)]
    public void Parses_reg_query_dword(string output, bool? expected) => Assert.Equal(expected, CommandStateAction.ParseRegDword(output));
}

public sealed class HibernationStateTests
{
    private const string Key = @"SYSTEM\CurrentControlSet\Control\Power";

    private static bool? Read(FakeRegistry reg) =>
        TweakCatalog.HibernationEnabled(new NinjaOptimizer.Core.Abstractions.SystemContext(reg, new FakeServices(), new FakePowerCfg()));

    [Fact]
    public void Reads_default_when_HibernateEnabled_does_not_exist()
    {
        var reg = new FakeRegistry();
        Assert.True(Read(reg)); // nada no registro: padrão do Windows (ligada)
        reg.SetValue(NinjaOptimizer.Core.Abstractions.RegHive.LocalMachine, Key, "HibernateEnabledDefault", NinjaOptimizer.Core.Abstractions.RegValue.Dword(0));
        Assert.False(Read(reg));
        reg.SetValue(NinjaOptimizer.Core.Abstractions.RegHive.LocalMachine, Key, "HibernateEnabled", NinjaOptimizer.Core.Abstractions.RegValue.Dword(1));
        Assert.True(Read(reg)); // o valor explícito vence o padrão
    }
}
