using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Hardware;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Tests;

public sealed class ExtraJTests
{
    private static (SystemContext Ctx, FakeRegistry Reg, FakePowerCfg Cmd) New(SystemProfile? profile = null)
    {
        var reg = new FakeRegistry();
        var cmd = new FakePowerCfg { Registry = reg };
        return (new SystemContext(reg, new FakeServices(), cmd) { Profile = profile ?? Fakes.FullProfile }, reg, cmd);
    }

    [Fact]
    public async Task Bcd_with_previous_value_restores_it()
    {
        var (ctx, _, cmd) = New();
        cmd.Bcd["hypervisorlaunchtype"] = "Auto";
        var a = new BcdSettingAction("hypervisorlaunchtype", "Off", "x");
        var snap = await a.CaptureAsync(ctx, default);
        Assert.Equal("Auto", snap.Get("prev"));
        await a.ApplyAsync(ctx, snap, default);
        Assert.True(await a.IsAppliedAsync(ctx, default));
        await BcdSettingAction.RevertAsync(ctx, snap, default);
        Assert.Equal("Auto", cmd.Bcd["hypervisorlaunchtype"]);
        Assert.Contains("/set {current} hypervisorlaunchtype Auto", cmd.Log);
    }

    [Fact]
    public async Task Bcd_without_previous_value_reverts_with_deletevalue()
    {
        var (ctx, _, cmd) = New();
        cmd.Bcd.Clear();
        var a = new BcdSettingAction("hypervisorlaunchtype", "Off", "x");
        var snap = await a.CaptureAsync(ctx, default);
        Assert.Null(snap.Get("prev"));
        await a.ApplyAsync(ctx, snap, default);
        await BcdSettingAction.RevertAsync(ctx, snap, default);
        Assert.Empty(cmd.Bcd);
        Assert.Contains("/deletevalue {current} hypervisorlaunchtype", cmd.Log);
    }

    [Fact]
    public void Bcd_parse_ignores_translated_text_and_similar_names()
    {
        const string output = "Carregador de Inicialização do Windows\r\nidentificador           {current}\r\nhypervisorlaunchtypex    Nope\r\nhypervisorlaunchtype    Off\r\n";
        Assert.Equal("Off", BcdSettingAction.Parse(output, "hypervisorlaunchtype"));
        Assert.Null(BcdSettingAction.Parse("identificador {current}", "hypervisorlaunchtype"));
    }

    [Theory]
    [InlineData("hypervisor launchtype", "Off")]
    [InlineData("hypervisorlaunchtype", "Off & del")]
    [InlineData("", "Off")]
    public void Bcd_rejects_invalid_elements_and_values(string e, string v) =>
        Assert.Throws<ArgumentException>(() => new BcdSettingAction(e, v, "x"));

    [Fact]
    public async Task Bcd_tampered_snapshot_is_refused()
    {
        var (ctx, _, _) = New();
        var a = new BcdSettingAction("hypervisorlaunchtype", "Off", "x");
        var snap = await a.CaptureAsync(ctx, default);
        snap.Data["prev"] = "Auto & format c:";
        await Assert.ThrowsAsync<InvalidOperationException>(() => BcdSettingAction.RevertAsync(ctx, snap, default));
    }

    [Fact]
    public void Memory_integrity_blocked_by_policy_or_uefi_lock()
    {
        var mi = TweakCatalog.Get("security.memory-integrity-off");
        var (ctx, reg, _) = New();
        Assert.True(mi.CheckConditions(ctx).Applicable);

        reg.SetValue(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard", "EnableVirtualizationBasedSecurity", RegValue.Dword(1));
        Assert.Contains("política", mi.CheckConditions(ctx).Reason);

        var (ctx2, reg2, _) = New();
        reg2.SetValue(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Locked", RegValue.Dword(1));
        Assert.Contains("UEFI", mi.CheckConditions(ctx2).Reason);
    }

    [Fact]
    public void Credential_guard_only_when_running_and_not_uefi_locked()
    {
        var cg = TweakCatalog.Get("security.credential-guard-off");
        Assert.True(cg.CheckConditions(New().Ctx).Applicable);
        Assert.Contains("não está ligado", cg.CheckConditions(New(Fakes.FullProfile with { CredentialGuardRunning = false }).Ctx).Reason);
        Assert.Contains("não foi possível", cg.CheckConditions(New(Fakes.FullProfile with { CredentialGuardRunning = null }).Ctx).Reason, StringComparison.OrdinalIgnoreCase);
        var (ctx, reg, _) = New();
        reg.SetValue(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags", RegValue.Dword(1));
        Assert.Contains("UEFI", cg.CheckConditions(ctx).Reason);
    }

    [Fact]
    public void Cortana_only_on_windows_10()
    {
        var t = TweakCatalog.Get("privacy.cortana-off");
        Assert.False(t.CheckConditions(New().Ctx).Applicable);
        Assert.True(t.CheckConditions(New(Fakes.FullProfile with { WindowsBuild = 19045 }).Ctx).Applicable);
    }

    [Fact]
    public void Context_warnings_show_only_where_they_apply()
    {
        var prefetch = TweakCatalog.Get("disk.prefetcher-off");
        Assert.DoesNotContain(prefetch.ConsequencesFor(New().Ctx), c => c.Contains("num HD"));
        Assert.Contains(prefetch.ConsequencesFor(New(Fakes.FullProfile with { SystemDriveType = DiskMediaType.Hdd }).Ctx), c => c.Contains("num HD"));

        var wu = TweakCatalog.Get("update.block-internet-locations");
        var (ctx, reg, _) = New();
        Assert.Contains(wu.ConsequencesFor(ctx), c => c.Contains("não usa WSUS"));
        reg.SetValue(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer", RegValue.Str("http://wsus"));
        Assert.DoesNotContain(wu.ConsequencesFor(ctx), c => c.Contains("não usa WSUS"));
    }

    [Fact]
    public void New_items_of_this_part_are_outside_all_profiles()
    {
        string[] ids = ["security.memory-integrity-off", "security.credential-guard-off", "security.hypervisor-off", "services.biometrics-off",
            "gaming.xbox-services-off", "privacy.cortana-off", "system.taskbar-search-hidden", "disk.prefetcher-off", "compat.pca-off",
            "compat.engine-off", "update.block-internet-locations"];
        Assert.All(ids, id => Assert.Empty(TweakCatalog.Get(id).Profiles));
    }
}
