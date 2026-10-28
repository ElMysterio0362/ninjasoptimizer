using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using NinjasOptimizer.Core.Abstractions;
using NinjasOptimizer.Core.Hardware;

namespace NinjasOptimizer.Windows.Hardware;

/// <summary>
/// Sensores em tempo real.
/// <list type="bullet">
/// <item>GPU (NVIDIA/AMD/Intel): LibreHardwareMonitor, funciona sem admin.</item>
/// <item>Temperatura, clock por núcleo e consumo da CPU: LibreHardwareMonitor via driver PawnIO —
/// exigem o app como administrador E o PawnIO instalado. Sem isso a biblioteca devolve 0, que tratamos como "indisponível".</item>
/// <item>Clock da CPU sem driver: contador "% Processor Performance" × clock base (o mesmo do Gerenciador de Tarefas).</item>
/// <item>RAM: GlobalMemoryStatusEx (memória física real; o sensor da LHM mistura memória virtual).</item>
/// </list>
/// </summary>
public sealed class LhmSensorService : ISensorService
{
    private readonly object _gate = new();
    private readonly IElevationService _elevation;
    private Computer? _computer;
    private Timer? _timer;
    private int _busy;
    private PerformanceCounter? _cpuPerf;
    private PerformanceCounter? _cpuUtility;
    private readonly float _baseMhz = ReadBaseMhz();

    public event EventHandler<SensorSnapshot>? Updated;
    public SensorSnapshot Latest { get; private set; } = SensorSnapshot.Empty;
    public SensorAccess Access { get; private set; }

    public LhmSensorService(IElevationService elevation)
    {
        _elevation = elevation;
        Access = DetectAccess();
    }

    private SensorAccess DetectAccess()
    {
        if (!_elevation.IsElevated) return SensorAccess.NeedsAdmin;
        return IsPawnIoInstalled() ? SensorAccess.Full : SensorAccess.NeedsDriver;
    }

    /// <summary>O instalador do PawnIO registra uma entrada de desinstalação (mesma checagem da LHM).</summary>
    public static bool IsPawnIoInstalled()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        return k is not null;
    }

    public void Start(TimeSpan interval)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, interval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Reset()
    {
        SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            _computer?.Close();
            _computer = null; // reabre na próxima leitura, já enxergando o driver
            Access = DetectAccess();
        }
    }

    private void Tick()
    {
        // Evita leituras sobrepostas se uma demorar mais que o intervalo.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            Latest = Read();
            Updated?.Invoke(this, Latest);
        }
        catch
        {
            // Sensores são opcionais: falhas não devem afetar o app.
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private SensorSnapshot Read()
    {
        float? cpuLoad = null, cpuTemp = null, cpuPower = null, gpuLoad = null, gpuTemp = null, gpuCore = null, gpuMem = null;
        float? vramUsed = null, vramTotal = null;
        var coreClocks = new List<float>();
        var gpuSeen = false;

        try
        {
            _computer ??= Open();
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
                var sensors = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)).Where(s => s.Value.HasValue).ToList();

                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        cpuLoad ??= SensorValues.Percent(Find(sensors, SensorType.Load, "CPU Total"));
                        cpuTemp ??= SensorValues.Temperature(Find(sensors, SensorType.Temperature,
                            "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core (Tdie)", "Core Average", "Core Max"));
                        cpuPower ??= SensorValues.Positive(Find(sensors, SensorType.Power, "Package", "CPU Package"));
                        coreClocks.AddRange(sensors
                            .Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #") && s.Value > 0)
                            .Select(s => s.Value!.Value));
                        break;

                    case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                        // A primeira GPU listada costuma ser a dedicada; não misturamos leituras de duas GPUs.
                        if (gpuSeen) break;
                        gpuSeen = true;
                        gpuLoad = SensorValues.Percent(Find(sensors, SensorType.Load, "GPU Core", "D3D 3D"));
                        gpuTemp = SensorValues.Temperature(Find(sensors, SensorType.Temperature, "GPU Core", "GPU Hot Spot"));
                        gpuCore = SensorValues.Positive(Find(sensors, SensorType.Clock, "GPU Core"));
                        gpuMem = SensorValues.Positive(Find(sensors, SensorType.Clock, "GPU Memory"));
                        vramUsed = SensorValues.Positive(Find(sensors, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"));
                        vramTotal = SensorValues.Positive(Find(sensors, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total"));
                        break;
                }
            }
        }
        catch
        {
            // LHM indisponível: seguimos com as fontes do próprio Windows.
        }

        // Clock: núcleos (driver) ou estimativa pelo contador do Windows.
        float? cpuClock = coreClocks.Count > 0 ? coreClocks.Average() : null;
        var estimated = false;
        if (cpuClock is null && _baseMhz > 0 && ReadCounter(ref _cpuPerf, "% Processor Performance") is { } perf and > 0)
        {
            cpuClock = _baseMhz * perf / 100f;
            estimated = true;
        }

        // Uso da CPU: se a LHM não tiver, usa "% Processor Utility" (mesma métrica do Gerenciador de Tarefas).
        cpuLoad ??= ReadCounter(ref _cpuUtility, "% Processor Utility") is { } u ? Math.Min(u, 100f) : null;

        var (ramUsed, ramTotal, ramLoad) = ReadPhysicalMemory();

        return new SensorSnapshot
        {
            CpuLoad = cpuLoad,
            CpuTempC = cpuTemp,
            CpuPowerW = cpuPower,
            CpuClockMhz = cpuClock,
            CpuClockEstimated = estimated,
            GpuLoad = gpuLoad,
            GpuTempC = gpuTemp,
            GpuCoreClockMhz = gpuCore,
            GpuMemClockMhz = gpuMem,
            VramUsedMb = vramUsed,
            VramTotalMb = vramTotal,
            RamUsedGb = ramUsed,
            RamTotalGb = ramTotal,
            RamLoad = ramLoad,
        };
    }

    private static Computer Open()
    {
        // RAM vem do Windows (GlobalMemoryStatusEx); a LHM só para CPU e GPU.
        var c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
        c.Open();
        return c;
    }

    private static float? Find(List<ISensor> sensors, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            var s = sensors.FirstOrDefault(x => x.SensorType == type && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (s is not null) return s.Value;
        }
        return null;
    }

    private static float? ReadCounter(ref PerformanceCounter? counter, string name)
    {
        try
        {
            if (counter is null)
            {
                counter = new PerformanceCounter("Processor Information", name, "_Total", readOnly: true);
                counter.NextValue(); // a primeira leitura é sempre 0
                return null;
            }
            return counter.NextValue();
        }
        catch
        {
            return null;
        }
    }

    private static float ReadBaseMhz()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return k?.GetValue("~MHz") is int mhz ? mhz : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private static (float? Used, float? Total, float? Load) ReadPhysicalMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (null, null, null);
        const float gb = 1024f * 1024 * 1024;
        return ((m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb, m.dwMemoryLoad);
    }

    public void Dispose()
    {
        Stop();
        // Espera uma leitura em andamento terminar antes de fechar o driver.
        SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            _computer?.Close();
            _computer = null;
            _cpuPerf?.Dispose();
            _cpuUtility?.Dispose();
        }
    }
}
