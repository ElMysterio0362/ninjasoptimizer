using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Maintenance;
using NinjaOptimizer.Core.Optimization;

namespace NinjaOptimizer.Windows.Maintenance;

/// <summary>Tarefas de limpeza e manutenção disponíveis no Windows (etapa 2C).</summary>
public static class WindowsMaintenanceCatalog
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string LocalLow => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

    public static IReadOnlyList<MaintenanceTask> Build(ICommandRunner commands, IServiceManager services, IElevationService elevation) =>
    [
        new FolderCleanupTask("cleanup.temp", "Apagar arquivos temporários",
            "Apaga arquivos temporários do seu usuário e do Windows com mais de 1 dia. Arquivos em uso são pulados.",
            TweakCategory.Disk,
            () => elevation.IsElevated ? [Path.GetTempPath(), Path.Combine(Windows, "Temp")] : [Path.GetTempPath()],
            TimeSpan.FromDays(1)),

        new RecycleBinTask(),

        new FolderCleanupTask("cleanup.shader-cache", "Limpar cache de shaders",
            "Apaga os shaders salvos pelo DirectX e pelos drivers NVIDIA, AMD e Intel. Útil depois de atualizar o driver ou quando um jogo engasga sem motivo.",
            TweakCategory.Gpu,
            () =>
            [
                Path.Combine(Local, "D3DSCache"),
                Path.Combine(Local, "NVIDIA", "DXCache"), Path.Combine(Local, "NVIDIA", "GLCache"),
                Path.Combine(Local, "AMD", "DxCache"), Path.Combine(Local, "AMD", "DxcCache"),
                Path.Combine(Local, "AMD", "VkCache"), Path.Combine(Local, "AMD", "GLCache"),
                Path.Combine(LocalLow, "Intel", "ShaderCache"),
            ],
            TimeSpan.Zero,
            note: "Na primeira vez que você abrir cada jogo, ele recompila os shaders e pode engasgar por alguns minutos."),

        new WindowsUpdateCacheTask(services),
        new ComponentCleanupTask(commands),
        new DriveOptimizeTask(commands),
        new StandbyListTask(),
        new DnsFlushTask(commands),
    ];
}

internal sealed class RecycleBinTask : MaintenanceTask
{
    public override string Id => "cleanup.recycle-bin";
    public override string Name => "Esvaziar a Lixeira";
    public override string Description => "Apaga de vez os arquivos que estão na Lixeira, em todas as unidades.";
    public override TweakCategory Category => TweakCategory.Disk;
    public override string Note => "Os arquivos da Lixeira não poderão ser recuperados depois.";
    public override string ActionText => "Esvaziar agora";

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;

    private static long Size()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    public override Task<long?> EstimateAsync(CancellationToken ct) => Task.Run(() => (long?)Size(), ct);

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var before = Size();
        if (before == 0) return new MaintenanceResult(true, 0, "A Lixeira já estava vazia.");
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        return hr == 0 ? new MaintenanceResult(true, before, "Lixeira esvaziada.")
            : new MaintenanceResult(false, 0, $"O Windows não esvaziou a Lixeira (código 0x{hr:X8}).");
    }, ct);
}

internal sealed class WindowsUpdateCacheTask(IServiceManager services) : MaintenanceTask
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");
    private static readonly string[] UpdateServices = ["wuauserv", "bits"];

    public override string Id => "cleanup.windows-update";
    public override string Name => "Limpar downloads do Windows Update";
    public override string Description => "Apaga atualizações já baixadas e instaladas que ficaram guardadas. O Windows Update é pausado durante a limpeza e volta em seguida.";
    public override TweakCategory Category => TweakCategory.Disk;
    public override string Note => "Uma atualização baixada e ainda não instalada será baixada de novo.";
    public override bool RequiresAdmin => true;

    public override Task<long?> EstimateAsync(CancellationToken ct) =>
        Task.Run(() => (long?)FolderCleaner.Measure([Folder], TimeSpan.Zero).Bytes, ct);

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        progress?.Report("Pausando o Windows Update…");
        var wasRunning = UpdateServices.Where(s => services.Exists(s) && services.IsRunning(s)).ToList();
        try
        {
            foreach (var s in wasRunning) services.Stop(s);
            progress?.Report("Apagando downloads antigos…");
            var r = FolderCleaner.Clean([Folder], TimeSpan.Zero, ct);
            return new MaintenanceResult(true, r.Bytes, $"{r.Files} arquivo(s) apagado(s).");
        }
        finally
        {
            foreach (var s in wasRunning)
            {
                try { services.Start(s); } catch { /* o Windows reinicia o serviço sozinho quando precisar */ }
            }
        }
    }, ct);
}

