using System.Management;
using Microsoft.Win32;
using NinjaOptimizer.Core.Hardware;
using NinjaOptimizer.Core.Optimization;

namespace NinjaOptimizer.Windows.Hardware;

/// <summary>Lê o resumo do PC usado pelas condições dos ajustes. Cada parte falha sozinha (fica "desconhecido").</summary>
public static class SystemProfileProbe
{
    public static Task<SystemProfile> ReadAsync() => Task.Run(() => new SystemProfile(
        true,
        Try(SystemDriveType, DiskMediaType.Unknown),
        Try<bool?>(IsPortable, null),
        Try<IReadOnlySet<GpuVendor>?>(GpuVendors, null),
        Try<int?>(Build, null))
    {
        CredentialGuardRunning = Try<bool?>(CredentialGuard, null),
    });

    /// <summary>Win32_DeviceGuard.SecurityServicesRunning: 1 = Credential Guard, 2 = Integridade de Memória.</summary>
    private static bool? CredentialGuard()
    {
        var o = Query("SELECT SecurityServicesRunning FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard").FirstOrDefault();
        if (o is null) return null;
        return o["SecurityServicesRunning"] is uint[] running ? running.Contains(1u) : false;
    }

    private static T Try<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch (Exception) { return fallback; }
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var s = new ManagementObjectSearcher(scope, wql);
        foreach (var o in s.Get()) yield return o;
    }

    private static DiskMediaType SystemDriveType()
    {
        const string scope = @"root\Microsoft\Windows\Storage";
        var letter = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")[0];
        var part = Query($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'", scope).FirstOrDefault();
        if (part?["DiskNumber"] is null) return DiskMediaType.Unknown;
        var number = Convert.ToUInt32(part["DiskNumber"]).ToString();
        var disk = Query($"SELECT FriendlyName, MediaType, BusType, SpindleSpeed FROM MSFT_PhysicalDisk WHERE DeviceId = '{number}'", scope).FirstOrDefault();
        if (disk is null) return DiskMediaType.Unknown;
        var reported = Convert.ToInt32(disk["MediaType"] ?? 0) switch { 3 => DiskMediaType.Hdd, 4 => DiskMediaType.Ssd, 5 => DiskMediaType.Scm, _ => DiskMediaType.Unknown };
        var bus = Convert.ToInt32(disk["BusType"] ?? 0) == 17 ? "NVMe" : "";
        var spindle = disk["SpindleSpeed"] is null ? uint.MaxValue : Convert.ToUInt32(disk["SpindleSpeed"]);
        return DiskTypeHeuristics.Infer(disk["FriendlyName"]?.ToString() ?? "", reported, bus, spindle);
    }

    private static bool? IsPortable()
    {
        var chassis = Query("SELECT ChassisTypes FROM Win32_SystemEnclosure")
            .SelectMany(o => o["ChassisTypes"] is ushort[] t ? t.Select(x => (int)x) : []).ToList();
        var battery = Query("SELECT DeviceID FROM Win32_Battery").Any();
        if (chassis.Count == 0 && !battery) return null;
        return SystemProfile.IsPortableChassis(chassis, battery);
    }

    private static IReadOnlySet<GpuVendor>? GpuVendors()
    {
        var set = Query("SELECT PNPDeviceID FROM Win32_VideoController")
            .Select(o => SystemProfile.VendorFromPnpId(o["PNPDeviceID"]?.ToString() ?? ""))
            .OfType<GpuVendor>().ToHashSet();
        return set;
    }

    private static int? Build()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return int.TryParse(k?.GetValue("CurrentBuildNumber")?.ToString(), out var b) ? b : Environment.OSVersion.Version.Build;
    }
}
