using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjasOptimizer.Core.Abstractions;

namespace NinjasOptimizer.App.ViewModels;

public sealed record NavItem(string Key, string Title, string Glyph, object Page);

public sealed partial class MainViewModel : ObservableObject
{
    public IReadOnlyList<NavItem> NavItems { get; }
    public IReadOnlyList<NavItem> FooterItems { get; }
    public bool IsElevated { get; }

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
        }
    }

    public MainViewModel(
        DashboardViewModel dashboard,
        OptimizeViewModel optimize,
        HardwareViewModel hardware,
        SettingsViewModel settings,
        IElevationService elevation)
    {
        IsElevated = elevation.IsElevated;
        NavItems =
        [
            new("dashboard", "Dashboard", "", dashboard),
            new("optimize", "Otimizar", "", optimize),
            new("hardware", "Hardware", "", hardware),
            new("benchmark", "Benchmark", "", new PlaceholderViewModel("Benchmark",
                "Testes de CPU, GPU, RAM e disco com pontuação final.", "Etapa 3",
                ["Testes de CPU (single e multi-thread), RAM (largura de banda e latência), disco (sequencial e 4K aleatório) e GPU",
                 "Pontuação final normalizada", "Comparação antes × depois das otimizações", "Histórico salvo localmente"])),
            new("overlay", "Overlay", "", new PlaceholderViewModel("Overlay em jogos",
                "FPS, 1% low, frametime, temperaturas e uso de CPU/GPU/RAM/VRAM sobre o jogo.", "Etapa 4",
                ["Captura de frametimes via ETW (sem injetar DLL — compatível com anti-cheat)",
                 "Janela transparente click-through com impacto mínimo", "Posição, tamanho, opacidade e métricas configuráveis",
                 "Atalho global para ligar/desligar"])),
            new("bottlenecks", "Gargalos", "", new PlaceholderViewModel("Compatibilidade e gargalos",
                "Equilíbrio entre CPU, GPU, RAM e armazenamento.", "Etapa 5",
                ["Nota de equilíbrio do sistema", "Detecção de gargalos (CPU limitando GPU, RAM em single channel, SSD lento…)",
                 "Sugestões de upgrade em ordem de impacto"])),
        ];
        FooterItems = [new("settings", "Configurações", "", settings)];
        _current = NavItems[0];
    }

    [RelayCommand]
    public void Navigate(string key) =>
        Current = NavItems.Concat(FooterItems).FirstOrDefault(n => n.Key == key) ?? Current;
}
