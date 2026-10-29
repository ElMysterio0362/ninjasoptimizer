using System.Runtime.InteropServices;
using NinjaOptimizer.Core.GameMode;
using NinjaOptimizer.Windows.Maintenance;

namespace NinjaOptimizer.Windows.GameMode;

/// <summary>Memória em espera (mesmo caminho do "Liberar memória em espera") e timer de 0,5 ms.</summary>
public sealed class WindowsGameBoosts : IGameBoosts
{
    private bool _timerOn;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    [DllImport("ntdll.dll")] private static extern int NtSetTimerResolution(uint desired, bool set, out uint current);

    public (long StandbyMb, long FreeMb)? ReadMemory()
    {
        var m = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        var standby = StandbyListTask.StandbyBytes();
        // "Disponível" inclui a lista em espera: livre de verdade = disponível − em espera.
        var free = Math.Max(0, (long)m.AvailPhys - standby);
        return (standby / (1024 * 1024), free / (1024 * 1024));
    }

    public bool PurgeStandby()
    {
        try
        {
            StandbyListTask.EnablePrivilege("SeProfileSingleProcessPrivilege");
            var command = StandbyListTask.MemoryPurgeStandbyList;
            return StandbyListTask.NtSetSystemInformation(StandbyListTask.SystemMemoryListInformation, ref command, sizeof(int)) == 0;
        }
        catch (Exception) { return false; } // sem administrador
    }

    public bool SetHighResolutionTimer(bool on)
    {
        if (on == _timerOn) return true;
        // 5000 × 100 ns = 0,5 ms. O Windows solta sozinho se o processo fechar ou travar.
        var ok = NtSetTimerResolution(5000, on, out _) == 0;
        if (ok) _timerOn = on;
        return ok;
    }
}
