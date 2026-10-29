using System.Text.Json;
using System.Text.RegularExpressions;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Debloat;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Safety;

namespace NinjaOptimizer.Core.Tests;

/// <summary>Simula Get-AppxPackage, remoção, provisionados, winget e a Store.</summary>
internal sealed class FakeAppx : ICommandRunner
{
    public List<InstalledApp> Installed { get; } = [];
    public Dictionary<string, string> Provisioned { get; } = new(StringComparer.OrdinalIgnoreCase); // nome → pacote
    public HashSet<string> WingetCatalog { get; } = [];
    public bool OneDrive { get; set; } = true;
    public List<string> Log { get; } = [];

    public static InstalledApp App(string name, bool framework = false, bool nonRemovable = false, string sig = "Store") =>
        new(name, $"{name}_1.0.0.0_x64__8wekyb3d8bbwe", $"{name}_8wekyb3d8bbwe", framework, nonRemovable, sig);

    public Task<CommandResult> RunAsync(string f, string a, CancellationToken ct = default)
    {
        Log.Add($"{f} {a}");
        CommandResult ok(string o = "") => new(0, o, "");
        if (f == "powershell" && a.Contains("Get-AppxPackage |"))
            return Task.FromResult(ok(JsonSerializer.Serialize(Installed.Select(i => new
            { i.Name, i.PackageFullName, i.PackageFamilyName, i.IsFramework, i.NonRemovable, i.SignatureKind }))));
        if (f == "powershell" && a.Contains("Get-AppxProvisionedPackage"))
        {
            var name = Regex.Match(a, @"-eq '([^']+)'").Groups[1].Value;
            return Task.FromResult(ok(Provisioned.TryGetValue(name, out var pkg) ? $"{pkg}|%SYSTEMDRIVE%\\Program Files\\WindowsApps\\{pkg}\r\n" : ""));
        }
        if (f == "powershell" && a.Contains("Remove-AppxProvisionedPackage"))
        {
            var pkg = Regex.Match(a, @"-PackageName '([^']+)'").Groups[1].Value;
            foreach (var k in Provisioned.Where(kv => kv.Value == pkg).Select(kv => kv.Key).ToList()) Provisioned.Remove(k);
            return Task.FromResult(ok());
        }
        if (f == "powershell" && a.Contains("Remove-AppxPackage"))
        {
            var full = Regex.Match(a, @"-Package '([^']+)'").Groups[1].Value;
            return Task.FromResult(Installed.RemoveAll(i => i.PackageFullName == full) > 0 ? ok() : new(1, "", "não encontrado"));
        }
        if (f == "powershell" && a.Contains("Add-AppxPackage -Register"))
        {
            var pkg = Regex.Match(a, @"WindowsApps\\([^\\']+)\\").Groups[1].Value;
            var name = Provisioned.FirstOrDefault(kv => kv.Value == pkg).Key;
            if (name is null) return Task.FromResult(new CommandResult(1, "", "manifesto não encontrado"));
            Installed.Add(App(name));
            return Task.FromResult(ok());
        }
        if (f == "winget" && a.StartsWith("install --id Microsoft.OneDrive")) { OneDrive = true; return Task.FromResult(ok()); }
        if (f == "winget" && a.StartsWith("uninstall --id Microsoft.OneDrive")) { OneDrive = false; return Task.FromResult(ok()); }
        if (f == "winget" && a.StartsWith("list --id Microsoft.OneDrive")) return Task.FromResult(OneDrive ? ok() : new(-1978335212, "", ""));
        if (f == "winget" && a.StartsWith("install --id "))
        {
            var id = a.Split(' ')[2];
            if (!WingetCatalog.Contains(id)) return Task.FromResult(new CommandResult(-1, "", "não encontrado"));
            var entry = AppCatalog.Known.First(k => k.StoreId == id);
            Installed.Add(App(entry.Names[0]));
            return Task.FromResult(ok());
        }
        if (f == "explorer.exe") return Task.FromResult(ok());
        return Task.FromResult(new CommandResult(1, "", "desconhecido: " + a));
    }
}

