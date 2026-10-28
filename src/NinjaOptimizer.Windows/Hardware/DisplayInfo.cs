using System.Management;
using System.Runtime.InteropServices;
using NinjaOptimizer.Core.Hardware;

namespace NinjaOptimizer.Windows.Hardware;

/// <summary>Resolução e taxa de atualização de cada monitor ativo (EnumDisplayDevices/EnumDisplaySettings).</summary>
internal static class DisplayInfo
{
    private const int EnumCurrentSettings = -1;
    private const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4;
    private const uint GetDeviceInterfaceName = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    public static IReadOnlyList<MonitorInfo> Read()
    {
        var result = new List<MonitorInfo>();
        var friendly = ReadWmiMonitorNames();
        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & AttachedToDesktop) == 0) continue;

            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(adapter.DeviceName, EnumCurrentSettings, ref mode)) continue;

            // O monitor é "filho" do adaptador. Com EDD_GET_DEVICE_INTERFACE_NAME, o DeviceID vem como
            // \\?\DISPLAY#GSM5B09#5&1234&0&UID4353#{guid}, que casa com a InstanceName do WmiMonitorID.
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string? name = null;
            if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, GetDeviceInterfaceName))
                name = FriendlyNameFor(monitor.DeviceID, friendly) ?? Generic(monitor.DeviceString);

            result.Add(new MonitorInfo(name ?? $"Monitor {result.Count + 1}", mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency,
                (adapter.StateFlags & PrimaryDevice) != 0));
        }
        // Se não deu para casar pelo ID mas só há um monitor, usamos o único nome conhecido.
        if (result.Count == 1 && friendly.Count == 1 && result[0].Name.StartsWith("Monitor "))
            result[0] = result[0] with { Name = friendly.Values.First() };
        return result.OrderByDescending(m => m.IsPrimary).ToList();
    }

    private static string? Generic(string deviceString) =>
        string.IsNullOrWhiteSpace(deviceString) || deviceString.StartsWith(@"\\.\") ? null : deviceString;

    /// <summary>InstanceName ("DISPLAY\GSM5B09\5&amp;..._0") → nome do EDID ("LG ULTRAGEAR").</summary>
    private static Dictionary<string, string> ReadWmiMonitorNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName, ManufacturerName FROM WmiMonitorID");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                var friendly = Decode(o["UserFriendlyName"]);
                if (string.IsNullOrEmpty(friendly)) friendly = Decode(o["ManufacturerName"]);
                if (!string.IsNullOrEmpty(friendly) && o["InstanceName"] is string instance)
                    names[instance] = friendly;
            }
        }
        catch (ManagementException) { }
        return names;
    }

    private static string Decode(object? value) =>
        value is ushort[] chars ? new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";

    private static string? FriendlyNameFor(string deviceInterface, Dictionary<string, string> names)
    {
        // \\?\DISPLAY#GSM5B09#5&1234&0&UID4353#{...}  →  DISPLAY\GSM5B09\5&1234&0&UID4353
        var parts = deviceInterface.Split('#');
        if (parts.Length < 3) return null;
        var prefix = $@"DISPLAY\{parts[1]}\{parts[2]}";
        return names.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
