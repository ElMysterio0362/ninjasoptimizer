using System.Management;
using Microsoft.Win32;
using NinjaOptimizer.Core.Hardware;

namespace NinjaOptimizer.Windows.Hardware;

/// <summary>Coleta o inventário de hardware via WMI/CIM, registro e APIs de vídeo.</summary>
public sealed class WmiHardwareProvider : IHardwareProvider
{
    public Task<HardwareReport> GetReportAsync(CancellationToken ct = default) => Task.Run(() => new HardwareReport(
        Safe(ReadCpu, new CpuInfo("Desconhecido", "", 0, Environment.ProcessorCount, 0, 0, 0, "")),
        Safe(ReadGpus, []),
        Safe(ReadMemory, new MemoryInfo(0, 0, [], 0, "Desconhecido")),
        Safe(ReadMotherboard, new MotherboardInfo("Desconhecido", "", "", null)),
        Safe(ReadDisks, []),
        Safe(DisplayInfo.Read, []),
        Safe(ReadOs, new OsInfo(Environment.OSVersion.ToString(), "", "", "")),
        "Não detectável por software"), ct);

    // Cada seção é independente: uma falha de WMI não derruba o painel inteiro.
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }

    private static IEnumerable<ManagementObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        foreach (ManagementObject mo in searcher.Get()) yield return mo;
    }

    private static string S(ManagementBaseObject o, string p) => (o[p]?.ToString() ?? "").Trim();
    private static ulong U(ManagementBaseObject o, string p) => o[p] is null ? 0 : Convert.ToUInt64(o[p]);
    private static int I(ManagementBaseObject o, string p) => o[p] is null ? 0 : Convert.ToInt32(o[p]);

    private static DateTime? Dmtf(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(value); } catch { return null; }
    }

    private static CpuInfo ReadCpu()
    {
        var o = Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor").First();
        return new CpuInfo(S(o, "Name"), S(o, "Manufacturer"), I(o, "NumberOfCores"), I(o, "NumberOfLogicalProcessors"),
            I(o, "MaxClockSpeed"), I(o, "L2CacheSize"), I(o, "L3CacheSize"), S(o, "SocketDesignation"));
    }

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        // AdapterRAM do WMI é uint32 (trava em 4 GB). O valor real fica no registro do driver.
        var vramByName = ReadVramFromRegistry();
        return Query("SELECT Name, AdapterRAM, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController")
            .Where(o => !S(o, "PNPDeviceID").StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) // adaptadores virtuais
            .Select(o =>
            {
                var name = S(o, "Name");
                var vram = vramByName.TryGetValue(name, out var v) ? v : U(o, "AdapterRAM");
                var integrated = name.Contains("Intel", StringComparison.OrdinalIgnoreCase) && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
                                 || name.EndsWith("Radeon Graphics", StringComparison.OrdinalIgnoreCase);
                return new GpuInfo(name, vram, S(o, "DriverVersion"), Dmtf(S(o, "DriverDate")), integrated);
            })
            .OrderBy(g => g.IsIntegrated)
            .ToList();
    }

    private static Dictionary<string, ulong> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return result;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(sub);
            var desc = k?.GetValue("DriverDesc") as string;
            var mem = k?.GetValue("HardwareInformation.qwMemorySize");
            if (desc is null || mem is null) continue;
            result[desc] = mem switch { long l => (ulong)l, byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b), _ => 0 };
        }
        return result;
    }

    private static MemoryInfo ReadMemory()
    {
        var modules = Query("SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory")
            .Select(o => new MemoryModule(S(o, "DeviceLocator"), S(o, "BankLabel"), U(o, "Capacity"), I(o, "Speed"),
                I(o, "ConfiguredClockSpeed"), S(o, "Manufacturer"), S(o, "PartNumber"),
                MemoryChannelAnalyzer.MemoryTypeName(I(o, "SMBIOSMemoryType"))))
            .ToList();
        var slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(o => I(o, "MemoryDevices"));
        var (channels, mode) = MemoryChannelAnalyzer.Analyze(modules);
        return new MemoryInfo((ulong)modules.Sum(m => (decimal)m.CapacityBytes), Math.Max(slots, modules.Count), modules, channels, mode);
    }

    private static MotherboardInfo ReadMotherboard()
    {
        var b = Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").First();
        var bios = Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        return new MotherboardInfo(S(b, "Manufacturer"), S(b, "Product"),
            bios is null ? "" : S(bios, "SMBIOSBIOSVersion"), bios is null ? null : Dmtf(S(bios, "ReleaseDate")));
    }

    private static IReadOnlyList<DiskInfo> ReadDisks()
    {
        const string scope = @"root\Microsoft\Windows\Storage";
        var disks = new List<DiskInfo>();
        foreach (var d in Query("SELECT ObjectId, FriendlyName, Size, MediaType, BusType, HealthStatus, SpindleSpeed FROM MSFT_PhysicalDisk", scope))
        {
            var reported = I(d, "MediaType") switch { 3 => DiskMediaType.Hdd, 4 => DiskMediaType.Ssd, 5 => DiskMediaType.Scm, _ => DiskMediaType.Unknown };
            var bus = I(d, "BusType") switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 3 => "ATA", var x => $"Barramento {x}" };
            var spindle = d["SpindleSpeed"] is null ? uint.MaxValue : Convert.ToUInt32(d["SpindleSpeed"]);
            var media = DiskTypeHeuristics.Infer(S(d, "FriendlyName"), reported, bus, spindle);
            var health = I(d, "HealthStatus") switch { 0 => "Saudável", 1 => "Atenção", 2 => "Com problemas", _ => "Desconhecida" };

            // Temperatura e desgaste: exigem admin e suporte do driver; ficam nulos se indisponíveis.
            int? temp = null, wear = null;
            try
            {
                var objectId = S(d, "ObjectId").Replace("\\", "\\\\").Replace("\"", "\\\"");
                var rel = Query($"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{objectId}\"}} WHERE ResultClass = MSFT_StorageReliabilityCounter", scope).FirstOrDefault();
                if (rel is not null)
                {
                    if (rel["Temperature"] is { } t && Convert.ToInt32(t) > 0) temp = Convert.ToInt32(t);
                    if (rel["Wear"] is { } w) wear = Convert.ToInt32(w);
                }
            }
            catch (ManagementException) { }

            disks.Add(new DiskInfo(S(d, "FriendlyName"), U(d, "Size"), media, bus, health, temp, wear));
        }
        return disks;
    }

    private static OsInfo ReadOs()
    {
        var o = Query("SELECT Caption, BuildNumber, OSArchitecture FROM Win32_OperatingSystem").First();
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var display = cv?.GetValue("DisplayVersion") as string ?? cv?.GetValue("ReleaseId") as string ?? "";
        var ubr = cv?.GetValue("UBR") is int u ? $".{u}" : "";
        return new OsInfo(S(o, "Caption").Replace("Microsoft ", ""), display, S(o, "BuildNumber") + ubr, S(o, "OSArchitecture"));
    }
}
