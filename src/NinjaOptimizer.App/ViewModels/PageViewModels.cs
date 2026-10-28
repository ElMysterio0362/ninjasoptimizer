using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Hardware;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Settings;
using NinjaOptimizer.Windows.Platform;

namespace NinjaOptimizer.App.ViewModels;

// ───────────────────────── Dashboard ─────────────────────────
public sealed record QuickItem(string Text, bool Applied);

public sealed record Recommendation(string Title, string Detail, StatusKind Kind, string ActionText, IRelayCommand Action);

public enum HeroPhase { Idle, Running, Done }

public sealed partial class DashboardViewModel : ObservableObject
{
    private const OptimizationProfile QuickProfile = OptimizationProfile.Balanced;

    private readonly HardwareState _hardware;
    private readonly OptimizeViewModel _optimize;
    private readonly AppLog _log;
    private readonly DispatcherTimer _diskTimer;
    private HardwareReport? _report;
    private ApplyReport? _lastQuick;

    public LiveSensors Live { get; }

    /// <summary>Navegação entre páginas (definida pela janela principal).</summary>
    public Action<string>? Navigate { get; set; }

    public ObservableCollection<QuickItem> QuickItems { get; } = new();
    public ObservableCollection<Recommendation> Recommendations { get; } = new();

    [ObservableProperty] private int _applied;
    [ObservableProperty] private int _total;
    [ObservableProperty] private double _optimizationScore;
    [ObservableProperty] private HeroPhase _phase;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _subline = "";
    [ObservableProperty] private string _lastOptimizationText = "";
    [ObservableProperty] private string _ramTypeText = "";

    // Disco do Windows (C:)
    [ObservableProperty] private string _systemDriveLabel = "DISCO C:";
    [ObservableProperty] private double _systemDrivePercent;
    [ObservableProperty] private string _systemDriveNumber = "—";
    [ObservableProperty] private string _systemDriveDetail = "";
    [ObservableProperty] private bool _systemDriveAlmostFull;

    public string ScoreText => $"{OptimizationScore:0}";
    public bool IsIdle => Phase == HeroPhase.Idle;
    public bool IsRunning => Phase == HeroPhase.Running;
    public bool IsDone => Phase == HeroPhase.Done;
    public bool HasRecommendations => Recommendations.Count > 0;

    public DashboardViewModel(HardwareState hardware, OptimizeViewModel optimize, LiveSensors live, AppLog log)
    {
        _hardware = hardware;
        _optimize = optimize;
        _log = log;
        Live = live;

        _optimize.StateChanged += (_, _) => Refresh();
        _optimize.PropertyChanged += OnOptimizeChanged;

        _diskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _diskTimer.Tick += (_, _) => UpdateSystemDrive();
        _diskTimer.Start();
        UpdateSystemDrive();
        Refresh();
    }

    partial void OnOptimizationScoreChanged(double value) => OnPropertyChanged(nameof(ScoreText));

    partial void OnPhaseChanged(HeroPhase value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
    }

