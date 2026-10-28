using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NinjaOptimizer.Core.GameMode;

namespace NinjaOptimizer.Windows.GameMode;

/// <summary>Lista, pausa e retoma processos pelas APIs do Windows (NtSuspendProcess / NtResumeProcess).</summary>
public sealed class WindowsProcessControl : IProcessControl
{
    private const uint QueryLimited = 0x1000, SuspendResume = 0x0800;

    public int CurrentSessionId { get; } = Process.GetCurrentProcess().SessionId;

    public IReadOnlyList<ProcessInfo> List()
    {
        var list = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string? path = null;
                    long start = 0;
                    var h = OpenProcess(QueryLimited, false, p.Id);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            var sb = new StringBuilder(1024);
                            var size = sb.Capacity;
                            if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                            if (GetProcessTimes(h, out var c, out _, out _, out _)) start = c;
                        }
                        finally { CloseHandle(h); }
                    }
                    list.Add(new ProcessInfo(p.Id, p.ProcessName + ".exe", path, p.WorkingSet64, p.SessionId, start));
                }
                catch (InvalidOperationException) { } // encerrou enquanto líamos
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return list;
    }

    public int? ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    public bool Suspend(int pid) => Call(pid, NtSuspendProcess);
    public bool Resume(int pid) => Call(pid, NtResumeProcess);

    private static bool Call(int pid, Func<IntPtr, int> fn)
    {
        var h = OpenProcess(SuspendResume, false, pid);
        if (h == IntPtr.Zero) return false; // sem permissão (ex.: processo de administrador)
        try { return fn(h) == 0; }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
