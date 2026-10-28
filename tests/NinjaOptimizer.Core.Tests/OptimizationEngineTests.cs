using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;
using NinjaOptimizer.Core.Safety;

namespace NinjaOptimizer.Core.Tests;

public sealed class OptimizationEngineTests : IDisposable
{
    private readonly FakeRegistry _reg = new();
    private readonly FakeServices _svc = new();
    private readonly FakePowerCfg _power = new();
    private readonly FakeRestorePoint _rp = new();
    private readonly FakeBackup _backup = new();
    private readonly FakeElevation _elev = new();
    private readonly TempPaths _paths = new();
    private readonly OptimizationEngine _engine;

    public OptimizationEngineTests()
    {
        _svc.Services["DiagTrack"] = (ServiceStartMode.Automatic, true);
        _engine = NewEngine();
    }

    private OptimizationEngine NewEngine() => new(
        new SystemContext(_reg, _svc, _power),
        new ChangeJournal(Path.Combine(_paths.DataFolder, "journal.json")),
        _rp, _backup, _elev, _paths);

    public void Dispose() => _paths.Dispose();

    private static ApplyOptions Opts => new();

    [Fact]
    public async Task Telemetry_apply_and_revert_restores_exact_previous_state()
    {
        // Um valor já existia com outro conteúdo; o outro não existia.
        _reg.SetValue(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", RegValue.Dword(3));
        var before = new Dictionary<string, RegValue>(_reg.Values);
        var tweak = TweakCatalog.Get("privacy.telemetry");

        var report = await _engine.ApplyAsync([tweak], Opts);

        Assert.Equal(1, report.AppliedCount);
        Assert.True(await _engine.IsAppliedAsync(tweak));
        Assert.Equal((ServiceStartMode.Disabled, false), _svc.Services["DiagTrack"]);
        Assert.Equal(1, _rp.Calls);
        Assert.NotEmpty(_backup.Exported);

        var entry = Assert.Single(_engine.Journal.ActiveEntries);
        var revert = await _engine.RevertAsync(entry.Id);

        Assert.True(revert.Success);
        Assert.Equal(before, _reg.Values); // valor antigo restaurado e o criado removido
        Assert.Equal((ServiceStartMode.Automatic, true), _svc.Services["DiagTrack"]);
        Assert.False(await _engine.IsAppliedAsync(tweak));
        Assert.Empty(_engine.Journal.ActiveEntries);
    }

    [Fact]
    public async Task GameDvr_only_touches_registry_and_reverts()
    {
        var tweak = TweakCatalog.Get("gaming.gamedvr");
        await _engine.ApplyAsync([tweak], Opts);
        Assert.Equal(RegValue.Dword(0), _reg.GetValue(RegHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled"));

        await _engine.RevertAllAsync();
        Assert.Empty(_reg.Values);
    }

    [Fact]
    public async Task PowerPlan_creates_app_plan_and_revert_restores_previous_and_deletes_it()
    {
        var tweak = TweakCatalog.Get("cpu.ultimate-power-plan");
        var report = await _engine.ApplyAsync([tweak], Opts);

        Assert.Equal(TweakOutcome.Applied, report.Results.Single().Outcome);
        Assert.Equal(PowerPlanAction.AppUltimateGuid, _power.Active);

        await _engine.RevertAllAsync();
        Assert.Equal(PowerPlanAction.BalancedGuid, _power.Active);
        Assert.DoesNotContain(PowerPlanAction.AppUltimateGuid, _power.Schemes);
    }

    [Fact]
    public async Task PowerPlan_falls_back_to_high_performance()
    {
        _power.BlockUltimate = true;
        await _engine.ApplyAsync([TweakCatalog.Get("cpu.ultimate-power-plan")], Opts);
        Assert.Equal(PowerPlanAction.HighPerformanceGuid, _power.Active);
    }

    [Fact]
    public async Task Failure_mid_tweak_rolls_back_already_applied_actions()
    {
        _reg.FailOnSetName = "AllowGameDVR"; // 3ª ação do Game DVR falha
        var report = await _engine.ApplyAsync([TweakCatalog.Get("gaming.gamedvr")], Opts);

        Assert.Equal(TweakOutcome.Failed, report.Results.Single().Outcome);
        Assert.Empty(_reg.Values); // as 2 primeiras foram desfeitas
        Assert.Equal(JournalStatus.Failed, _engine.Journal.Entries.Single().Status);
    }

    [Fact]
    public async Task High_risk_is_never_applied_without_explicit_confirmation()
    {
        var risky = new Tweak
        {
            Id = "test.risky", Name = "x", Description = "x", Benefit = "x",
            Category = TweakCategory.Cpu, Risk = RiskLevel.High,
            Actions = [new RegistryValueAction(RegHive.CurrentUser, "K", "V", RegValue.Dword(1))],
        };

        var noCallback = await _engine.ApplyAsync([risky], Opts);
        Assert.Equal(TweakOutcome.SkippedHighRisk, noCallback.Results.Single().Outcome);

        var denied = await _engine.ApplyAsync([risky], new ApplyOptions { ConfirmHighRisk = _ => Task.FromResult(false) });
        Assert.Equal(TweakOutcome.SkippedHighRisk, denied.Results.Single().Outcome);
        Assert.Empty(_reg.Values);

        var ok = await _engine.ApplyAsync([risky], new ApplyOptions { ConfirmHighRisk = _ => Task.FromResult(true) });
        Assert.Equal(TweakOutcome.Applied, ok.Results.Single().Outcome);
    }

    [Fact]
    public async Task Admin_tweaks_abort_when_not_elevated_and_nothing_changes()
    {
        _elev.IsElevated = false;
        var report = await _engine.ApplyAsync([TweakCatalog.Get("privacy.telemetry")], Opts);
        Assert.True(report.Aborted);
        Assert.Empty(_reg.Values);
        Assert.Equal(0, _rp.Calls);
    }

    [Fact]
    public async Task Restore_point_failure_can_abort()
    {
        var engine = new OptimizationEngine(new SystemContext(_reg, _svc, _power),
            new ChangeJournal(Path.Combine(_paths.DataFolder, "j2.json")), new FakeRestorePoint(false), _backup, _elev, _paths);

        var report = await engine.ApplyAsync([TweakCatalog.Get("gaming.gamedvr")], new ApplyOptions { AbortIfRestorePointFails = true });
        Assert.True(report.Aborted);
        Assert.Empty(_reg.Values);
    }

    [Fact]
    public async Task Already_applied_tweak_is_skipped()
    {
        var t = TweakCatalog.Get("gaming.gamedvr");
        await _engine.ApplyAsync([t], Opts);
        var again = await _engine.ApplyAsync([t], Opts);
        Assert.Equal(TweakOutcome.AlreadyApplied, again.Results.Single().Outcome);
        Assert.Single(_engine.Journal.Entries);
    }

    [Fact]
    public async Task Journal_survives_restart_and_revert_works_from_disk()
    {
        await _engine.ApplyAsync([TweakCatalog.Get("privacy.telemetry")], Opts);

        var reopened = NewEngine(); // relê o journal do disco
        var results = await reopened.RevertAllAsync();

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Empty(_reg.Values);
        Assert.Equal(ServiceStartMode.Automatic, _svc.Services["DiagTrack"].Mode);
    }

    [Fact]
    public async Task RevertAll_undoes_in_reverse_order()
    {
        var a = new Tweak
        {
            Id = "a", Name = "a", Description = "", Benefit = "", Category = TweakCategory.Cpu, Risk = RiskLevel.Low,
            Actions = [new RegistryValueAction(RegHive.CurrentUser, "K", "V", RegValue.Dword(1))],
        };
        var b = new Tweak
        {
            Id = "b", Name = "b", Description = "", Benefit = "", Category = TweakCategory.Cpu, Risk = RiskLevel.Low,
            Actions = [new RegistryValueAction(RegHive.CurrentUser, "K", "V", RegValue.Dword(2))],
        };
        await _engine.ApplyAsync([a], Opts);
        await _engine.ApplyAsync([b], Opts);
        await _engine.RevertAllAsync();
        Assert.Empty(_reg.Values);
    }

    [Fact]
    public void Catalog_ids_are_unique_and_every_profile_is_populated()
    {
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var p in Enum.GetValues<OptimizationProfile>())
            Assert.NotEmpty(TweakCatalog.ForProfile(p));
        // Perfil Seguro nunca inclui risco médio/alto.
        Assert.All(TweakCatalog.ForProfile(OptimizationProfile.Safe), t => Assert.Equal(RiskLevel.Low, t.Risk));
    }
}

public sealed class JournalCleanupTests : IDisposable
{
    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    [Fact]
    public void RemoveFailed_keeps_applied_and_reverted_entries()
    {
        var path = Path.Combine(_paths.DataFolder, "journal.json");
        var j = new ChangeJournal(path);
        j.Add(new JournalEntry { TweakId = "a", Status = JournalStatus.Applied });
        j.Add(new JournalEntry { TweakId = "b", Status = JournalStatus.Failed });
        j.Add(new JournalEntry { TweakId = "c", Status = JournalStatus.Reverted });

        Assert.Equal(1, j.RemoveFailed());
        Assert.Equal(["a", "c"], new ChangeJournal(path).Entries.Select(e => e.TweakId));
    }
}
