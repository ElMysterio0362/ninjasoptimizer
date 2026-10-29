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

    /// <param name="applyTweak">Aplica um ajuste do catálogo pelo motor (com histórico); devolve o resumo.</param>
    /// <param name="revertTweak">Desfaz o ajuste ativo com esse Id pelo motor.</param>
    public static IReadOnlyList<MaintenanceTask> Build(ICommandRunner commands, IServiceManager services, IElevationService elevation,
        NinjaOptimizer.Core.Safety.ChangeJournal journal, IAppPaths paths,
        Func<string, CancellationToken, Task<string>>? applyTweak = null,
        Func<string, CancellationToken, Task<MaintenanceResult>>? revertTweak = null) =>
    [
        new FolderCleanupTask("cleanup.temp", "Apagar arquivos temporários",
            "Apaga arquivos temporários do seu usuário e do Windows com mais de 1 dia. Arquivos em uso são pulados.",
            TweakCategory.Disk,
            () => elevation.IsElevated ? [Path.GetTempPath(), Path.Combine(Windows, "Temp")] : [Path.GetTempPath()],
            TimeSpan.FromDays(1),
            consequences: ["Os arquivos apagados não vão para a Lixeira: não dá para recuperar."]),

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
            note: "Na primeira vez que você abrir cada jogo, ele recompila os shaders e pode engasgar por alguns minutos.",
            consequences: ["Cada jogo recompila os shaders na primeira abertura e pode engasgar ou demorar a carregar por alguns minutos."]),

        new WindowsUpdateCacheTask(services),
        new ComponentCleanupTask(commands),
        new DriveOptimizeTask(commands),
        new StandbyListTask(),
        new DnsFlushTask(commands),

        // Reparos (catálogo extra F)
        new RepairSystemFilesTask(commands, RunSfcAsync),
        new RestartExplorerTask(elevation),
        new RecentItemsTask(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent")),
        new RepairWindowsUpdateTask(services, Path.Combine(Windows, "SoftwareDistribution"), Path.Combine(Environment.SystemDirectory, "catroot2")),
        new RepairNetworkTask(commands, paths.LogFolder, () => JournalQueries.ActiveTweakIds(journal)),
        new CheckDiskTask(commands, SystemDrive, () => new DriveInfo(SystemDrive).DriveFormat, YesLetter),
        new RestartAudioTask(services),
        new RestoreDefaultServicesTask(services, () => JournalQueries.ServicesChangedByApp(journal)),
        new RepairStoreTask(commands, services, elevation, () => JournalQueries.ServicesChangedByApp(journal)),

        // Catálogo extra K
        new WorkingSetsTask(),
        new RepairXboxAppsTask(commands, services,
            () => JournalQueries.ActiveTweakIds(journal).Contains("gaming.xbox-services-off"),
            ct => revertTweak?.Invoke("gaming.xbox-services-off", ct) ?? Task.FromResult(new MaintenanceResult(false, 0, "Use Desfazer no histórico de Otimizar."))),
        new DeviceRepairTask(DeviceRepairKind.Camera, commands, services, new ProcessAppCloser(), ApplyOrExplain(applyTweak)),
        new DeviceRepairTask(DeviceRepairKind.Microphone, commands, services, new ProcessAppCloser(), ApplyOrExplain(applyTweak)),
        new ForceShutdownTask(commands),
    ];

    private static Func<string, CancellationToken, Task<string>> ApplyOrExplain(Func<string, CancellationToken, Task<string>>? apply) =>
        apply ?? ((_, _) => Task.FromResult("Opção não disponível."));

    /// <summary>Letra de "Sim" que o chkdsk espera, no idioma do Windows.</summary>
    private static char YesLetter => System.Globalization.CultureInfo.InstalledUICulture.TwoLetterISOLanguageName switch
    {
        "pt" or "es" or "it" => 'S',
        "fr" => 'O',
        "de" or "nl" => 'J',
        _ => 'Y',
    };

    private static string SystemDrive => (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")[..2];

    /// <summary>O sfc escreve em UTF-16 (diferente das outras ferramentas); só o código de saída interessa.</summary>
    private static async Task<int> RunSfcAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sfc.exe", "/scannow")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.Unicode, StandardErrorEncoding = System.Text.Encoding.Unicode,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o sfc.");
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        await Task.WhenAll(o, e);
        return p.ExitCode;
    }
}

internal sealed class RecycleBinTask : MaintenanceTask
{
    public override IReadOnlyList<string> Consequences => ["Os arquivos da Lixeira são apagados para sempre."];
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
    public override IReadOnlyList<string> Consequences => ["Uma atualização baixada e ainda não instalada será baixada de novo.", "O Windows Update fica pausado por alguns segundos."];
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
    public override IReadOnlyList<string> Consequences => ["Não dá mais para desinstalar atualizações do Windows que já foram instaladas."];
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
    public override bool HasSideEffects => false;
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
    public override IReadOnlyList<string> Consequences => ["Arquivos e programas usados há pouco recarregam do disco: podem abrir um pouco mais devagar na próxima vez."];
    public override string Id => "memory.standby-list";
    public override string Name => "Liberar memória em espera";
    public override string Description =>
        "Esvazia o cache de arquivos que o Windows guarda na RAM (memória em espera). Ajuda quando um jogo engasga " +
        "por falta de memória livre. Em uso normal o Windows já libera esse cache sozinho.";
    public override TweakCategory Category => TweakCategory.Memory;
    public override string Note => "Programas abertos em seguida podem demorar um pouco mais na primeira vez.";
    public override bool RequiresAdmin => true;
    public override string ActionText => "Liberar agora";

