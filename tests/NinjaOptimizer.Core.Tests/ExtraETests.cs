using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Safety;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Tests;

public sealed class ExtraETests
{
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static (SystemContext Ctx, FakeRegistry Reg) New()
    {
        var reg = new FakeRegistry();
        reg.SubKeys[$@"LocalMachine\{Ifeo}"] = ["notepad.exe"]; // outras entradas do IFEO que já existem
        return (new SystemContext(reg, new FakeServices(), new FakePowerCfg()), reg);
    }

    private static async Task<ActionSnapshot> Apply(SystemContext ctx, ProcessPriorityAction a)
    {
        var s = await a.CaptureAsync(ctx, default);
        await a.ApplyAsync(ctx, s, default);
        return s;
    }

    [Fact]
    public async Task Creates_only_PerfOptions_and_removes_it_on_revert_when_absent_before()
    {
        var (ctx, reg) = New();
        var a = new ProcessPriorityAction("cs2.exe", CpuPriority.AboveNormal);
        var snap = await Apply(ctx, a);

        Assert.Equal(6u, reg.GetValue(RegHive.LocalMachine, $@"{Ifeo}\cs2.exe\PerfOptions", "CpuPriorityClass")!.Data);
        Assert.Single(reg.Values); // nada de Debugger, GlobalFlag…
        Assert.True(await a.IsAppliedAsync(ctx, default));

        await ProcessPriorityAction.RevertAsync(ctx, snap, default);
        Assert.Empty(reg.Values);
        Assert.Empty(reg.GetSubKeyNames(RegHive.LocalMachine, $@"{Ifeo}\cs2.exe"));
        Assert.Equal(["notepad.exe"], reg.GetSubKeyNames(RegHive.LocalMachine, Ifeo));
    }

    [Fact]
    public async Task Restores_previous_value_and_keeps_other_ifeo_values_untouched()
    {
        var (ctx, reg) = New();
        reg.SetValue(RegHive.LocalMachine, $@"{Ifeo}\game.exe", "MitigationOptions", RegValue.Dword(9));
        reg.SetValue(RegHive.LocalMachine, $@"{Ifeo}\game.exe\PerfOptions", "CpuPriorityClass", RegValue.Dword(3));
        reg.SetValue(RegHive.LocalMachine, $@"{Ifeo}\game.exe\PerfOptions", "IoPriority", RegValue.Dword(3));
        var before = reg.Values.ToDictionary(kv => kv.Key, kv => kv.Value.Serialize());

        var snap = await Apply(ctx, new ProcessPriorityAction("game.exe", CpuPriority.AboveNormal));
        await ProcessPriorityAction.RevertAsync(ctx, snap, default);

        Assert.Equal(before, reg.Values.ToDictionary(kv => kv.Key, kv => kv.Value.Serialize()));
    }

    [Fact]
    public async Task Revert_when_the_game_was_uninstalled_and_keys_were_removed_is_harmless()
    {
        var (ctx, reg) = New();
        var snap = await Apply(ctx, new ProcessPriorityAction("old.exe", CpuPriority.High));
        reg.Values.Clear(); // outro programa ou o desinstalador limpou a chave
        await ProcessPriorityAction.RevertAsync(ctx, snap, default);
        Assert.Empty(reg.Values);
    }

    [Theory]
    [InlineData(@"C:\Games\cs2.exe")]
    [InlineData("../cs2.exe")]
    [InlineData("cs2")]
    [InlineData("cs2.exe\\Debugger")]
    [InlineData("cs2.bat")]
    [InlineData(" cs2.exe")]
    [InlineData(".exe")]
    [InlineData("")]
    [InlineData("a|b.exe")]
    public void Rejects_paths_and_invalid_names(string exe) =>
        Assert.Throws<ArgumentException>(() => new ProcessPriorityAction(exe, CpuPriority.AboveNormal));

    [Theory]
    [InlineData(4u)]   // Tempo real: nunca
    [InlineData(0u)]
    [InlineData(7u)]
    public void Rejects_priorities_outside_PerfOptions(uint value) =>
        Assert.Throws<ArgumentException>(() => new ProcessPriorityAction("cs2.exe", (CpuPriority)value));

