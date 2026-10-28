using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjasOptimizer.App.Services;
using NinjasOptimizer.Core.Abstractions;
using NinjasOptimizer.Core.Hardware;
using NinjasOptimizer.Core.Optimization;
using NinjasOptimizer.Core.Settings;
using NinjasOptimizer.Windows.Platform;

namespace NinjasOptimizer.App.ViewModels;

// ───────────────────────── Dashboard ─────────────────────────
public sealed record SummaryCard(string Glyph, string Caption, string Title, string Detail);

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly HardwareState _hardware;
    private readonly OptimizeViewModel _optimize;
    private readonly AppLog _log;

    public LiveSensors Live { get; }

    public ObservableCollection<SummaryCard> Summary { get; } = new();
    [ObservableProperty] private int _applied;
    [ObservableProperty] private int _total;
    [ObservableProperty] private double _optimizationScore;

    public DashboardViewModel(HardwareState hardware, OptimizeViewModel optimize, LiveSensors live, AppLog log)
    {
        _hardware = hardware;
        _optimize = optimize;
        _log = log;
        Live = live;
        _optimize.StateChanged += (_, _) => UpdateScore();
        UpdateScore();
    }

    private void UpdateScore()
    {
        Applied = _optimize.AppliedCount;
        Total = _optimize.TotalCount;
        OptimizationScore = Total == 0 ? 0 : 100.0 * Applied / Total;
    }

    public async Task LoadAsync()
    {
        try
        {
            var r = await _hardware.GetAsync();
            var gpu = r.Gpus.FirstOrDefault();
            var m = r.Memory;
            Summary.Clear();
            Summary.Add(new("\uE950", "PROCESSADOR", r.Cpu.Name,
                $"{r.Cpu.Cores} núcleos · {r.Cpu.Threads} threads · {r.Cpu.BaseClockMhz / 1000.0:0.0#} GHz"));
            Summary.Add(new("\uE7F4", "PLACA DE VÍDEO", gpu?.Name ?? "Não detectada",
                gpu is null ? "" : $"{Format.Bytes(gpu.VramBytes)} VRAM · driver {gpu.DriverVersion}"));
            Summary.Add(new("\uE964", "MEMÓRIA", $"{Format.Bytes(m.TotalBytes)} {m.Modules.FirstOrDefault()?.Type}",
                $"{m.EffectiveSpeedMts} MT/s · {m.ChannelMode} · {m.UsedSlots}/{m.TotalSlots} slots"));
            Summary.Add(new("\uEDA2", "ARMAZENAMENTO", $"{r.Disks.Count} disco(s) · {Format.DiskSize((ulong)r.Disks.Sum(d => (decimal)d.SizeBytes))}",
                string.Join(" · ", r.Disks.Select(d => $"{d.BusType} {d.MediaType}").Distinct())));
            Summary.Add(new("\uE770", "SISTEMA", r.Os.Name, $"{r.Os.DisplayVersion} · build {r.Os.Build} · {r.Os.Architecture}"));
            var mon = r.Monitors.FirstOrDefault();
            Summary.Add(new("\uE7F8", "MONITOR", mon?.Name ?? "—", mon is null ? "" : $"{mon.Width}×{mon.Height} @ {mon.RefreshHz} Hz"));
        }
        catch (Exception ex)
        {
            _log.Error("Dashboard: leitura de hardware", ex);
        }
    }

    [RelayCommand]
    private Task QuickOptimizeAsync() => _optimize.QuickOptimizeAsync(OptimizationProfile.Balanced);
}

// ───────────────────────── Hardware ─────────────────────────
public sealed record InfoRow(string Label, string Value);

public sealed class InfoSection(string title, string glyph, IEnumerable<InfoRow> rows, string? subtitle = null)
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string? Subtitle { get; } = subtitle;
    public IReadOnlyList<InfoRow> Rows { get; } = rows.ToList();
}

public sealed partial class HardwareViewModel(HardwareState hardware, LiveSensors live, AppSettings settings, AppLog log) : ObservableObject
{
    public LiveSensors Live { get; } = live;
    public ObservableCollection<InfoSection> Sections { get; } = new();

    [ObservableProperty] private bool _isLoading;

