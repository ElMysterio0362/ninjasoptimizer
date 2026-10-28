namespace NinjasOptimizer.Core.Hardware;

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
    /// <summary>Classe de velocidade esperada pela interface (a velocidade real vem do benchmark).</summary>
    public string SpeedClass => (MediaType, BusType) switch
    {
        (_, "NVMe") => "NVMe — até ~3.500–7.000 MB/s",
        (DiskMediaType.Ssd, _) => "SSD SATA — até ~550 MB/s",
        (DiskMediaType.Hdd, _) => "HDD — ~80–200 MB/s",
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

public interface ISensorService : IDisposable
{
    /// <summary>Disparado a cada leitura (em thread de fundo).</summary>
    event EventHandler<SensorSnapshot>? Updated;
    SensorSnapshot Latest { get; }
    void Start(TimeSpan interval);
    void Stop();
}
