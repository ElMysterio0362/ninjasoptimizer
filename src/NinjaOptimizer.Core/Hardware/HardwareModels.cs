namespace NinjaOptimizer.Core.Hardware;

public sealed record CpuInfo(
    string Name,
    string Manufacturer,
    int Cores,
    int Threads,
    int BaseClockMhz,
    int L2CacheKb,
    int L3CacheKb,
    string Socket);

public sealed record GpuInfo(
    string Name,
    ulong VramBytes,
    string DriverVersion,
    DateTime? DriverDate,
    bool IsIntegrated);

public sealed record MemoryModule(
    string Slot,
    string Bank,
    ulong CapacityBytes,
    int SpeedMts,
    int ConfiguredSpeedMts,
    string Manufacturer,
    string PartNumber,
    string Type);

public sealed record MemoryInfo(
    ulong TotalBytes,
    int TotalSlots,
    IReadOnlyList<MemoryModule> Modules,
    int Channels,
    string ChannelMode)
{
    public int UsedSlots => Modules.Count;
    public int EffectiveSpeedMts => Modules.Count == 0 ? 0 : Modules.Min(m => m.ConfiguredSpeedMts > 0 ? m.ConfiguredSpeedMts : m.SpeedMts);
}

public sealed record MotherboardInfo(string Manufacturer, string Product, string BiosVersion, DateTime? BiosDate);

public enum DiskMediaType { Unknown, Hdd, Ssd, Scm }

public sealed record DiskInfo(
    string Model,
    ulong SizeBytes,
    DiskMediaType MediaType,
    string BusType,
    string Health,
    int? TemperatureC,
    int? WearPercent)
{
    public string TypeLabel => MediaType switch
    {
        DiskMediaType.Hdd => $"HDD {BusType}",
        DiskMediaType.Ssd => $"SSD {BusType}",
        DiskMediaType.Scm => $"SCM {BusType}",
        _ => BusType,
    };

    /// <summary>Classe de velocidade esperada pela interface (a velocidade real vem do benchmark).</summary>
    public string SpeedClass => (MediaType, BusType) switch
    {
        (_, "NVMe") => "NVMe — até 7.000 MB/s",
        (DiskMediaType.Ssd, _) => "SSD SATA — até 550 MB/s",
        (DiskMediaType.Hdd, _) => "HDD — 80 a 200 MB/s",
        _ => "Desconhecida",
    };
}

public sealed record MonitorInfo(string Name, int Width, int Height, int RefreshHz, bool IsPrimary);

public sealed record OsInfo(string Name, string DisplayVersion, string Build, string Architecture);

public sealed record HardwareReport(
    CpuInfo Cpu,
    IReadOnlyList<GpuInfo> Gpus,
    MemoryInfo Memory,
    MotherboardInfo Motherboard,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<MonitorInfo> Monitors,
    OsInfo Os,
    string PowerSupply);

public interface IHardwareProvider
{
    Task<HardwareReport> GetReportAsync(CancellationToken ct = default);
}

/// <summary>Leituras em tempo real. Valores nulos = sensor indisponível.</summary>
public sealed record SensorSnapshot
{
    public float? CpuLoad { get; init; }
    public float? CpuTempC { get; init; }
    public float? CpuClockMhz { get; init; }

    /// <summary>True quando o clock vem do contador de desempenho do Windows (sem driver), não dos núcleos.</summary>
    public bool CpuClockEstimated { get; init; }
    public float? CpuPowerW { get; init; }
    public float? GpuLoad { get; init; }
    public float? GpuTempC { get; init; }
    public float? GpuCoreClockMhz { get; init; }
    public float? GpuMemClockMhz { get; init; }
    public float? VramUsedMb { get; init; }
    public float? VramTotalMb { get; init; }
    public float? RamUsedGb { get; init; }
    public float? RamTotalGb { get; init; }
    public float? RamLoad { get; init; }
    public static SensorSnapshot Empty { get; } = new();
}

/// <summary>O que limita a leitura dos sensores de baixo nível (temperatura/clock/consumo da CPU).</summary>
public enum SensorAccess
{
    Full,
    /// <summary>App não está como administrador.</summary>
    NeedsAdmin,
    /// <summary>Administrador, mas o driver PawnIO (usado pela LibreHardwareMonitor) não está instalado.</summary>
    NeedsDriver,
}

/// <summary>Descarta leituras impossíveis (sensores sem acesso devolvem 0 em vez de "indisponível").</summary>
public static class SensorValues
{
    public static float? Temperature(float? v) => v is > 1 and < 130 ? v : null;
    public static float? Positive(float? v) => v is > 0 and < 1_000_000 ? v : null;
    public static float? Percent(float? v) => v is >= 0 and <= 100 ? v : null;
}

public interface ISensorService : IDisposable
{
    SensorAccess Access { get; }

    /// <summary>Reabre os sensores (ex.: depois de instalar o driver).</summary>
    void Reset();

    /// <summary>Disparado a cada leitura (em thread de fundo).</summary>
    event EventHandler<SensorSnapshot>? Updated;
    SensorSnapshot Latest { get; }
    void Start(TimeSpan interval);
    void Stop();
}