    internal const int SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4, MemoryEmptyWorkingSets = 2, MemoryFlushModifiedList = 3;
    private const uint SePrivilegeEnabled = 0x2, TokenAdjustPrivileges = 0x20, TokenQuery = 0x8;

    [DllImport("ntdll.dll")]
    internal static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

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

    internal static void EnablePrivilege(string name)
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

    internal static long StandbyBytes()
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
    public override IReadOnlyList<string> Consequences => ["Os primeiros sites que você abrir depois demoram uma fração de segundo a mais."];
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

/// <summary>F2: reinicia o Explorer da sua sessão e garante que ele volta sem elevação.</summary>
internal sealed class RestartExplorerTask(IElevationService elevation) : MaintenanceTask
{
    public override IReadOnlyList<string> Consequences => ["A barra de tarefas e a área de trabalho somem por alguns segundos.", "As janelas de pastas abertas são fechadas."];
    public override string Id => "maintenance.restart-explorer";
    public override string Name => "Reiniciar o Explorer";
    public override string Description => "Fecha e abre de novo a barra de tarefas, o menu Iniciar e a área de trabalho. Corrige quando eles travam ou somem.";
    public override TweakCategory Category => TweakCategory.SystemPrivacy;
    public override string Note => "As janelas de pastas abertas são fechadas. Programas abertos não são afetados.";
    public override string ActionText => "Reiniciar agora";

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        var session = Process.GetCurrentProcess().SessionId;
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer").Where(p => p.SessionId == session))
            {
                using (p)
                {
                    try { p.Kill(); p.WaitForExit(5000); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                }
            }
        }
        finally
        {
            // O Windows reabre o Explorer sozinho (AutoRestartShell); se não reabrir em 5 s, abrimos nós.
            for (var i = 0; i < 10 && !ExplorerRunning(session); i++) await Task.Delay(500, CancellationToken.None);
            if (!ExplorerRunning(session)) StartUnelevated();
        }
        return ExplorerRunning(session)
            ? new MaintenanceResult(true, 0, "Explorer reiniciado.")
            : new MaintenanceResult(false, 0, "O Explorer não voltou sozinho. Aperte Ctrl+Shift+Esc → Executar nova tarefa → explorer.");
    }, ct);

    private static bool ExplorerRunning(int session)
    {
        var list = Process.GetProcessesByName("explorer");
        try { return list.Any(p => p.SessionId == session); }
        finally { foreach (var p in list) p.Dispose(); }
    }

    private void StartUnelevated()
    {
        // Iniciado direto de um app elevado, o Explorer abriria como administrador.
        var psi = elevation.IsElevated
            ? new ProcessStartInfo("runas.exe", "/trustlevel:0x20000 explorer.exe") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        try { Process.Start(psi)?.Dispose(); } catch (Win32Exception) { }
    }
}

/// <summary>K2: tira da RAM as páginas dos programas (working sets) e, opcionalmente, grava a lista modificada.</summary>
internal sealed class WorkingSetsTask : MaintenanceTask
{
    private const string FlushModified = "flush-modified";

    public override string Id => "memory.working-sets";
    public override string Name => "Liberar a memória dos programas";
    public override string Description => "Pede a todos os programas abertos que devolvam a RAM que não estão usando agora. O uso de RAM cai na hora.";
    public override TweakCategory Category => TweakCategory.Memory;
    public override bool RequiresAdmin => true;
    public override string ActionText => "Liberar agora";
    public override IReadOnlyList<string> Consequences =>
    [
        "O uso de RAM cai na hora, mas os programas abertos ficam lentos por alguns segundos enquanto recarregam o que precisam do disco.",
        "Durante um jogo pode causar engasgos: não use com o jogo aberto.",
    ];
    public override IReadOnlyList<MaintenanceOption> Options =>
        [new(FlushModified, "Também gravar no disco a memória modificada", ["Pode deixar o disco ocupado por alguns segundos."])];

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    private static ulong AvailableRam()
    {
        var m = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? m.AvailPhys : 0;
    }

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        StandbyListTask.EnablePrivilege("SeProfileSingleProcessPrivilege");
        var before = AvailableRam();
        var command = StandbyListTask.MemoryEmptyWorkingSets;
        var status = StandbyListTask.NtSetSystemInformation(StandbyListTask.SystemMemoryListInformation, ref command, sizeof(int));
        if (status != 0) return new MaintenanceResult(false, 0, $"O Windows recusou a liberação (status 0x{status:X8}).");
        if (IsSelected(FlushModified))
        {
            command = StandbyListTask.MemoryFlushModifiedList;
            StandbyListTask.NtSetSystemInformation(StandbyListTask.SystemMemoryListInformation, ref command, sizeof(int));
        }
        var after = AvailableRam();
        return new MaintenanceResult(true, (long)Math.Max(0, (double)after - before), "Memória dos programas liberada.");
    }, ct);
}

/// <summary>Fecha programas pedindo primeiro (como o X da janela); só força se o usuário mandar.</summary>
internal sealed class ProcessAppCloser : IAppCloser
{
    public IReadOnlyList<(int Pid, string Name)> Running(IEnumerable<string> processNames)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var list = new List<(int, string)>();
        foreach (var n in processNames)
            foreach (var p in Process.GetProcessesByName(n))
                using (p) { if (p.SessionId == session) list.Add((p.Id, p.ProcessName)); }
        return list;
    }

    public bool CloseGracefully(int pid, TimeSpan wait)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.MainWindowHandle == IntPtr.Zero) return p.HasExited;
            p.CloseMainWindow();
            return p.WaitForExit(wait);
        }
        catch (ArgumentException) { return true; } // já fechou
        catch (InvalidOperationException) { return true; }
    }

    public void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