    public async Task LoadAsync(bool refresh = false)
    {
        IsLoading = true;
        try
        {
            var r = await hardware.GetAsync(refresh);
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

    private IEnumerable<InfoSection> Build(HardwareReport r)
    {
        yield return new InfoSection("Processador", "",
        [
            new("Modelo", r.Cpu.Name),
            new("Núcleos / threads", $"{r.Cpu.Cores} / {r.Cpu.Threads}"),
            new("Clock base", $"{r.Cpu.BaseClockMhz} MHz"),
            new("Cache L2 / L3", $"{r.Cpu.L2CacheKb / 1024.0:0.#} MB / {r.Cpu.L3CacheKb / 1024.0:0.#} MB"),
            new("Soquete", r.Cpu.Socket),
        ]);

        foreach (var g in r.Gpus)
            yield return new InfoSection("Placa de vídeo", "",
            [
                new("Modelo", g.Name),
                new("Tipo", g.IsIntegrated ? "Integrada" : "Dedicada"),
                new("VRAM", Format.Bytes(g.VramBytes)),
                new("Driver", g.DriverVersion + (g.DriverDate is { } d ? $" ({d:dd/MM/yyyy})" : "")),
            ]);

        var m = r.Memory;
        yield return new InfoSection("Memória RAM", "",
            new InfoRow[]
            {
                new("Total", $"{Format.Bytes(m.TotalBytes)} {m.Modules.FirstOrDefault()?.Type}"),
                new("Frequência efetiva", $"{m.EffectiveSpeedMts} MT/s"),
                new("Canais", m.ChannelMode),
                new("Slots usados", $"{m.UsedSlots} de {m.TotalSlots}"),
            }.Concat(m.Modules.Select(x => new InfoRow(
                string.IsNullOrEmpty(x.Slot) ? x.Bank : x.Slot,
                $"{Format.Bytes(x.CapacityBytes)} · {x.SpeedMts} MT/s · {x.Manufacturer} {x.PartNumber}".Trim())))
        );

        yield return new InfoSection("Placa-mãe", "",
        [
            new("Fabricante", r.Motherboard.Manufacturer),
            new("Modelo", r.Motherboard.Product),
            new("BIOS", r.Motherboard.BiosVersion + (r.Motherboard.BiosDate is { } bd ? $" ({bd:dd/MM/yyyy})" : "")),
        ]);

        foreach (var d in r.Disks)
            yield return new InfoSection("Armazenamento", "",
            [
                new("Modelo", d.Model),
                new("Capacidade", Format.DiskSize(d.SizeBytes)),
                new("Tipo", $"{(d.MediaType == DiskMediaType.Unknown ? "—" : d.MediaType.ToString().ToUpperInvariant())} · {d.BusType}"),
                new("Velocidade esperada", d.SpeedClass),
                new("Saúde", d.Health + (d.WearPercent is { } w ? $" · desgaste {w}%" : "")),
                new("Temperatura", d.TemperatureC is { } t ? $"{t} °C" : "— (requer administrador)"),
            ]);

        yield return new InfoSection("Monitores", "",
            r.Monitors.Select(x => new InfoRow(x.Name + (x.IsPrimary ? " (principal)" : ""), $"{x.Width}×{x.Height} @ {x.RefreshHz} Hz")));

        yield return new InfoSection("Sistema", "",
        [
            new("Windows", r.Os.Name),
            new("Versão", $"{r.Os.DisplayVersion} (build {r.Os.Build})"),
            new("Arquitetura", r.Os.Architecture),
        ]);

        yield return new InfoSection("Fonte de alimentação", "",
        [
            new("Modelo", string.IsNullOrWhiteSpace(settings.PowerSupply) ? r.PowerSupply : settings.PowerSupply),
            new("Observação", "A fonte não envia dados ao Windows. Informe o modelo em Configurações para a análise de gargalos."),
        ]);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(refresh: true);

    [RelayCommand]
    private void CopyReport()
    {
        var sb = new StringBuilder("Ninjas Optimizer — Relatório de hardware").AppendLine().AppendLine();
        foreach (var s in Sections)
        {
            sb.AppendLine($"[{s.Title}]");
            foreach (var row in s.Rows) sb.AppendLine($"  {row.Label}: {row.Value}");
            sb.AppendLine();
        }
        Clipboard.SetText(sb.ToString());
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
public sealed class PlaceholderViewModel(string title, string subtitle, string stage, IEnumerable<string> features)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string Stage { get; } = stage;
    public IReadOnlyList<string> Features { get; } = features.ToList();
}
