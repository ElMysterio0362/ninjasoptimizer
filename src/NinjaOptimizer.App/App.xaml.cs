using NinjaOptimizer.Core.Startup;
using NinjaOptimizer.Core.GameMode;
using NinjaOptimizer.Windows.GameMode;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.App.ViewModels;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Benchmark;
using NinjaOptimizer.Core.Maintenance;
using NinjaOptimizer.Windows.Maintenance;
using NinjaOptimizer.Core.Hardware;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Safety;
using NinjaOptimizer.Core.Settings;
using NinjaOptimizer.Windows.Hardware;
using NinjaOptimizer.Windows.Platform;

namespace NinjaOptimizer.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Formatação de números e datas no padrão do Windows do usuário (ex.: "3,8 GHz", "1.980 MHz").
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));
        _services = ConfigureServices();

        var log = _services.GetRequiredService<AppLog>();
        DispatcherUnhandledException += (_, ex) =>
        {
            log.Error("Exceção não tratada", ex.Exception);
            _services.GetRequiredService<DialogService>().Info("Algo deu errado", ex.Exception.Message, kind: DialogKind.Danger);
            ex.Handled = true;
        };
        log.Info($"Iniciado. Admin={_services.GetRequiredService<IElevationService>().IsElevated} Args={string.Join(' ', e.Args)}");

        var main = _services.GetRequiredService<MainViewModel>();
        var optimize = _services.GetRequiredService<OptimizeViewModel>();
        ApplyStartupArgs(e.Args, main, optimize);

        _services.GetRequiredService<GameModeViewModel>().Start();
        MainWindow = new MainWindow(main);
        MainWindow.Show();

        var settings = _services.GetRequiredService<AppSettings>();
        _services.GetRequiredService<ISensorService>().Start(TimeSpan.FromMilliseconds(Math.Max(500, settings.SensorIntervalMs)));

        _ = LoadSystemProfileAsync(optimize, log);
        await Task.WhenAll(
            optimize.RefreshStatusAsync(),
            _services.GetRequiredService<DashboardViewModel>().LoadAsync());
    }

    /// <summary>Usado pelas opções dos reparos: aplica um ajuste do catálogo pelo motor (fica no histórico com Desfazer).</summary>
    private static async Task<string> ApplyCatalogTweakAsync(IServiceProvider sp, string id, CancellationToken ct)
    {
        var engine = sp.GetRequiredService<OptimizationEngine>();
        var settings = sp.GetRequiredService<AppSettings>();
        var tweak = TweakCatalog.Get(id);
        var report = await engine.ApplyAsync([tweak], new ApplyOptions
        {
            CreateRestorePoint = settings.CreateRestorePoint,
            AbortIfRestorePointFails = settings.AbortIfRestorePointFails,
            ConfirmHighRisk = _ => Task.FromResult(true), // a opção de risco alto já foi confirmada no diálogo da tarefa
        }, ct);
        await Current.Dispatcher.InvokeAsync(() => sp.GetRequiredService<OptimizeViewModel>().RefreshStatusAsync()).Task.Unwrap();
        var r = report.Results.SingleOrDefault();
        return r?.Outcome switch
        {
            TweakOutcome.Applied => $"\"{tweak.Name}\" aplicado (pode desfazer no histórico).",
            TweakOutcome.AlreadyApplied => $"\"{tweak.Name}\" já estava assim.",
            _ => $"\"{tweak.Name}\" não foi aplicado: {report.AbortReason ?? r?.Message}",
        };
    }

    private static async Task<MaintenanceResult> RevertCatalogTweakAsync(IServiceProvider sp, string id, CancellationToken ct)
    {
        var engine = sp.GetRequiredService<OptimizationEngine>();
        var entry = engine.Journal.ActiveEntries.LastOrDefault(e => e.TweakId == id);
        if (entry is null) return new MaintenanceResult(true, 0, "O ajuste já não estava ativo.");
        var r = await engine.RevertAsync(entry.Id, ct);
        await Current.Dispatcher.InvokeAsync(() => sp.GetRequiredService<OptimizeViewModel>().RefreshStatusAsync()).Task.Unwrap();
        return new MaintenanceResult(r.Success, 0, r.Success ? "Ajuste desfeito. Rode o reparo de novo se ainda precisar." : r.Message);
    }

    private static async Task LoadSystemProfileAsync(OptimizeViewModel optimize, AppLog log)
    {
        try
        {
            var p = await NinjaOptimizer.Windows.Hardware.SystemProfileProbe.ReadAsync();
            log.Info($"Perfil do PC: disco do Windows={p.SystemDriveType} portátil={p.IsPortable} GPUs={string.Join(",", p.GpuVendors ?? new HashSet<GpuVendor>())} build={p.WindowsBuild}");
            optimize.SetSystemProfile(p);
        }
        catch (Exception ex) { log.Error("Ler perfil do PC", ex); }
    }

    /// <summary>Restaura página e seleção após reabrir como administrador.</summary>
    private static void ApplyStartupArgs(string[] args, MainViewModel main, OptimizeViewModel optimize)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--page") main.Navigate(args[i + 1]);
            if (args[i] == "--select") optimize.SelectByIds(args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var s = new ServiceCollection();

        // Plataforma Windows
        s.AddSingleton<IAppPaths, AppPaths>();
        s.AddSingleton<ICommandRunner, ProcessCommandRunner>();
        s.AddSingleton<IRegistryAccess, WindowsRegistryAccess>();
        s.AddSingleton<IServiceManager, WindowsServiceManager>();
        s.AddSingleton<IRestorePointService, WmiRestorePointService>();
        s.AddSingleton<IRegistryBackup, RegExeBackup>();
        s.AddSingleton<IElevationService, WindowsElevationService>();
        s.AddSingleton<IHardwareProvider, WmiHardwareProvider>();
        s.AddSingleton<ISensorService, LhmSensorService>();

        // Núcleo
        s.AddSingleton<SystemContext>();
        s.AddSingleton(sp => new ChangeJournal(Path.Combine(sp.GetRequiredService<IAppPaths>().DataFolder, "journal.json")));
        s.AddSingleton(sp => new SettingsStore(Path.Combine(sp.GetRequiredService<IAppPaths>().DataFolder, "settings.json")));
        s.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());
        s.AddSingleton(sp => new OptimizationEngine(
            sp.GetRequiredService<SystemContext>(),
            sp.GetRequiredService<ChangeJournal>(),
            sp.GetRequiredService<IRestorePointService>(),
            sp.GetRequiredService<IRegistryBackup>(),
            sp.GetRequiredService<IElevationService>(),
            sp.GetRequiredService<IAppPaths>()));

        // App
        s.AddSingleton<AppLog>();
        s.AddSingleton<DialogService>();
        s.AddSingleton<HardwareState>();
        s.AddSingleton<LiveSensors>();
        s.AddSingleton(sp => new MaintenanceStore(Path.Combine(sp.GetRequiredService<IAppPaths>().DataFolder, "maintenance.json")));
        s.AddSingleton<IReadOnlyList<MaintenanceTask>>(sp => WindowsMaintenanceCatalog.Build(
            sp.GetRequiredService<ICommandRunner>(), sp.GetRequiredService<IServiceManager>(), sp.GetRequiredService<IElevationService>(),
            sp.GetRequiredService<ChangeJournal>(), sp.GetRequiredService<IAppPaths>(),
            applyTweak: (id, ct) => ApplyCatalogTweakAsync(sp, id, ct),
            revertTweak: (id, ct) => RevertCatalogTweakAsync(sp, id, ct)));
        s.AddSingleton<CleanupViewModel>();
        s.AddSingleton(sp => new StartupManager(sp.GetRequiredService<IRegistryAccess>(), StartupFolders.Files));
        s.AddSingleton<StartupViewModel>();
        s.AddSingleton<DebloatViewModel>();
        s.AddSingleton<IProcessControl, WindowsProcessControl>();
        s.AddSingleton(sp => new GameModeService(sp.GetRequiredService<IProcessControl>(),
            Path.Combine(sp.GetRequiredService<IAppPaths>().DataFolder, "gamemode.json")));
        s.AddSingleton<GameModeViewModel>();
        s.AddSingleton<OptimizeViewModel>();
        s.AddSingleton<DashboardViewModel>();
        s.AddSingleton<HardwareViewModel>();
        s.AddSingleton(sp => new BenchmarkStore(Path.Combine(sp.GetRequiredService<IAppPaths>().DataFolder, "benchmarks.json")));
        s.AddSingleton(new BenchmarkOptions
        {
            // FILE_FLAG_NO_BUFFERING: mede o disco, não o cache de memória do Windows.
            DiskFileOptions = FileOptions.WriteThrough | (FileOptions)0x20000000,
        });
        s.AddSingleton<BenchmarkViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<MainViewModel>();

        return s.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<GameModeViewModel>()?.Shutdown(); // nada fica pausado depois que o app fecha
        _services?.Dispose(); // fecha o driver de sensores
        base.OnExit(e);
    }
}