internal sealed class ComponentCleanupTask(ICommandRunner commands) : MaintenanceTask
{
    public override string Id => "cleanup.components";
    public override string Name => "Limpar componentes antigos do Windows";
    public override string Description => "Remove versões antigas de componentes que ficaram para trás depois das atualizações (DISM).";
    public override TweakCategory Category => TweakCategory.Disk;
    public override string Note => "Depois disso, as atualizações já instaladas não podem mais ser desinstaladas.";
    public override bool RequiresAdmin => true;
    public override string Duration => "alguns minutos";

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Limpando componentes do Windows. Isso leva alguns minutos…");
        var free = SystemDriveFree();
        var r = await commands.RunAsync("DISM.exe", "/Online /Cleanup-Image /StartComponentCleanup /NoRestart", ct);
        var freed = Math.Max(0, SystemDriveFree() - free);
        return r.Success
            ? new MaintenanceResult(true, freed, "Componentes antigos removidos.")
            : new MaintenanceResult(false, freed, $"O DISM terminou com erro {r.ExitCode}. Nada foi danificado; tente de novo mais tarde.");
    }

    internal static long SystemDriveFree() =>
        new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").AvailableFreeSpace;
}

internal sealed class DriveOptimizeTask(ICommandRunner commands) : MaintenanceTask
{
    public override string Id => "maintenance.optimize-drives";
    public override string Name => "Otimizar unidades (TRIM e desfragmentação)";
    public override string Description => "Envia o TRIM para os SSDs e desfragmenta os HDDs, do mesmo jeito que a ferramenta \"Otimizar unidades\" do Windows.";
    public override TweakCategory Category => TweakCategory.Disk;
    public override bool RequiresAdmin => true;
    public override string Duration => "segundos no SSD, até mais de uma hora no HDD";
    public override string ActionText => "Otimizar agora";

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Otimizando as unidades…");
        var r = await commands.RunAsync("defrag.exe", "/C /O /H", ct);
        return r.Success
            ? new MaintenanceResult(true, 0, "Unidades otimizadas.")
            : new MaintenanceResult(false, 0, $"O Windows não conseguiu otimizar todas as unidades (código {r.ExitCode}).");
    }
}

/// <summary>Esvazia a lista de memória em espera (cache de arquivos na RAM) via NtSetSystemInformation.</summary>
internal sealed class StandbyListTask : MaintenanceTask
{
    public override string Id => "memory.standby-list";
    public override string Name => "Liberar memória em espera";
    public override string Description =>
        "Esvazia o cache de arquivos que o Windows guarda na RAM (memória em espera). Ajuda quando um jogo engasga " +
        "por falta de memória livre. Em uso normal o Windows já libera esse cache sozinho.";
    public override TweakCategory Category => TweakCategory.Memory;
    public override string Note => "Programas abertos em seguida podem demorar um pouco mais na primeira vez.";
    public override bool RequiresAdmin => true;
    public override string ActionText => "Liberar agora";

    private const int SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4;
    private const uint SePrivilegeEnabled = 0x2, TokenAdjustPrivileges = 0x20, TokenQuery = 0x8;

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public int Count; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, IntPtr prev, IntPtr ret);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out var token))
            throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception();
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                throw new InvalidOperationException("O Windows não concedeu a permissão para liberar a memória.");
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static long StandbyBytes()
    {
        long total = 0;
        foreach (var counter in new[] { "Standby Cache Core Bytes", "Standby Cache Normal Priority Bytes", "Standby Cache Reserve Bytes" })
        {
            try
            {
                using var pc = new PerformanceCounter("Memory", counter, readOnly: true);
                total += (long)pc.NextValue();
            }
            catch { /* contador indisponível nesta edição */ }
        }
        return total;
    }

    public override Task<long?> EstimateAsync(CancellationToken ct) => Task.Run(() => (long?)StandbyBytes(), ct);

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var before = StandbyBytes();
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        var command = MemoryPurgeStandbyList;
        var status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        if (status != 0) return new MaintenanceResult(false, 0, $"O Windows recusou a liberação (status 0x{status:X8}).");
        var freed = Math.Max(0, before - StandbyBytes());
        return new MaintenanceResult(true, freed, "Memória em espera liberada.");
    }, ct);
}

internal sealed class DnsFlushTask(ICommandRunner commands) : MaintenanceTask
{
    public override string Id => "maintenance.dns-flush";
    public override string Name => "Limpar cache de DNS";
    public override string Description => "Esquece os endereços de sites guardados. Resolve sites que não abrem depois de mudarem de servidor ou de trocar o DNS.";
    public override TweakCategory Category => TweakCategory.Network;
    public override string ActionText => "Limpar agora";

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Limpando o cache de DNS…");
        var r = await commands.RunAsync("ipconfig", "/flushdns", ct);
        return r.Success
            ? new MaintenanceResult(true, 0, "Cache de DNS limpo.")
            : new MaintenanceResult(false, 0, $"O Windows não limpou o cache de DNS (código {r.ExitCode}).");
    }
}
