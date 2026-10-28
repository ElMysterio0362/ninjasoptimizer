using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using NinjasOptimizer.App.Services;
using NinjasOptimizer.App.ViewModels;
using NinjasOptimizer.Core.Abstractions;
using NinjasOptimizer.Core.Hardware;
using NinjasOptimizer.Core.Optimization;
using NinjasOptimizer.Core.Safety;
using NinjasOptimizer.Core.Settings;
using NinjasOptimizer.Windows.Hardware;
using NinjasOptimizer.Windows.Platform;

namespace NinjasOptimizer.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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

        MainWindow = new MainWindow(main);
        MainWindow.Show();

        var settings = _services.GetRequiredService<AppSettings>();
        _services.GetRequiredService<ISensorService>().Start(TimeSpan.FromMilliseconds(Math.Max(500, settings.SensorIntervalMs)));

        await Task.WhenAll(
            optimize.RefreshStatusAsync(),
            _services.GetRequiredService<DashboardViewModel>().LoadAsync());
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
        s.AddSingleton<OptimizeViewModel>();
        s.AddSingleton<DashboardViewModel>();
        s.AddSingleton<HardwareViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<MainViewModel>();

        return s.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose(); // fecha o driver de sensores
        base.OnExit(e);
    }
}
