using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.Windows.Platform;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Hardware;

namespace NinjaOptimizer.App.Services;

/// <summary>Log de diagnóstico do app (o histórico de alterações fica no journal).</summary>
public sealed class AppLog(IAppPaths paths)
{
    private readonly object _gate = new();

    public string CurrentFile => Path.Combine(paths.LogFolder, $"app-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? ex = null) => Write("ERRO", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(paths.LogFolder);
                File.AppendAllText(CurrentFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* log nunca deve derrubar o app */ }
    }
}

public static class Shell
{
    public static void Open(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenFile(string file)
    {
        if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}

public static class Format
{
    public static string Bytes(ulong bytes)
    {
        if (bytes == 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i >= 3 ? $"{v:0.#} {units[i]}" : $"{v:0} {units[i]}";
    }

    /// <summary>Discos são vendidos em base 10 (1 TB = 10¹² bytes).</summary>
    public static string DiskSize(ulong bytes) =>
        bytes >= 1_000_000_000_000 ? $"{bytes / 1e12:0.##} TB" : $"{bytes / 1e9:0} GB";

    public static string Value(float? v, string format, string unit) => v is null ? "—" : v.Value.ToString(format) + unit;

    public static string Number(float? v) => v is null ? "—" : v.Value.ToString("0");

    public static string Ghz(float? mhz, string? missing = "—") => mhz is null ? missing ?? "" : $"{mhz / 1000:0.0} GHz";

    /// <summary>Junta partes com " · " ignorando as indisponíveis.</summary>
    public static string Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return present.Count == 0 ? "Sensores indisponíveis" : string.Join(" · ", present);
    }

    public static StatusKind TempStatus(float? temp, float warm, float hot) =>
        temp is null ? StatusKind.Neutral
        : temp >= hot ? StatusKind.Danger
        : temp >= warm ? StatusKind.Warning
        : StatusKind.Ok;

    public static string TempStatusText(StatusKind k) => k switch
    {
        StatusKind.Ok => "Normal",
        StatusKind.Warning => "Quente",
        StatusKind.Danger => "Muito quente",
        _ => "Sem sensor",
    };

    /// <summary>"hoje, 09:42", "ontem, 21:10" ou "12/10, 18:00".</summary>
    public static string When(DateTimeOffset at)
    {
        var d = at.LocalDateTime;
        var day = d.Date == DateTime.Today ? "hoje" : d.Date == DateTime.Today.AddDays(-1) ? "ontem" : d.ToString("dd/MM");
        return $"{day}, {d:HH:mm}";
    }
}

/// <summary>Inventário de hardware carregado uma vez e compartilhado entre as telas.</summary>
public sealed class HardwareState(IHardwareProvider provider)
{
    private Task<HardwareReport>? _report;

    public Task<HardwareReport> GetAsync(bool refresh = false)
    {
        if (refresh || _report is null || _report.IsFaulted) _report = provider.GetReportAsync();
        return _report;
    }
}

/// <summary>Leituras ao vivo já formatadas e marshaled para a thread da UI, com histórico para gráficos.</summary>
public sealed partial class LiveSensors : ObservableObject
{
    private const int HistoryLength = 60;
    private const string PawnIoSite = "https://pawnio.eu/";
    private readonly ISensorService _sensors;
    private readonly DialogService _dialogs;
    private readonly AppLog _log;

    [ObservableProperty] private SensorSnapshot _snapshot = SensorSnapshot.Empty;

    public ObservableCollection<double> CpuHistory { get; } = new(Enumerable.Repeat(0d, HistoryLength));
    public ObservableCollection<double> GpuHistory { get; } = new(Enumerable.Repeat(0d, HistoryLength));
    public ObservableCollection<double> RamHistory { get; } = new(Enumerable.Repeat(0d, HistoryLength));

    [ObservableProperty] private bool _isInstallingDriver;

    public LiveSensors(ISensorService sensors, DialogService dialogs, AppLog log)
    {
        _sensors = sensors;
        _dialogs = dialogs;
        _log = log;
        sensors.Updated += (_, s) => Application.Current?.Dispatcher.InvokeAsync(() => Push(s));
    }

    // ───────── Aviso de acesso aos sensores da CPU ─────────
    public SensorAccess Access => _sensors.Access;
    public bool ShowAccessBanner => Access != SensorAccess.Full;
    public bool CanElevate => Access == SensorAccess.NeedsAdmin;
    public bool CanInstallDriver => Access == SensorAccess.NeedsDriver;
    public string AccessTitle => Access switch
    {
        SensorAccess.NeedsAdmin => "Temperatura e consumo da CPU indisponíveis",
        SensorAccess.NeedsDriver => "Falta o driver de sensores da CPU",
        _ => "",
    };
    public string AccessMessage => Access switch
    {
        SensorAccess.NeedsAdmin => "Abra o app como administrador para ler esses sensores. Até lá, a frequência é estimada pelo Windows.",
        SensorAccess.NeedsDriver => "O PawnIO é gratuito, de código aberto e assinado. Ele lê temperatura, frequência e consumo da CPU.",
        _ => "",
    };

    [RelayCommand]
    private void Elevate()
    {
        if (WindowsElevationService.RelaunchElevated("--page hardware")) Application.Current.Shutdown();
    }

    /// <summary>Tenta instalar o PawnIO via winget; se não der, abre o site oficial.</summary>
    [RelayCommand]
    private async Task InstallDriverAsync()
    {
        if (!_dialogs.Confirm("Instalar driver PawnIO?",
                "O PawnIO é o driver de código aberto usado pela LibreHardwareMonitor para ler sensores da CPU. " +
                "Vamos tentar instalar pelo winget; se não for possível, o site oficial será aberto.",
                confirmText: "Instalar"))
            return;

        IsInstallingDriver = true;
        try
        {
            var ok = await Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo("winget",
                        "install --id namazso.PawnIO -e --accept-package-agreements --accept-source-agreements")
                    { UseShellExecute = false, CreateNoWindow = true });
                    if (p is null) return false;
                    p.WaitForExit(TimeSpan.FromMinutes(5));
                    return p.HasExited && p.ExitCode == 0;
                }
                catch (Exception) { return false; }
            });

