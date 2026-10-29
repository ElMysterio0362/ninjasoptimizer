using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Maintenance;
using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Tests;

internal sealed class FakeCloser : IAppCloser
{
    public List<(int Pid, string Name)> Procs { get; } = [];
    public HashSet<int> Stubborn { get; } = [];
    public List<string> Log { get; } = [];
    public IReadOnlyList<(int Pid, string Name)> Running(IEnumerable<string> names) =>
        Procs.Where(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    public bool CloseGracefully(int pid, TimeSpan wait) { Log.Add($"close {pid}"); if (Stubborn.Contains(pid)) return false; Procs.RemoveAll(p => p.Pid == pid); return true; }
    public void Kill(int pid) { Log.Add($"kill {pid}"); Procs.RemoveAll(p => p.Pid == pid); }
}

internal sealed class FakeUi(IReadOnlyList<string>? choose, bool confirm) : IMaintenanceUi
{
    public List<string> Asked { get; } = [];
    public Task<IReadOnlyList<string>?> ChooseAsync(string title, string message, IReadOnlyList<string> items, string confirmText)
    { Asked.Add("choose:" + string.Join(",", items)); return Task.FromResult(choose); }
    public Task<bool> ConfirmAsync(string title, string message, IReadOnlyList<string> details, string confirmText)
    { Asked.Add("confirm:" + string.Join(",", details)); return Task.FromResult(confirm); }
}

public sealed class ExtraKTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ninja-k-" + Guid.NewGuid().ToString("N"));
    public ExtraKTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Network_repair_renews_ip_only_when_option_is_checked()
    {
        var cmd = new FakeCommands();
        var t = new RepairNetworkTask(cmd, _dir, () => new HashSet<string>());
        await t.RunAsync(null, default);
        Assert.DoesNotContain(cmd.Log, l => l.Contains("/release"));
        t.SelectedOptions.Add(RepairNetworkTask.Renew);
        await t.RunAsync(null, default);
        Assert.Contains("ipconfig /release", cmd.Log);
        Assert.Contains("ipconfig /renew", cmd.Log);
    }

    [Fact]
    public async Task Recent_items_jumplists_option_also_clears_destinations()
    {
        var recent = Directory.CreateDirectory(Path.Combine(_dir, "Recent")).FullName;
        var auto = Directory.CreateDirectory(Path.Combine(recent, "AutomaticDestinations")).FullName;
        File.WriteAllText(Path.Combine(auto, "x.automaticDestinations-ms"), "x");
        var t = new RecentItemsTask(recent);
        await t.RunAsync(null, default);
        Assert.Single(Directory.GetFiles(auto));
        t.SelectedOptions.Add(RecentItemsTask.JumpLists);
        await t.RunAsync(null, default);
        Assert.Empty(Directory.GetFiles(auto));
    }

    [Fact]
    public async Task Check_disk_full_option_schedules_f_r_with_the_local_yes_letter()
    {
        var cmd = new FakeCommands();
        var t = new CheckDiskTask(cmd, "C:", () => "NTFS", 's');
        Assert.False(t.HasSideEffects);
        Assert.Empty(t.Consequences);
        t.SelectedOptions.Add(CheckDiskTask.FullAtBoot);
        Assert.NotEmpty(t.Consequences);
        var r = await t.RunAsync(null, default);
        Assert.True(r.RequiresRestart);
        Assert.Equal(["cmd.exe /c echo S| chkdsk.exe C: /f /r"], cmd.Log);
        Assert.Throws<ArgumentException>(() => new CheckDiskTask(cmd, "C:", () => "NTFS", '&'));
    }

    [Fact]
    public async Task Store_all_apps_option_needs_admin()
    {
        var cmd = new FakeCommands();
        var t = new RepairStoreTask(cmd, new OrderedServices(), new FakeElevation(false), () => new HashSet<string>());
        t.SelectedOptions.Add(RepairStoreTask.AllApps);
        var r = await t.RunAsync(null, default);
        Assert.Contains("administrador", r.Message);
        Assert.DoesNotContain(cmd.Log, l => l.Contains("-AllUsers"));
        await new RepairStoreTask(cmd, new OrderedServices(), new FakeElevation(true), () => new HashSet<string>()) { SelectedOptions = { RepairStoreTask.AllApps } }.RunAsync(null, default);
        Assert.Contains(cmd.Log, l => l.Contains("Get-AppxPackage -AllUsers"));
    }

    [Fact]
    public async Task Xbox_repair_offers_undo_when_the_app_tweak_is_active()
    {
        var undone = false;
        var t = new RepairXboxAppsTask(new FakeCommands(), new OrderedServices(), () => true,
            _ => { undone = true; return Task.FromResult(new MaintenanceResult(true, 0, "ok")); });
        var r = await t.RunAsync(null, default);
        Assert.False(r.Success);
        Assert.NotNull(r.FollowUp);
        await r.FollowUp!.Run(default);
        Assert.True(undone);
    }

