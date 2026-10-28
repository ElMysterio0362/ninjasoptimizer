using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.App.ViewModels;

public sealed record NavItem(string Key, string Title, string Icon, object Page);

public sealed partial class MainViewModel : ObservableObject
{
    public IReadOnlyList<NavItem> NavItems { get; }
    public IReadOnlyList<NavItem> FooterItems { get; }
    public bool IsElevated { get; }
    public string FooterText => $"Versão {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} · " +
        (IsElevated ? "admin" : "usuário");

    private NavItem _current;

    /// <summary>Página atual. Ignora null (vem da lista lateral que não contém o item selecionado).</summary>
    public NavItem? Current
    {
        get => _current;
        set
        {
            if (value is null || ReferenceEquals(value, _current)) return;
            _current = value;
            OnPropertyChanged();
            if (value.Page is HardwareViewModel hw) _ = hw.LoadAsync();
            if (value.Page is GameModeViewModel gm) _ = gm.RefreshAppsAsync();
        }
    }

    public MainViewModel(
        DashboardViewModel dashboard,
        OptimizeViewModel optimize,
        HardwareViewModel hardware,
        BenchmarkViewModel benchmark,
        GameModeViewModel gameMode,
        SettingsViewModel settings,
        IElevationService elevation)
    {
        IsElevated = elevation.IsElevated;
        NavItems =
        [
            new("dashboard", "Dashboard", "Icon.Dashboard", dashboard),
            new("optimize", "Otimizar", "Icon.Bolt", optimize),
            new("gamemode", "Modo Gamer", "Icon.Gamepad", gameMode),
            new("hardware", "Hardware", "Icon.Cpu", hardware),
            new("benchmark", "Benchmark", "Icon.Benchmark", benchmark),
            new("overlay", "Overlay de FPS", "Icon.Overlay", new PlaceholderViewModel("Overlay de FPS",
                "FPS, 1% mínimo, tempo de quadro e sensores por cima do jogo.", "Etapa 4", "Icon.Overlay",
                ["Lê os quadros pelo próprio Windows (ETW), sem injetar nada no jogo: compatível com anti-cheat",
                 "Janela transparente que não rouba cliques nem desempenho", "Posição, tamanho, opacidade e métricas ajustáveis",
                 "Atalho para ligar e desligar durante o jogo"])),
            new("bottlenecks", "Gargalos", "Icon.Filter", new PlaceholderViewModel("Gargalos",
                "Mostra o que segura o desempenho do seu PC e o que vale trocar primeiro.", "Etapa 5", "Icon.Filter",
                ["Nota de equilíbrio entre CPU, GPU, RAM e armazenamento", "Gargalos com explicação: CPU limitando a GPU, RAM em single channel, SSD lento",
                 "Upgrades em ordem de impacto"])),
        ];
        FooterItems = [new("settings", "Configurações", "Icon.Settings", settings)];
        dashboard.Navigate = Navigate;
        _current = NavItems[0];
    }

    [RelayCommand]
    public void Navigate(string key)
    {
        // "cleanup" abre a página Otimizar já na aba Limpeza e manutenção.
        if (key is "cleanup" or "startup")
        {
            var optimize = NavItems.First(n => n.Key == "optimize");
            var page = (OptimizeViewModel)optimize.Page;
            if (key == "cleanup") page.ShowCleanup = true; else page.ShowStartup = true;
            Current = optimize;
            return;
        }
        Current = NavItems.Concat(FooterItems).FirstOrDefault(n => n.Key == key) ?? Current;
    }
}