public sealed class DebloatTests : IDisposable
{
    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    private (OptimizationEngine Engine, FakeAppx Appx) New()
    {
        var appx = new FakeAppx();
        var ctx = new SystemContext(new FakeRegistry(), new FakeServices(), appx);
        return (new OptimizationEngine(ctx, new ChangeJournal(Path.Combine(_paths.DataFolder, Guid.NewGuid() + ".json")),
            new FakeRestorePoint(), new FakeBackup(), new FakeElevation(), _paths), appx);
    }

    private static readonly ApplyOptions Confirm = new() { ConfirmHighRisk = _ => Task.FromResult(true) };

    [Fact]
    public void Never_lists_protected_frameworks_system_or_invalid_packages()
    {
        var list = Appx.Removable([
            FakeAppx.App("Microsoft.BingNews"),
            FakeAppx.App("Microsoft.WindowsStore"), FakeAppx.App("Microsoft.DesktopAppInstaller"), FakeAppx.App("Microsoft.SecHealthUI"),
            FakeAppx.App("MicrosoftWindows.Client.CBS"), FakeAppx.App("Microsoft.HEVCVideoExtension"), FakeAppx.App("Microsoft.WebpImageExtension"),
            FakeAppx.App("Microsoft.VCLibs.140.00"), FakeAppx.App("Microsoft.UI.Xaml.2.8"), FakeAppx.App("Microsoft.WindowsCalculator"),
            FakeAppx.App("Some.Framework", framework: true), FakeAppx.App("Some.Locked", nonRemovable: true), FakeAppx.App("Some.Sys", sig: "System"),
            FakeAppx.App("Bad'; Remove-Item C:\\"),
            FakeAppx.App("SpotifyAB.SpotifyMusic"),
            FakeAppx.App("Microsoft.Windows.Photos"),
        ]);
        Assert.Equal(["Microsoft.BingNews", "SpotifyAB.SpotifyMusic", "Microsoft.Windows.Photos"], list.Select(a => a.Name));
    }

    [Fact]
    public void Protected_package_and_invalid_names_are_refused_by_the_action()
    {
        Assert.Throws<ArgumentException>(() => new AppxRemoveAction(FakeAppx.App("Microsoft.WindowsStore"), false, null));
        Assert.Throws<ArgumentException>(() => new AppxRemoveAction(FakeAppx.App("x' -Force; rm"), false, null));
        Assert.Throws<ArgumentException>(() => new AppxRemoveAction(FakeAppx.App("a b"), false, null));
    }

    [Fact]
    public void No_wildcards_third_party_apps_are_unknown_not_matched()
    {
        Assert.Null(AppCatalog.Find("SpotifyAB.SpotifyMusic"));
        var t = DebloatTweaks.Remove(FakeAppx.App("SpotifyAB.SpotifyMusic"), false);
        Assert.Equal(RiskLevel.High, t.Risk);
        Assert.Contains("não conhece", string.Concat(t.Consequences));
    }

    [Fact]
    public async Task Removal_goes_to_journal_and_undo_uses_the_provisioned_package_first()
    {
        var (engine, appx) = New();
        appx.Installed.Add(FakeAppx.App("Microsoft.BingNews"));
        appx.Provisioned["Microsoft.BingNews"] = "Microsoft.BingNews_1.0.0.0_neutral__8wekyb3d8bbwe";
        appx.WingetCatalog.Add("9WZDNCRFHVFW");

        var r = await engine.ApplyAsync([DebloatTweaks.Remove(appx.Installed[0], false)], Confirm);
        Assert.Equal(TweakOutcome.Applied, r.Results.Single().Outcome);
        Assert.Empty(appx.Installed);
        Assert.True(appx.Provisioned.ContainsKey("Microsoft.BingNews")); // não removeu das contas novas

        var undo = await engine.RevertAllAsync();
        Assert.True(undo.Single().Success, undo.Single().Message);
        Assert.Single(appx.Installed);
        Assert.Contains(appx.Log, l => l.Contains("Add-AppxPackage -Register") && l.Contains("-DisableDevelopmentMode"));
        Assert.DoesNotContain(appx.Log, l => l.StartsWith("winget install"));
    }

