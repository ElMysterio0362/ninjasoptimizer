using LibreHardwareMonitor.Hardware;
using NinjasOptimizer.Core.Hardware;

namespace NinjasOptimizer.Windows.Hardware;

/// <summary>
/// Sensores em tempo real via LibreHardwareMonitor. Temperaturas de CPU exigem o app elevado
/// (driver de baixo nível); sem admin, cargas/clocks/GPU/RAM continuam disponíveis.
/// </summary>
public sealed class LhmSensorService : ISensorService
{
    private readonly object _gate = new();
    private Computer? _computer;
    private Timer? _timer;
    private int _busy;

    public event EventHandler<SensorSnapshot>? Updated;
    public SensorSnapshot Latest { get; private set; } = SensorSnapshot.Empty;

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

    private void Tick()
    {
        // Evita leituras sobrepostas se uma demorar mais que o intervalo.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            _computer ??= Open();
            Latest = Read(_computer);
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

    private static Computer Open()
    {
        var c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
        c.Open();
        return c;
    }

    private static SensorSnapshot Read(Computer computer)
    {
        float? cpuLoad = null, cpuTemp = null, cpuPower = null, gpuLoad = null, gpuTemp = null, gpuCore = null, gpuMem = null;
        float? vramUsed = null, vramTotal = null, ramUsed = null, ramAvail = null, ramLoad = null;
        var coreClocks = new List<float>();
        var gpuSeen = false;

        foreach (var hw in computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
            var sensors = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)).Where(s => s.Value.HasValue).ToList();

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    cpuLoad ??= Find(sensors, SensorType.Load, "CPU Total");
                    cpuTemp ??= Find(sensors, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core Average", "Core Max");
                    cpuPower ??= Find(sensors, SensorType.Power, "CPU Package", "Package");
                    coreClocks.AddRange(sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #")).Select(s => s.Value!.Value));
                    break;

                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                    // A primeira GPU listada costuma ser a dedicada; não misturamos leituras de duas GPUs.
                    if (gpuSeen) break;
                    gpuSeen = true;
                    gpuLoad = Find(sensors, SensorType.Load, "GPU Core", "D3D 3D");
                    gpuTemp = Find(sensors, SensorType.Temperature, "GPU Core", "GPU Hot Spot");
                    gpuCore = Find(sensors, SensorType.Clock, "GPU Core");
                    gpuMem = Find(sensors, SensorType.Clock, "GPU Memory");
                    vramUsed = Find(sensors, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
                    vramTotal = Find(sensors, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
                    break;

                case HardwareType.Memory:
                    ramLoad ??= Find(sensors, SensorType.Load, "Memory");
                    ramUsed ??= Find(sensors, SensorType.Data, "Memory Used");
                    ramAvail ??= Find(sensors, SensorType.Data, "Memory Available");
                    break;
            }
        }

        return new SensorSnapshot
        {
            CpuLoad = cpuLoad,
            CpuTempC = cpuTemp,
            CpuPowerW = cpuPower,
            CpuClockMhz = coreClocks.Count > 0 ? coreClocks.Average() : null,
            GpuLoad = gpuLoad,
            GpuTempC = gpuTemp,
            GpuCoreClockMhz = gpuCore,
            GpuMemClockMhz = gpuMem,
            VramUsedMb = vramUsed,
            VramTotalMb = vramTotal,
            RamUsedGb = ramUsed,
            RamTotalGb = ramUsed + ramAvail,
            RamLoad = ramLoad,
        };
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

    public void Dispose()
    {
        Stop();
        // Espera uma leitura em andamento terminar antes de fechar o driver.
        SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            _computer?.Close();
            _computer = null;
        }
    }
}