    [Fact]
    public async Task Tampered_journal_snapshot_is_refused_on_revert()
    {
        var (ctx, _) = New();
        var snap = await Apply(ctx, new ProcessPriorityAction("cs2.exe", CpuPriority.AboveNormal));
        snap.Data["exe"] = @"..\..\Debugger";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessPriorityAction.RevertAsync(ctx, snap, default));
    }

    [Fact]
    public void Current_reads_only_valid_priorities()
    {
        var (_, reg) = New();
        Assert.Null(ProcessPriorityAction.Current(reg, "x.exe"));
        reg.SetValue(RegHive.LocalMachine, $@"{Ifeo}\x.exe\PerfOptions", "CpuPriorityClass", RegValue.Dword(4));
        Assert.Null(ProcessPriorityAction.Current(reg, "x.exe"));
        reg.SetValue(RegHive.LocalMachine, $@"{Ifeo}\x.exe\PerfOptions", "CpuPriorityClass", RegValue.Dword(6));
        Assert.Equal(CpuPriority.AboveNormal, ProcessPriorityAction.Current(reg, "x.exe"));
    }
}

public sealed class GamePriorityEngineTests : IDisposable
{
    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    [Fact]
    public async Task Per_game_priority_goes_through_engine_journal_and_undo()
    {
        var reg = new FakeRegistry();
        var ctx = new SystemContext(reg, new FakeServices(), new FakePowerCfg());
        var journalPath = Path.Combine(_paths.DataFolder, "j.json");
        var engine = new OptimizationEngine(ctx, new ChangeJournal(journalPath), new FakeRestorePoint(), new FakeBackup(), new FakeElevation(), _paths);

        var report = await engine.ApplyAsync([GamePriority.Create("cs2.exe", CpuPriority.AboveNormal)], new ApplyOptions());
        Assert.Equal(TweakOutcome.Applied, report.Results.Single().Outcome);
        Assert.Equal(CpuPriority.AboveNormal, ProcessPriorityAction.Current(reg, "cs2.exe"));

        // Nova abertura do app: o journal relido ainda desfaz
        var reloaded = new ChangeJournal(journalPath);
        var entry = GamePriority.ActiveEntry(reloaded, "CS2.exe")!;
        var engine2 = new OptimizationEngine(ctx, reloaded, new FakeRestorePoint(), new FakeBackup(), new FakeElevation(), _paths);
        Assert.True((await engine2.RevertAsync(entry.Id)).Success);
        Assert.Null(ProcessPriorityAction.Current(reg, "cs2.exe"));
        Assert.Null(GamePriority.ActiveEntry(reloaded, "cs2.exe"));
    }

    [Fact]
    public void Known_games_are_never_protected_and_skip_generic_names()
    {
        Assert.DoesNotContain(NinjaOptimizer.Core.GameMode.ProcessRules.KnownGames, NinjaOptimizer.Core.GameMode.ProcessRules.IsProtected);
        Assert.DoesNotContain("javaw", NinjaOptimizer.Core.GameMode.ProcessRules.KnownGames);
    }
}

public sealed class ConsequenceTests
{
    [Fact]
    public void Game_priority_always_has_consequences_and_core_repairs_too()
    {
        Assert.NotEmpty(GamePriority.Create("a.exe", CpuPriority.AboveNormal).Consequences);
        Assert.Contains("Discord", string.Concat(GamePriority.Create("a.exe", CpuPriority.High).Consequences));
    }

    [Fact]
    public void Portable_warning_is_added_only_on_notebooks()
    {
        var t = TweakCatalog.Get("network.adapter-power-saving");
        Assert.Equal(t.Consequences.Count + 1, t.ConsequencesFor(true).Count);
        Assert.Equal(t.Consequences.Count, t.ConsequencesFor(false).Count);
        Assert.Equal(t.Consequences.Count, t.ConsequencesFor(null).Count);
    }
}