    private void OnOptimizeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OptimizeViewModel.IsBusy) or nameof(OptimizeViewModel.Status)) UpdateHero();
    }

    public async Task LoadAsync()
    {
        try
        {
            _report = await _hardware.GetAsync();
            var type = _report.Memory.Modules.FirstOrDefault()?.Type;
            RamTypeText = type is null ? "" : $" · {type}";
            Refresh();
        }
        catch (Exception ex)
        {
            _log.Error("Dashboard: leitura de hardware", ex);
        }
    }

    private void Refresh()
    {
        Applied = _optimize.AppliedCount;
        Total = _optimize.TotalCount;
        OptimizationScore = Total == 0 ? 0 : Math.Round(100.0 * Applied / Total);
        LastOptimizationText = _optimize.LastAppliedAt is { } at
            ? $"Última otimização: {Format.When(at)}"
            : "Nenhuma otimização aplicada ainda";

        var applied = _optimize.AllItems.Where(i => i.IsApplied).Select(i => i.Tweak.Id).ToHashSet();
        QuickItems.Clear();
        foreach (var t in TweakCatalog.ForProfile(QuickProfile))
            QuickItems.Add(new QuickItem(t.Name, applied.Contains(t.Id)));

        BuildRecommendations(applied);
        UpdateHero();
    }

    private void UpdateHero()
    {
        if (_optimize.IsBusy)
        {
            Phase = HeroPhase.Running;
            Headline = "Otimizando…";
            Subline = string.IsNullOrEmpty(_optimize.Status) ? "Analisando seu PC…" : _optimize.Status;
            return;
        }

        if (_lastQuick is { AppliedCount: > 0 } done)
        {
            Phase = HeroPhase.Done;
            Headline = $"Pronto. {done.AppliedCount} ajuste(s) aplicado(s).";
            Subline = $"Seu PC subiu para {ScoreText} de 100. Você pode reverter quando quiser.";
            return;
        }

        Phase = HeroPhase.Idle;
        var pending = QuickItems.Count(q => !q.Applied);
        Headline = Applied == Total && Total > 0 ? "Todas as otimizações estão ativas" : $"Seu PC está {ScoreText}% otimizado";
        Subline = pending > 0
            ? $"{pending} ajuste(s) do perfil Equilibrado ainda não estão ativos. Você revisa a lista antes de aplicar."
            : "Nada pendente no perfil Equilibrado. Cada ajuste pode ser revertido quando você quiser.";
    }

    private void BuildRecommendations(HashSet<string> applied)
    {
        Recommendations.Clear();

        if (SystemDriveAlmostFull)
            Recommendations.Add(new Recommendation($"Disco C: quase cheio ({SystemDrivePercent:0}%)", "Atenção", StatusKind.Warning,
                "Limpar disco", new RelayCommand(() => Start("cleanmgr.exe"))));

        if (_report?.Memory is { Channels: 1, Modules.Count: >= 1 } mem)
            Recommendations.Add(new Recommendation("Memória em single channel", mem.Modules.Count == 1
                    ? "Um segundo pente igual dobra a largura de banda" : "Atenção: confira os slots no manual da placa",
                StatusKind.Warning, "Ver detalhes", new RelayCommand(() => Navigate?.Invoke("hardware"))));

        foreach (var item in _optimize.AllItems.Where(i => !applied.Contains(i.Tweak.Id))
                     .OrderByDescending(i => i.Tweak.Impact).ThenBy(i => i.Tweak.Risk).Take(4 - Recommendations.Count))
        {
            var tweak = item.Tweak;
            Recommendations.Add(new Recommendation(tweak.Name, LabelsPt.Of(tweak.Impact),
                tweak.Impact == ImpactLevel.High ? StatusKind.Accent : StatusKind.Neutral,
                "Aplicar", new AsyncRelayCommand(() => _optimize.ApplyOneAsync(tweak))));
        }
        OnPropertyChanged(nameof(HasRecommendations));
    }

    private void UpdateSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new DriveInfo(root);
            if (!d.IsReady || d.TotalSize == 0) return;
            var used = d.TotalSize - d.AvailableFreeSpace;
            SystemDriveLabel = $"DISCO {root.TrimEnd('\\')}";
            SystemDrivePercent = 100.0 * used / d.TotalSize;
            SystemDriveNumber = $"{SystemDrivePercent:0}";
            SystemDriveAlmostFull = SystemDrivePercent >= 85;
            SystemDriveDetail = $"{Format.DiskSize((ulong)d.AvailableFreeSpace)} livres de {Format.DiskSize((ulong)d.TotalSize)}";
        }
        catch (Exception ex)
        {
            _log.Error("Dashboard: disco do sistema", ex);
        }
    }

    private static void Start(string file)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch { /* ferramenta indisponível nesta edição do Windows */ }
    }

    [RelayCommand]
    private async Task QuickOptimizeAsync()
    {
        _lastQuick = null;
        var report = await _optimize.QuickOptimizeAsync(QuickProfile);
        if (report is { AppliedCount: > 0 }) _lastQuick = report;
        Refresh();
    }

    [RelayCommand]
    private void SeeChanges()
    {
        _lastQuick = null;
        UpdateHero();
        Navigate?.Invoke("optimize");
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        await _optimize.RevertAllAsync();
        _lastQuick = null;
        Refresh();
    }
}