    [Fact]
    public async Task Undo_falls_back_to_winget_when_not_provisioned()
    {
        var (engine, appx) = New();
        appx.Installed.Add(FakeAppx.App("Microsoft.BingWeather"));
        appx.WingetCatalog.Add("9WZDNCRFJ3Q2");
        await engine.ApplyAsync([DebloatTweaks.Remove(appx.Installed[0], false)], Confirm);

        Assert.True((await engine.RevertAllAsync()).Single().Success);
        Assert.Contains(appx.Log, l => l == "winget install --id 9WZDNCRFJ3Q2 --source msstore --accept-package-agreements --accept-source-agreements --silent");
    }

    [Fact]
    public async Task Undo_without_store_id_opens_the_store_page_and_keeps_the_entry()
    {
        var (engine, appx) = New();
        appx.Installed.Add(FakeAppx.App("Microsoft.GetHelp"));
        await engine.ApplyAsync([DebloatTweaks.Remove(appx.Installed[0], false)], Confirm);

        var undo = (await engine.RevertAllAsync()).Single();
        Assert.False(undo.Success);
        Assert.Contains("Store", undo.Message);
        Assert.Contains(appx.Log, l => l == "explorer.exe ms-windows-store://pdp/?PFN=Microsoft.GetHelp_8wekyb3d8bbwe");
        Assert.Single(engine.Journal.ActiveEntries); // continua no histórico para tentar de novo
    }

    [Fact]
    public async Task Remove_for_new_accounts_is_high_risk_admin_and_removes_provisioned()
    {
        var (engine, appx) = New();
        appx.Installed.Add(FakeAppx.App("Microsoft.BingNews"));
        appx.Provisioned["Microsoft.BingNews"] = "Microsoft.BingNews_1.0.0.0_neutral__8wekyb3d8bbwe";
        var tweak = DebloatTweaks.Remove(appx.Installed[0], alsoProvisioned: true);
        Assert.Equal(RiskLevel.High, tweak.Risk);
        Assert.True(tweak.RequiresAdmin);
        Assert.Contains("Contas criadas depois", string.Concat(tweak.Consequences));

        // Sem confirmação, risco alto nunca é aplicado
        Assert.Equal(TweakOutcome.SkippedHighRisk, (await engine.ApplyAsync([tweak], new ApplyOptions())).Results.Single().Outcome);
        await engine.ApplyAsync([tweak], Confirm);
        Assert.Empty(appx.Provisioned);
    }

    [Fact]
    public async Task OneDrive_uninstalls_and_reinstalls_with_winget()
    {
        var (engine, appx) = New();
        await engine.ApplyAsync([DebloatTweaks.RemoveOneDrive()], Confirm);
        Assert.False(appx.OneDrive);
        Assert.True((await engine.RevertAllAsync()).Single().Success);
        Assert.True(appx.OneDrive);
    }

    [Fact]
    public void Every_catalog_entry_has_consequences_and_valid_ids()
    {
        Assert.All(AppCatalog.Known, e =>
        {
            Assert.NotEmpty(e.Consequences);
            Assert.All(e.Names, n => Assert.True(AppCatalog.IsValidPackageName(n), n));
            Assert.All(e.Names, n => Assert.False(AppCatalog.IsProtected(n), n));
            if (e.StoreId is not null) Assert.True(AppCatalog.IsValidStoreId(e.StoreId), e.StoreId);
        });
        Assert.NotEmpty(DebloatTweaks.RemoveOneDrive().Consequences);
    }

    [Fact]
    public void Parse_handles_single_object_and_arrays()
    {
        Assert.Single(Appx.Parse("{\"Name\":\"A\",\"PackageFullName\":\"A_1\",\"PackageFamilyName\":\"A_x\",\"IsFramework\":false,\"NonRemovable\":false,\"SignatureKind\":\"Store\"}"));
        Assert.Empty(Appx.Parse(""));
    }
}