    [Fact]
    public async Task Xbox_repair_restores_disabled_services_to_manual_and_reregisters_only_xbox_packages()
    {
        var svc = new OrderedServices();
        svc.S["XblAuthManager"] = (ServiceStartMode.Disabled, false);
        svc.S["XboxGipSvc"] = (ServiceStartMode.Manual, true);
        var cmd = new FakeCommands();
        var r = await new RepairXboxAppsTask(cmd, svc, () => false, _ => throw new InvalidOperationException()).RunAsync(null, default);
        Assert.True(r.Success);
        Assert.Equal(ServiceStartMode.Manual, svc.S["XblAuthManager"].Mode);
        Assert.Contains("stop XboxGipSvc", svc.Log);
        Assert.Contains("start XboxGipSvc", svc.Log);
        Assert.Equal(4, cmd.Log.Count);
        Assert.All(cmd.Log, l => Assert.Contains("Get-AppxPackage Microsoft.", l));
        Assert.DoesNotContain(cmd.Log, l => l.Contains("-AllUsers"));
    }

    [Fact]
    public async Task Camera_repair_asks_before_closing_and_forces_only_when_told()
    {
        var closer = new FakeCloser();
        closer.Procs.AddRange([(1, "Discord"), (2, "Zoom"), (3, "notepad")]);
        closer.Stubborn.Add(2);
        var svc = new OrderedServices();
        svc.S["FrameServer"] = (ServiceStartMode.Manual, true);
        var cmd = new FakeCommands();
        var applied = new List<string>();
        var t = new DeviceRepairTask(DeviceRepairKind.Camera, cmd, svc, closer, (id, _) => { applied.Add(id); return Task.FromResult("ok"); })
        {
            Ui = new FakeUi(["Discord", "Zoom"], confirm: false),
        };
        var r = await t.RunAsync(null, default);

        Assert.Equal(["close 1", "close 2"], closer.Log); // não forçou, porque o usuário disse não
        Assert.Contains(closer.Procs, p => p.Name == "notepad");
        Assert.Contains("stop FrameServer", svc.Log);
        Assert.Contains("start FrameServer", svc.Log);
        Assert.DoesNotContain(svc.Log, l => l.StartsWith("mode")); // tipo de início não muda
        Assert.Contains(cmd.Log, l => l.Contains("Get-PnpDevice -Class 'Camera','Image'") && l.Contains("Disable-PnpDevice"));
        Assert.Contains("pnputil /scan-devices", cmd.Log);
        Assert.Contains("explorer.exe ms-settings:privacy-webcam", cmd.Log);
        Assert.Empty(applied); // opções avançadas desmarcadas
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Camera_repair_without_ui_closes_nothing_and_options_become_tweaks()
    {
        var closer = new FakeCloser();
        closer.Procs.Add((1, "Discord"));
        var applied = new List<string>();
        var t = new DeviceRepairTask(DeviceRepairKind.Camera, new FakeCommands(), new OrderedServices(), closer,
            (id, _) => { applied.Add(id); return Task.FromResult("ok"); });
        t.SelectedOptions.UnionWith([DeviceRepairTask.AllowApps, DeviceRepairTask.RemovePolicy]);
        await t.RunAsync(null, default);
        Assert.Empty(closer.Log);
        Assert.Equal(["privacy.camera-allow-apps", "privacy.camera-remove-policy"], applied);
    }

    [Fact]
    public async Task Microphone_repair_includes_media_only_with_the_option()
    {
        var cmd = new FakeCommands();
        var svc = new OrderedServices();
        svc.S["Audiosrv"] = (ServiceStartMode.Automatic, true);
        svc.S["AudioEndpointBuilder"] = (ServiceStartMode.Automatic, true);
        var t = new DeviceRepairTask(DeviceRepairKind.Microphone, cmd, svc, new FakeCloser(), (_, _) => Task.FromResult(""));
        await t.RunAsync(null, default);
        Assert.Contains(cmd.Log, l => l.Contains("-Class 'AudioEndpoint' "));
        Assert.Equal(["stop Audiosrv", "stop AudioEndpointBuilder", "start AudioEndpointBuilder", "start Audiosrv"], svc.Log);
        t.SelectedOptions.Add(DeviceRepairTask.ExtraClass);
        await t.RunAsync(null, default);
        Assert.Contains(cmd.Log, l => l.Contains("-Class 'AudioEndpoint','MEDIA'"));
        Assert.Contains("explorer.exe ms-settings:sound", cmd.Log);
    }

    [Fact]
    public async Task Force_shutdown_and_restart_commands()
    {
        var cmd = new FakeCommands();
        var t = new ForceShutdownTask(cmd);
        Assert.Contains("sem salvar", string.Concat(t.Consequences));
        await t.RunAsync(null, default);
        await Restart.NowAsync(cmd);
        Assert.Equal(["shutdown /s /f /t 0", "shutdown /r /t 0"], cmd.Log);
    }

    [Fact]
    public async Task Registry_delete_action_restores_the_deleted_policy_on_undo()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessCamera", RegValue.Dword(2));
        var ctx = new SystemContext(reg, new FakeServices(), new FakeCommands());
        var a = new RegistryDeleteValueAction(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessCamera");
        var snap = await a.CaptureAsync(ctx, default);
        await a.ApplyAsync(ctx, snap, default);
        Assert.True(await a.IsAppliedAsync(ctx, default));
        await ActionReverter.RevertAsync(ctx, snap, default);
        Assert.Equal(2u, reg.GetValue(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessCamera")!.Data);
    }
}
