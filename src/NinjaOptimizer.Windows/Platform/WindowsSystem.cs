using System.Diagnostics;
using System.Security.Principal;
using System.Globalization;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using NinjaOptimizer.Core.Abstractions;
using ServiceStartMode = NinjaOptimizer.Core.Abstractions.ServiceStartMode;
using WinStartMode = System.ServiceProcess.ServiceStartMode;

namespace NinjaOptimizer.Windows.Platform;

public sealed class WindowsRegistryAccess : IRegistryAccess
{
    // Sempre a visão 64 bits: é onde o Windows lê as políticas num SO x64.
    private static RegistryKey Root(RegHive hive) => RegistryKey.OpenBaseKey(
        hive == RegHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    public RegValue? GetValue(RegHive hive, string key, string name)
    {
        using var root = Root(hive);
        using var k = root.OpenSubKey(key);
        if (k is null) return null;
        var data = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (data is null) return null;
        return k.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => new RegValue(RegKind.DWord, unchecked((uint)(int)data)),
            RegistryValueKind.QWord => new RegValue(RegKind.QWord, unchecked((ulong)(long)data)),
            RegistryValueKind.ExpandString => new RegValue(RegKind.ExpandString, (string)data),
            RegistryValueKind.MultiString => new RegValue(RegKind.MultiString, (string[])data),
            RegistryValueKind.Binary => new RegValue(RegKind.Binary, (byte[])data),
            _ => new RegValue(RegKind.String, data.ToString() ?? ""),
        };
    }

    public void SetValue(RegHive hive, string key, string name, RegValue value)
    {
        using var root = Root(hive);
        using var k = root.CreateSubKey(key, writable: true);
        switch (value.Kind)
        {
            case RegKind.DWord: k.SetValue(name, unchecked((int)Convert.ToUInt32(value.Data)), RegistryValueKind.DWord); break;
            case RegKind.QWord: k.SetValue(name, unchecked((long)Convert.ToUInt64(value.Data)), RegistryValueKind.QWord); break;
            case RegKind.ExpandString: k.SetValue(name, value.Data, RegistryValueKind.ExpandString); break;
            case RegKind.MultiString: k.SetValue(name, value.Data, RegistryValueKind.MultiString); break;
            case RegKind.Binary: k.SetValue(name, value.Data, RegistryValueKind.Binary); break;
            default: k.SetValue(name, value.Data, RegistryValueKind.String); break;
        }
    }

    public void DeleteValue(RegHive hive, string key, string name)
    {
        using var root = Root(hive);
        using var k = root.OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}

public sealed class WindowsServiceManager(ICommandRunner commands, IRegistryAccess registry) : IServiceManager
{
    private static ServiceController? Find(string name) =>
        ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool Exists(string serviceName)
    {
        using var s = Find(serviceName);
        return s is not null;
    }

    public ServiceStartMode GetStartMode(string serviceName)
    {
        using var s = Find(serviceName) ?? throw new InvalidOperationException($"Serviço {serviceName} não existe.");
        var mode = s.StartType switch
        {
            WinStartMode.Boot => ServiceStartMode.Boot,
            WinStartMode.System => ServiceStartMode.System,
            WinStartMode.Automatic => ServiceStartMode.Automatic,
            WinStartMode.Manual => ServiceStartMode.Manual,
            _ => ServiceStartMode.Disabled,
        };
        if (mode == ServiceStartMode.Automatic &&
            registry.GetValue(RegHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{serviceName}", "DelayedAutostart")
                is { Data: 1u })
            mode = ServiceStartMode.AutomaticDelayed;
        return mode;
    }

    public void SetStartMode(string serviceName, ServiceStartMode mode)
    {
        var start = mode switch
        {
            ServiceStartMode.Boot => "boot",
            ServiceStartMode.System => "system",
            ServiceStartMode.Automatic => "auto",
            ServiceStartMode.AutomaticDelayed => "delayed-auto",
            ServiceStartMode.Manual => "demand",
            _ => "disabled",
        };
        var r = commands.RunAsync("sc.exe", $"config \"{serviceName}\" start= {start}").GetAwaiter().GetResult();
        if (!r.Success) throw new InvalidOperationException($"sc config {serviceName} falhou: {r.StdOut} {r.StdErr}".Trim());
    }

    public bool IsRunning(string serviceName)
    {
        using var s = Find(serviceName);
        return s?.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
    }

    public void Stop(string serviceName)
    {
        using var s = Find(serviceName);
        if (s is null || !s.CanStop) return;
        s.Stop();
        s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
    }

    public void Start(string serviceName)
    {
        using var s = Find(serviceName);
        if (s is null) return;
        s.Start();
        s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
    }
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    // Ferramentas de console (sc, powercfg, reg) escrevem na code page OEM (ex.: 850 no pt-BR).
    private static readonly Encoding OemEncoding = CreateOemEncoding();

    private static Encoding CreateOemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    public async Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {fileName}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new CommandResult(p.ExitCode, await stdout, await stderr);
    }
}

public sealed class WindowsElevationService : IElevationService
{
    public bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Reinicia o app pedindo elevação (UAC). Retorna false se o usuário cancelar.</summary>
    public static bool RelaunchElevated(string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC cancelado
        }
    }
}

public sealed class AppPaths : IAppPaths
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string DataFolder { get; } = Path.Combine(LocalAppData, "NinjaOptimizer");
    public string BackupFolder => Path.Combine(DataFolder, "backups");
    public string LogFolder => Path.Combine(DataFolder, "logs");

    public AppPaths() => MigrateLegacyFolder();

    /// <summary>
    /// Versões até 0.1 usavam a pasta "NinjasOptimizer". Movemos o histórico (journal) e os backups
    /// para a pasta nova, para que tudo continue podendo ser desfeito.
    /// </summary>
    private void MigrateLegacyFolder()
    {
        var legacy = Path.Combine(LocalAppData, "NinjasOptimizer");
        try
        {
            if (Directory.Exists(legacy) && !Directory.Exists(DataFolder))
                Directory.Move(legacy, DataFolder);
        }
        catch (IOException) { /* pasta em uso: tenta de novo na próxima abertura */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Exporta chaves com reg.exe, gerando .reg que o usuário pode importar manualmente se precisar.</summary>
public sealed class RegExeBackup(ICommandRunner commands) : IRegistryBackup
{
    public async Task<IReadOnlyList<string>> ExportAsync(IEnumerable<(RegHive Hive, string Key)> keys, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        var i = 0;
        foreach (var (hive, key) in keys.Distinct())
        {
            var full = (hive == RegHive.LocalMachine ? "HKLM\\" : "HKCU\\") + key;
            var safe = string.Concat(full.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '\\' ? '_' : c));
            var file = Path.Combine(folder, $"{i++:00}_{safe}.reg");
            var r = await commands.RunAsync("reg.exe", $"export \"{full}\" \"{file}\" /y /reg:64", ct);
            if (r.Success) files.Add(file); // chave inexistente: nada a salvar
        }
        return files;
    }
}
