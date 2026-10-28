using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;
using NinjaOptimizer.Core.Safety;

namespace NinjaOptimizer.Core.Tests;

public sealed class PowerAndDirectXTests : IDisposable
{
    private readonly TempPaths _paths = new();
    private readonly FakeRegistry _reg = new();
    private readonly FakePowerCfg _power = new();
    public void Dispose() => _paths.Dispose();

    private OptimizationEngine Engine() => new(new SystemContext(_reg, new FakeServices(), _power),
        new ChangeJournal(Path.Combine(_paths.DataFolder, Guid.NewGuid() + ".json")),
        new FakeRestorePoint(), new FakeBackup(), new FakeElevation(), _paths);

    [Fact]
    public void Parses_powercfg_query_in_portuguese()
    {
        const string output = """
            GUID do Esquema de Energia: 381b4222-f694-41f0-9685-ff5bb260df2e  (Equilibrado)
              GUID do Subgrupo: 54533251-82be-4824-96c1-47b60b740d00  (Gerenciamento de energia do processador)
                GUID da Configuração de Energia: 0cc5b647-c1df-4637-891a-dec35c318583
                  Configuração Mínima Possível: 0x00000000
                  Configuração Máxima Possível: 0x00000064
                  Incremento de Configurações Possíveis: 0x00000001
                Índice de Configuração de Energia CA Atual: 0x0000000a
                Índice de Configuração de Energia CC Atual: 0x00000005
            """;
        Assert.Equal((10u, 5u), PowerSettingAction.ParseQuery(output));
    }

    [Fact]
    public async Task Core_parking_sets_the_active_plan_and_restores_previous_value()
    {
        var key = $"{_power.Active}|54533251-82be-4824-96c1-47b60b740d00|0cc5b647-c1df-4637-891a-dec35c318583";
        _power.Settings[key] = 10;
        var engine = Engine();
        var tweak = TweakCatalog.Get("cpu.core-parking");

        await engine.ApplyAsync([tweak], new ApplyOptions());
        Assert.Equal(100u, _power.Settings[key]);
        Assert.True(await engine.IsAppliedAsync(tweak));

        await engine.RevertAllAsync();
        Assert.Equal(10u, _power.Settings[key]);
    }

    [Fact]
    public async Task Reverting_after_the_plan_was_deleted_is_harmless()
    {
        var engine = Engine();
        // Aplica o plano Desempenho Máximo e depois o core parking nele
        await engine.ApplyAsync([TweakCatalog.Get("cpu.ultimate-power-plan")], new ApplyOptions());
        await engine.ApplyAsync([TweakCatalog.Get("cpu.core-parking")], new ApplyOptions());
        // Desfaz o plano primeiro (ele é apagado) e só depois o core parking
        var plan = engine.Journal.Entries.First(e => e.TweakId == "cpu.ultimate-power-plan");
        Assert.True((await engine.RevertAsync(plan.Id)).Success);
        var parking = engine.Journal.Entries.First(e => e.TweakId == "cpu.core-parking");
        Assert.True((await engine.RevertAsync(parking.Id)).Success);
    }

    [Fact]
    public void DirectX_merge_preserves_other_options()
    {
        Assert.Equal("AutoHDREnable=1;VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;",
            DirectXSettingAction.Merge("AutoHDREnable=1;VRROptimizeEnable=0;", "SwapEffectUpgradeEnable", "1"));
        Assert.Equal("SwapEffectUpgradeEnable=1;", DirectXSettingAction.Merge(null, "SwapEffectUpgradeEnable", "1"));
        Assert.Equal("SwapEffectUpgradeEnable=1;", DirectXSettingAction.Merge("SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable", "1"));
    }

    [Fact]
    public async Task DirectX_tweak_keeps_auto_hdr_and_reverts_to_original_string()
    {
        _reg.SetValue(RegHive.CurrentUser, DirectXSettingAction.Key, DirectXSettingAction.ValueName, RegValue.Str("AutoHDREnable=1;"));
        var engine = Engine();
        var tweak = TweakCatalog.Get("gpu.windowed-optimizations");

        await engine.ApplyAsync([tweak], new ApplyOptions());
        var now = (string)_reg.GetValue(RegHive.CurrentUser, DirectXSettingAction.Key, DirectXSettingAction.ValueName)!.Data;
        Assert.Contains("AutoHDREnable=1;", now);
        Assert.Contains("SwapEffectUpgradeEnable=1;", now);

        await engine.RevertAllAsync();
        Assert.Equal("AutoHDREnable=1;", _reg.GetValue(RegHive.CurrentUser, DirectXSettingAction.Key, DirectXSettingAction.ValueName)!.Data);
    }
}