            _log.Info($"Instalação do PawnIO via winget: {(ok ? "ok" : "falhou")}");
            _sensors.Reset();
            OnPropertyChanged(string.Empty);

            if (Access == SensorAccess.Full)
                _dialogs.Info("Driver instalado", "Os sensores da CPU aparecem em alguns segundos.", kind: DialogKind.Success);
            else
            {
                Process.Start(new ProcessStartInfo(PawnIoSite) { UseShellExecute = true });
                _dialogs.Info("Instale pelo site oficial",
                    "Não conseguimos instalar pelo winget. Abrimos o site do PawnIO: instale por lá e reabra o NinjaOptimizer.");
            }
        }
        finally
        {
            IsInstallingDriver = false;
        }
    }

    private void Push(SensorSnapshot s)
    {
        Snapshot = s;
        Add(CpuHistory, s.CpuLoad);
        Add(GpuHistory, s.GpuLoad);
        Add(RamHistory, s.RamLoad);
        OnPropertyChanged(string.Empty); // atualiza todas as propriedades derivadas
    }

    private static void Add(ObservableCollection<double> c, float? v)
    {
        c.RemoveAt(0);
        c.Add(v ?? 0);
    }

    // ───────── Valores no formato do design (número + unidade + linha de detalhe) ─────────
    public double CpuLoad => Snapshot.CpuLoad ?? 0;
    public double GpuLoad => Snapshot.GpuLoad ?? 0;
    public double RamLoad => Snapshot.RamLoad ?? 0;

    public string CpuLoadNumber => Format.Number(Snapshot.CpuLoad);
    public string GpuLoadNumber => Format.Number(Snapshot.GpuLoad);
    public string RamUsedNumber => Snapshot.RamUsedGb is { } u ? u.ToString("0.0") : "—";
    public string RamTotalText => Snapshot.RamTotalGb is { } t ? $"de {t:0} GB" : "";
    public string RamFreeText => Snapshot is { RamTotalGb: { } t, RamUsedGb: { } u } ? $"{t - u:0.0} GB" : "—";
    public string RamUsedText => Snapshot.RamUsedGb is { } x ? $"{x:0.0} GB" : "—";

    public string CpuTempText => Format.Value(Snapshot.CpuTempC, "0", " °C");
    public string CpuFreqText => Format.Ghz(Snapshot.CpuClockMhz);
    public string CpuFreqLabel => Snapshot.CpuClockEstimated ? "Frequência (estimada)" : "Frequência";
    public string CpuPowerText => Format.Value(Snapshot.CpuPowerW, "0", " W");
    public string CpuLoadText => Format.Value(Snapshot.CpuLoad, "0", "%");
    public string CpuSubline => Format.Join(Snapshot.CpuTempC is { } t ? $"{t:0} °C" : null, Format.Ghz(Snapshot.CpuClockMhz, null));

    public string GpuTempText => Format.Value(Snapshot.GpuTempC, "0", " °C");
    public string GpuFreqText => Snapshot.GpuCoreClockMhz is { } c ? $"{c:N0} MHz" : "—";
    public string GpuLoadText => Format.Value(Snapshot.GpuLoad, "0", "%");
    public string VramText => Snapshot.VramUsedMb is { } used
        ? $"{used / 1024:0.0} / {(Snapshot.VramTotalMb ?? 0) / 1024:0} GB" : "—";
    public string GpuSubline => Format.Join(Snapshot.GpuTempC is { } t ? $"{t:0} °C" : null,
        Snapshot.GpuCoreClockMhz is { } c ? $"{c:N0} MHz" : null);

    // Selo de status pela temperatura (sempre com texto, nunca só cor)
    public StatusKind CpuStatusKind => Format.TempStatus(Snapshot.CpuTempC, 80, 95);
    public string CpuStatusText => Format.TempStatusText(CpuStatusKind);
    public StatusKind GpuStatusKind => Format.TempStatus(Snapshot.GpuTempC, 83, 92);
    public string GpuStatusText => Format.TempStatusText(GpuStatusKind);
}

public enum StatusKind { Neutral, Ok, Accent, Warning, Danger }

