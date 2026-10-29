using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Maintenance;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;
using NinjaOptimizer.Core.Safety;

namespace NinjaOptimizer.Core.Tests;

internal sealed class FakeCommands(Func<string, string, CommandResult>? handler = null) : ICommandRunner
{
    public List<string> Log { get; } = [];
    public Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        Log.Add($"{fileName} {arguments}".Trim());
        return Task.FromResult(handler?.Invoke(fileName, arguments) ?? new CommandResult(0, "", ""));
    }
}

/// <summary>Serviços com registro da ordem das paradas/partidas.</summary>
internal sealed class OrderedServices : IServiceManager
{
    public Dictionary<string, (ServiceStartMode Mode, bool Running)> S { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Log { get; } = [];
    public bool Exists(string n) => S.ContainsKey(n);
    public ServiceStartMode GetStartMode(string n) => S[n].Mode;
    public void SetStartMode(string n, ServiceStartMode m) { S[n] = (m, S[n].Running); Log.Add($"mode {n} {m}"); }
    public bool IsRunning(string n) => S[n].Running;
    public void Stop(string n) { S[n] = (S[n].Mode, false); Log.Add($"stop {n}"); }
    public void Start(string n) { S[n] = (S[n].Mode, true); Log.Add($"start {n}"); }
}

public sealed class ExtraFTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ninja-f-" + Guid.NewGuid().ToString("N"));
    public ExtraFTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData(0, 0, true, "Nenhum problema")]
    [InlineData(0, 1, true, "corrigidos")]
    [InlineData(0, 2, false, "não puderam")]
    [InlineData(5, 0, false, "DISM")]
    public void Repair_system_files_result_from_exit_codes(int dism, int sfc, bool ok, string text)
    {
        var r = RepairSystemFilesTask.Interpret(dism, sfc);
        Assert.Equal(ok, r.Success);
        Assert.Contains(text, r.Message);
    }

    [Fact]
    public async Task Repair_system_files_runs_dism_then_sfc()
    {
        var cmd = new FakeCommands();
        var sfcRan = false;
        var r = await new RepairSystemFilesTask(cmd, _ => { sfcRan = true; return Task.FromResult(0); }).RunAsync(null, default);
        Assert.True(r.Success);
        Assert.Equal(["DISM.exe /Online /Cleanup-Image /RestoreHealth"], cmd.Log);
        Assert.True(sfcRan);
    }

    [Fact]
    public async Task Recent_items_deletes_only_top_level_shortcuts()
    {
        var recent = Directory.CreateDirectory(Path.Combine(_dir, "Recent")).FullName;
        File.WriteAllText(Path.Combine(recent, "a.lnk"), "x");
        File.WriteAllText(Path.Combine(recent, "b.lnk"), "x");
        File.WriteAllText(Path.Combine(recent, "keep.txt"), "x");
        var auto = Directory.CreateDirectory(Path.Combine(recent, "AutomaticDestinations")).FullName;
        File.WriteAllText(Path.Combine(auto, "pinned.lnk"), "x");

        var task = new RecentItemsTask(recent);
        Assert.Equal(2, await task.EstimateAsync(default));
        var r = await task.RunAsync(null, default);
        Assert.Contains("2 item", r.Message);
        Assert.Equal(["keep.txt"], Directory.GetFiles(recent).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(auto, "pinned.lnk"))); // pastas fixadas continuam
    }

    [Fact]
    public async Task Windows_update_repair_renames_with_timestamp_and_restarts_services()
    {
        var sd = Directory.CreateDirectory(Path.Combine(_dir, "SoftwareDistribution")).FullName;
        var cr = Directory.CreateDirectory(Path.Combine(_dir, "catroot2")).FullName;
        Directory.CreateDirectory(sd + ".old"); // o .bat quebrava aqui
        var svc = new OrderedServices();
        svc.S["wuauserv"] = (ServiceStartMode.Manual, true);
        svc.S["bits"] = (ServiceStartMode.Manual, true);
        svc.S["cryptsvc"] = (ServiceStartMode.Automatic, true);
        var time = new FixedTime(new DateTimeOffset(2026, 10, 29, 14, 5, 9, TimeSpan.Zero));

        var r = await new RepairWindowsUpdateTask(svc, sd, cr, time).RunAsync(null, default);

        Assert.True(r.Success, r.Message);
        Assert.True(Directory.Exists(sd + ".bak-20261029-140509"));
        Assert.True(Directory.Exists(cr + ".bak-20261029-140509"));
        Assert.False(Directory.Exists(sd));
        Assert.All(svc.S.Values, v => Assert.True(v.Running));
        Assert.Equal(ServiceStartMode.Manual, svc.S["wuauserv"].Mode); // tipo de início não muda
        Assert.DoesNotContain(svc.Log, l => l.StartsWith("mode"));
    }

    [Fact]
    public async Task Windows_update_repair_restarts_services_even_if_rename_fails()
    {
        var svc = new OrderedServices();
        svc.S["wuauserv"] = (ServiceStartMode.Manual, true);
        svc.S["bits"] = (ServiceStartMode.Manual, true);
        var sd = Directory.CreateDirectory(Path.Combine(_dir, "SoftwareDistribution")).FullName;
        Directory.CreateDirectory(sd + ".bak-20261029-140509"); // destino ocupado → Move falha
        var r = await new RepairWindowsUpdateTask(svc, sd, Path.Combine(_dir, "nao-existe"),
            new FixedTime(new DateTimeOffset(2026, 10, 29, 14, 5, 9, TimeSpan.Zero))).RunAsync(null, default);
        Assert.False(r.Success);
        Assert.Contains("SoftwareDistribution", r.Message);
        Assert.All(svc.S.Values, v => Assert.True(v.Running));
    }

    [Fact]
    public async Task Network_repair_saves_config_first_and_warns_about_app_tweaks()
    {
        var cmd = new FakeCommands((f, a) => f == "ipconfig" && a == "/all" ? new(0, "IPv4: 192.168.0.50", "") : new(0, "", ""));
        var logs = Path.Combine(_dir, "logs");
        var task = new RepairNetworkTask(cmd, logs, () => new HashSet<string> { "network.dns-cloudflare" });
        Assert.Contains("DNS/Nagle", task.Note);
        Assert.DoesNotContain("DNS/Nagle", new RepairNetworkTask(cmd, logs, () => new HashSet<string>()).Note);

        var r = await task.RunAsync(null, default);
        Assert.True(r.Success);
        Assert.True(r.RequiresRestart);
        Assert.Equal("ipconfig /all", cmd.Log[0]);
        Assert.Contains("netsh winsock reset", cmd.Log);
        Assert.Contains(cmd.Log, l => l.StartsWith("netsh int ip reset") && l.Contains("netsh-ip-reset.log"));
        Assert.DoesNotContain(cmd.Log, l => l.Contains("/release") || l.Contains("/renew"));
        Assert.Contains("192.168.0.50", File.ReadAllText(Directory.GetFiles(logs, "rede-antes-*").Single()));
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(3, false, true)]
    public async Task Check_disk_maps_exit_codes_and_offers_spotfix_only_on_3(int exit, bool ok, bool followUp)
    {
        var cmd = new FakeCommands((f, _) => f == "chkdsk.exe" ? new(exit, "", "") : new(0, "", ""));
        var r = await new CheckDiskTask(cmd, "d:", () => "NTFS").RunAsync(null, default);
        Assert.Equal(ok, r.Success);
        Assert.Equal(followUp, r.FollowUp is not null);
        Assert.Equal("chkdsk.exe D: /scan", cmd.Log[0]);
        Assert.DoesNotContain(cmd.Log, l => l.Contains("/f") || l.Contains("/r"));
        if (r.FollowUp is { } next)
        {
            var r2 = await next.Run(default);
            Assert.True(r2.RequiresRestart);
            Assert.Contains("Repair-Volume -DriveLetter D -SpotFix", cmd.Log[^1]);
        }
    }

    [Fact]
    public async Task Check_disk_refuses_non_ntfs_and_bad_drive_names()
    {
        var cmd = new FakeCommands();
        var r = await new CheckDiskTask(cmd, "C:", () => "FAT32").RunAsync(null, default);
        Assert.False(r.Success);
        Assert.Empty(cmd.Log);
        Assert.Throws<ArgumentException>(() => new CheckDiskTask(cmd, "C: & del", () => "NTFS"));
    }

    [Fact]
    public async Task Restart_audio_uses_the_right_order_and_fixes_disabled()
    {
        var svc = new OrderedServices();
        svc.S["Audiosrv"] = (ServiceStartMode.Disabled, false);
        svc.S["AudioEndpointBuilder"] = (ServiceStartMode.Automatic, true);
        var r = await new RestartAudioTask(svc).RunAsync(null, default);
        Assert.True(r.Success);
        Assert.Equal(["mode Audiosrv Automatic", "stop AudioEndpointBuilder", "start AudioEndpointBuilder", "start Audiosrv"], svc.Log);
        Assert.Contains("Audiosrv: Desativado → Automático", r.Message);
    }

    [Fact]
    public async Task Restore_default_services_only_touches_disabled_and_not_the_ones_the_app_disabled()
    {
        var svc = new OrderedServices();
        svc.S["bthserv"] = (ServiceStartMode.Disabled, false);
        svc.S["WlanSvc"] = (ServiceStartMode.Manual, false);       // não está desativado: não mexe
        svc.S["XblGameSave"] = (ServiceStartMode.Disabled, false); // desativado por um ajuste do app: não mexe
        svc.S["Dhcp"] = (ServiceStartMode.Disabled, false);
        var r = await new RestoreDefaultServicesTask(svc, () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "xblgamesave" }).RunAsync(null, default);

        Assert.Equal(ServiceStartMode.Manual, svc.S["bthserv"].Mode);
        Assert.Equal(ServiceStartMode.Automatic, svc.S["Dhcp"].Mode);
        Assert.Equal(ServiceStartMode.Manual, svc.S["WlanSvc"].Mode);
        Assert.Equal(ServiceStartMode.Disabled, svc.S["XblGameSave"].Mode);
        Assert.Contains("bthserv: Desativado → Manual", r.Message);
        Assert.DoesNotContain(DefaultServices.Essential, e => e.Name == "NlaSvc");
    }

    [Fact]
    public async Task Repair_store_reregisters_only_store_and_app_installer_for_current_user()
    {
        var cmd = new FakeCommands();
        var svc = new OrderedServices();
        svc.S["ClipSVC"] = (ServiceStartMode.Disabled, false);
        var r = await new RepairStoreTask(cmd, svc, new FakeElevation(false), () => new HashSet<string>()).RunAsync(null, default);
        Assert.Equal(ServiceStartMode.Disabled, svc.S["ClipSVC"].Mode); // sem admin não mexe, só avisa
        Assert.Contains("administrador", r.Message);
        Assert.Equal("wsreset.exe", cmd.Log[0]);
        Assert.Contains("Microsoft.WindowsStore", cmd.Log[1]);
        Assert.Contains("Microsoft.DesktopAppInstaller", cmd.Log[1]);
        Assert.DoesNotContain("-AllUsers", cmd.Log[1]);

        await new RepairStoreTask(cmd, svc, new FakeElevation(true), () => new HashSet<string>()).RunAsync(null, default);
        Assert.Equal(ServiceStartMode.Manual, svc.S["ClipSVC"].Mode);
    }

    [Fact]
    public void Services_changed_by_active_app_tweaks_are_found_in_the_journal()
    {
        var journal = new ChangeJournal(Path.Combine(_dir, "j.json"));
        var snap = new ActionSnapshot { Kind = ServiceStartupAction.KindId };
        snap.Data["service"] = "SysMain";
        journal.Add(new JournalEntry { TweakId = "services.sysmain", TweakName = "x", Status = JournalStatus.Applied, Snapshots = [snap] });
        var reverted = new ActionSnapshot { Kind = ServiceStartupAction.KindId };
        reverted.Data["service"] = "WSearch";
        journal.Add(new JournalEntry { TweakId = "services.search-indexer", TweakName = "y", Status = JournalStatus.Reverted, Snapshots = [reverted] });

        Assert.Equal(["SysMain"], JournalQueries.ServicesChangedByApp(journal));
        Assert.Equal(["services.sysmain"], JournalQueries.ActiveTweakIds(journal));
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