// ───────────────────────── Hardware ─────────────────────────
public sealed record InfoRow(string Label, string Value);

public sealed class InfoSection(string title, string icon, IEnumerable<InfoRow> rows)
{
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public IReadOnlyList<InfoRow> Rows { get; } = rows.ToList();
}

public sealed record MetricItem(string Label, string Value, bool Highlight = false);

public sealed record DiskCard(string Caption, string Title, string Detail, string StatusText, StatusKind StatusKind, IReadOnlyList<MetricItem> Metrics);

public sealed partial class HardwareViewModel(HardwareState hardware, LiveSensors live, AppSettings settings, DialogService dialogs, AppLog log) : ObservableObject
{
    public LiveSensors Live { get; } = live;
    public ObservableCollection<InfoSection> Sections { get; } = new();
    public ObservableCollection<DiskCard> Disks { get; } = new();
    public ObservableCollection<string> Chips { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _cpuName = "Analisando seu PC…";
    [ObservableProperty] private string _cpuDetail = "";
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private string _gpuDetail = "";
    [ObservableProperty] private string _memTitle = "";
    [ObservableProperty] private string _memDetail = "";
    [ObservableProperty] private string _memChannelText = "";
    [ObservableProperty] private StatusKind _memChannelKind;
    [ObservableProperty] private string _memSlotsText = "";
    [ObservableProperty] private string _memSpeedText = "";

    public async Task LoadAsync(bool refresh = false)
    {
        IsLoading = true;
        try
        {
            var r = await hardware.GetAsync(refresh);
            Fill(r);
            Sections.Clear();
            foreach (var s in Build(r)) Sections.Add(s);
        }
        catch (Exception ex)
        {
            log.Error("Hardware: leitura", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Fill(HardwareReport r)
    {
        Chips.Clear();
        Chips.Add(Format.Join(r.Os.Name, r.Os.DisplayVersion));
        if (!string.IsNullOrWhiteSpace(r.Motherboard.Product))
            Chips.Add(Format.Join("Placa-mãe " + r.Motherboard.Product, string.IsNullOrEmpty(r.Motherboard.BiosVersion) ? null : "BIOS " + r.Motherboard.BiosVersion));
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Chips.Add(up.TotalDays >= 1 ? $"Ligado há {(int)up.TotalDays} d {up.Hours} h" : $"Ligado há {up.Hours} h {up.Minutes} min");

        CpuName = r.Cpu.Name;
        CpuDetail = $"{r.Cpu.Cores} núcleos · {r.Cpu.Threads} threads · base de {r.Cpu.BaseClockMhz / 1000.0:0.0} GHz";

        var gpu = r.Gpus.FirstOrDefault();
        GpuName = gpu?.Name ?? "Não detectada";
        GpuDetail = gpu is null ? "" : $"{Format.Bytes(gpu.VramBytes)} de VRAM · driver {gpu.DriverVersion}";

        var m = r.Memory;
        MemTitle = $"{Format.Bytes(m.TotalBytes)} {m.Modules.FirstOrDefault()?.Type}".Trim();
        var sizes = m.Modules.GroupBy(x => x.CapacityBytes).Select(g => $"{g.Count()} pente(s) de {Format.Bytes(g.Key)}");
        MemDetail = Format.Join(string.Join(" + ", sizes), m.EffectiveSpeedMts > 0 ? $"{m.EffectiveSpeedMts:N0} MT/s" : null);
        MemChannelText = m.ChannelMode;
        MemChannelKind = m.Channels switch { >= 2 => StatusKind.Accent, 1 => StatusKind.Warning, _ => StatusKind.Neutral };
        MemSlotsText = $"{m.UsedSlots} de {m.TotalSlots}";
        MemSpeedText = m.EffectiveSpeedMts > 0 ? $"{m.EffectiveSpeedMts:N0} MT/s" : "—";

        Disks.Clear();
        foreach (var d in r.Disks)
        {
            var (status, kind) = d.Health switch
            {
                "Saudável" => ("Saudável", StatusKind.Ok),
                "Atenção" => ("Atenção", StatusKind.Warning),
                "Com problemas" => ("Com problemas", StatusKind.Danger),
                _ => ("Saúde desconhecida", StatusKind.Neutral),
            };
            var title = d.MediaType == DiskMediaType.Unknown ? $"{d.BusType} {Format.DiskSize(d.SizeBytes)}" : $"{d.TypeLabel} {Format.DiskSize(d.SizeBytes)}";
            Disks.Add(new DiskCard("ARMAZENAMENTO", title, d.Model, status, kind,
            [
                new("Interface", d.BusType),
                new("Velocidade esperada", d.SpeedClass.Split(" — ").Last()),
                new("Temperatura", d.TemperatureC is { } t ? $"{t} °C" : "—"),
                new("Desgaste", d.WearPercent is { } w ? $"{w}%" : "—"),
            ]));
        }
    }

    private IEnumerable<InfoSection> Build(HardwareReport r)
    {
        yield return new InfoSection("Processador", "Icon.Cpu",
        [
            new("Modelo", r.Cpu.Name),
            new("Núcleos / threads", $"{r.Cpu.Cores} / {r.Cpu.Threads}"),
            new("Clock base", $"{r.Cpu.BaseClockMhz:N0} MHz"),
            new("Cache L2 / L3", $"{r.Cpu.L2CacheKb / 1024.0:0.#} MB / {r.Cpu.L3CacheKb / 1024.0:0.#} MB"),
            new("Soquete", r.Cpu.Socket),
        ]);

        foreach (var g in r.Gpus)
            yield return new InfoSection("Placa de vídeo", "Icon.Gpu",
            [
                new("Modelo", g.Name),
                new("Tipo", g.IsIntegrated ? "Integrada" : "Dedicada"),
                new("VRAM", Format.Bytes(g.VramBytes)),
                new("Driver", g.DriverVersion + (g.DriverDate is { } d ? $" ({d:dd/MM/yyyy})" : "")),
            ]);

        var m = r.Memory;
        yield return new InfoSection("Memória RAM", "Icon.Memory",
            new InfoRow[]
            {
                new("Total", $"{Format.Bytes(m.TotalBytes)} {m.Modules.FirstOrDefault()?.Type}"),
                new("Frequência efetiva", $"{m.EffectiveSpeedMts:N0} MT/s"),
                new("Canais", m.ChannelMode),
                new("Slots usados", $"{m.UsedSlots} de {m.TotalSlots}"),
            }.Concat(m.Modules.Select(x => new InfoRow(
                string.IsNullOrEmpty(x.Slot) ? x.Bank : x.Slot,
                $"{Format.Bytes(x.CapacityBytes)} · {x.SpeedMts:N0} MT/s · {x.Manufacturer} {x.PartNumber}".Trim())))
        );

        yield return new InfoSection("Placa-mãe", "Icon.Board",
        [
            new("Fabricante", r.Motherboard.Manufacturer),
            new("Modelo", r.Motherboard.Product),
            new("BIOS", r.Motherboard.BiosVersion + (r.Motherboard.BiosDate is { } bd ? $" ({bd:dd/MM/yyyy})" : "")),
        ]);

        yield return new InfoSection("Monitores", "Icon.Monitor",
            r.Monitors.Select(x => new InfoRow(x.Name + (x.IsPrimary ? " (principal)" : ""), $"{x.Width}×{x.Height} · {x.RefreshHz} Hz")));

        yield return new InfoSection("Sistema", "Icon.Settings",
        [
            new("Windows", r.Os.Name),
            new("Versão", $"{r.Os.DisplayVersion} (build {r.Os.Build})"),
            new("Arquitetura", r.Os.Architecture),
        ]);

        yield return new InfoSection("Fonte de alimentação", "Icon.Power",
        [
            new("Modelo", string.IsNullOrWhiteSpace(settings.PowerSupply) ? "Não informada" : settings.PowerSupply),
            new("Observação", "A fonte não envia dados ao Windows. Informe o modelo em Configurações."),
        ]);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(refresh: true);

    [RelayCommand]
    private void ExportReport()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Exportar relatório de hardware",
            FileName = $"NinjaOptimizer-hardware-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texto (*.txt)|*.txt",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder("NinjaOptimizer · Relatório de hardware").AppendLine()
            .AppendLine(DateTime.Now.ToString("dd/MM/yyyy HH:mm")).AppendLine();
        foreach (var s in Sections)
        {
            sb.AppendLine($"[{s.Title}]");
            foreach (var row in s.Rows) sb.AppendLine($"  {row.Label}: {row.Value}");
            sb.AppendLine();
        }
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            dialogs.Info("Relatório exportado", dlg.FileName, kind: DialogKind.Success);
        }
        catch (Exception ex)
        {
            dialogs.Info("Não conseguimos salvar o arquivo", ex.Message, kind: DialogKind.Danger);
        }
    }
}

// ───────────────────────── Configurações ─────────────────────────
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly IAppPaths _paths;
    private readonly AppLog _log;

    public bool IsElevated { get; }
    public string ElevationText => IsElevated ? "Executando como administrador" : "Executando como usuário comum";
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _abortIfRestorePointFails;
    [ObservableProperty] private string _powerSupply;

    public SettingsViewModel(AppSettings settings, SettingsStore store, IAppPaths paths, IElevationService elevation, AppLog log)
    {
        _settings = settings;
        _store = store;
        _paths = paths;
        _log = log;
        IsElevated = elevation.IsElevated;
        _createRestorePoint = settings.CreateRestorePoint;
        _abortIfRestorePointFails = settings.AbortIfRestorePointFails;
        _powerSupply = settings.PowerSupply;
    }

    partial void OnCreateRestorePointChanged(bool value) { _settings.CreateRestorePoint = value; Save(); }
    partial void OnAbortIfRestorePointFailsChanged(bool value) { _settings.AbortIfRestorePointFails = value; Save(); }
    partial void OnPowerSupplyChanged(string value) { _settings.PowerSupply = value.Trim(); Save(); }

    private void Save()
    {
        try { _store.Save(_settings); }
        catch (Exception ex) { _log.Error("Salvar configurações", ex); }
    }

    [RelayCommand] private void OpenBackups() => Shell.Open(_paths.BackupFolder);
    [RelayCommand] private void OpenData() => Shell.Open(_paths.DataFolder);
    [RelayCommand] private void OpenLog() => Shell.OpenFile(_log.CurrentFile);

    [RelayCommand]
    private void RelaunchElevated()
    {
        if (WindowsElevationService.RelaunchElevated("--page settings")) Application.Current.Shutdown();
    }
}

// ───────────────────────── Módulos das próximas etapas ─────────────────────────
public sealed class PlaceholderViewModel(string title, string subtitle, string stage, string icon, IEnumerable<string> features)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string Stage { get; } = stage;
    public string Icon { get; } = icon;
    public IReadOnlyList<string> Features { get; } = features.ToList();
}
