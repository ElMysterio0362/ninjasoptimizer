using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;
using NinjaOptimizer.Core.Safety;

namespace NinjaOptimizer.Core.Tests;

/// <summary>Valida todo o catálogo: cada ajuste aplica, é detectado como aplicado e volta exatamente ao estado de antes.</summary>
public sealed class CatalogTests : IDisposable
{
    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    public static TheoryData<string> AllIds()
    {
        var d = new TheoryData<string>();
        foreach (var t in TweakCatalog.All) d.Add(t.Id);
        return d;
    }

    private (OptimizationEngine Engine, FakeRegistry Reg, FakeServices Svc, FakePowerCfg Power) NewEngine()
    {
        var reg = new FakeRegistry();
        var svc = new FakeServices();
        svc.Services["DiagTrack"] = (ServiceStartMode.Automatic, true);
        var power = new FakePowerCfg { Registry = reg };
        var engine = new OptimizationEngine(new SystemContext(reg, svc, power),
            new ChangeJournal(Path.Combine(_paths.DataFolder, Guid.NewGuid() + ".json")),
            new FakeRestorePoint(), new FakeBackup(), new FakeElevation(), _paths);
        return (engine, reg, svc, power);
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public async Task Every_tweak_applies_and_reverts_to_an_empty_registry(string id)
    {
        var (engine, reg, svc, power) = NewEngine();
        var tweak = TweakCatalog.Get(id);
        var activeBefore = power.Active;
        var settingsBefore = new Dictionary<string, uint>(power.Settings);

        var report = await engine.ApplyAsync([tweak], new ApplyOptions { ConfirmHighRisk = _ => Task.FromResult(true) });
        Assert.Equal(TweakOutcome.Applied, report.Results.Single().Outcome);
        Assert.True(await engine.IsAppliedAsync(tweak));

        var revert = await engine.RevertAllAsync();
        Assert.All(revert, r => Assert.True(r.Success, r.Message));
        // O powercfg grava HibernateEnabled ao religar a hibernação (o estado é conferido abaixo)
        reg.Values.Remove(@"LocalMachine\SYSTEM\CurrentControlSet\Control\Power\HibernateEnabled");
        Assert.Empty(reg.Values);
        Assert.Equal((ServiceStartMode.Automatic, true), svc.Services["DiagTrack"]);
        Assert.Equal(activeBefore, power.Active);
        Assert.All(power.Settings, kv => Assert.Equal(settingsBefore.GetValueOrDefault(kv.Key, 50u), kv.Value));
        Assert.True(power.MemoryCompression);
        Assert.True(power.Hibernate);
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public async Task Every_tweak_restores_previous_values_exactly(string id)
    {
        var (engine, reg, _, _) = NewEngine();
        var tweak = TweakCatalog.Get(id);

        // Valores pré-existentes diferentes do alvo, do mesmo tipo
        foreach (var a in tweak.Actions.OfType<RegistryValueAction>())
            reg.SetValue(a.Hive, a.Key, a.Name, a.Value.Kind == RegKind.DWord ? RegValue.Dword(77) : RegValue.Str("antes"));
        var before = new Dictionary<string, RegValue>(reg.Values);

        await engine.ApplyAsync([tweak], new ApplyOptions { ConfirmHighRisk = _ => Task.FromResult(true) });
        await engine.RevertAllAsync();

        reg.Values.Remove(@"LocalMachine\SYSTEM\CurrentControlSet\Control\Power\HibernateEnabled");
        Assert.Equal(before, reg.Values);
    }

    [Fact]
    public void Catalog_rules()
    {
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in TweakCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Benefit), t.Id);
            Assert.True(t.Reversible, t.Id);
            // Sem exclamação (tom de voz da marca)
            Assert.DoesNotContain("!", t.Name + t.Description + t.Benefit);
            foreach (var a in t.Actions.OfType<RegistryValueAction>())
            {
                Assert.False(a.Key.StartsWith('\\') || a.Key.EndsWith('\\'), $"{t.Id}: {a.Key}");
                Assert.DoesNotContain("HKEY", a.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"\\", a.Key);
            }
        }
        // Nenhuma ação repete o mesmo valor de registro em dois ajustes (a reversão de um desfaria o outro)
        var values = TweakCatalog.All.SelectMany(t => t.Actions.OfType<RegistryValueAction>()
            .Select(a => $"{a.Hive}\\{a.Key}\\{a.Name}".ToLowerInvariant())).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
        // Seguro: só risco baixo. Risco alto nunca entra em perfil.
        Assert.All(TweakCatalog.ForProfile(OptimizationProfile.Safe), t => Assert.Equal(RiskLevel.Low, t.Risk));
        Assert.All(TweakCatalog.All.Where(t => t.Risk == RiskLevel.High), t => Assert.Empty(t.Profiles));
        // Todo perfil tem ajustes, e todo ajuste fora de perfil é uma correção opcional explícita
        foreach (var p in Enum.GetValues<OptimizationProfile>()) Assert.NotEmpty(TweakCatalog.ForProfile(p));
        Assert.All(TweakCatalog.All.Where(t => t.Profiles.Count == 0), t => Assert.True(t.Risk >= RiskLevel.Medium, t.Id));
    }
}
