using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Startup;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, MachineFolder }

public sealed record StartupEntry(string Name, string Command, StartupSource Source, bool Enabled)
{
    /// <summary>Itens de "todos os usuários" exigem administrador para ligar/desligar.</summary>
    public bool AllUsers => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.MachineFolder;
    public string Id => $"{Source}|{Name}";
}

/// <summary>Arquivo na pasta Inicializar (atalho ou programa).</summary>
public sealed record StartupFile(string FileName, string FullPath, bool AllUsers);

/// <summary>
/// Programas que abrem com o Windows. Usa o mesmo mecanismo do Gerenciador de Tarefas
/// (chaves StartupApproved): desativar não apaga nada, então ligar de volta restaura exatamente.
/// </summary>
public sealed class StartupManager(IRegistryAccess registry, Func<IEnumerable<StartupFile>> folderFiles)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    internal const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    internal static (RegHive Hive, string Key) ApprovedKey(StartupSource s) => s switch
    {
        StartupSource.UserRun => (RegHive.CurrentUser, Approved + @"\Run"),
        StartupSource.MachineRun => (RegHive.LocalMachine, Approved + @"\Run"),
        StartupSource.MachineRun32 => (RegHive.LocalMachine, Approved + @"\Run32"),
        StartupSource.UserFolder => (RegHive.CurrentUser, Approved + @"\StartupFolder"),
        _ => (RegHive.LocalMachine, Approved + @"\StartupFolder"),
    };

    /// <summary>Primeiro byte ímpar (0x03, 0x01…) = desativado; 0x02/0x06 ou ausente = ativado.</summary>
    internal static bool IsEnabled(RegValue? approved) =>
        approved?.Data is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;

    public IReadOnlyList<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        void FromRun(RegHive hive, string key, StartupSource source)
        {
            var approved = ApprovedKey(source);
            foreach (var name in registry.GetValueNames(hive, key).Where(n => n.Length > 0))
            {
                if (registry.GetValue(hive, key, name)?.Data is not string cmd) continue;
                list.Add(new StartupEntry(name, cmd, source, IsEnabled(registry.GetValue(approved.Hive, approved.Key, name))));
            }
        }
        FromRun(RegHive.CurrentUser, RunKey, StartupSource.UserRun);
        FromRun(RegHive.LocalMachine, RunKey, StartupSource.MachineRun);
        FromRun(RegHive.LocalMachine, Run32Key, StartupSource.MachineRun32);
        foreach (var f in folderFiles().Where(f => !f.FileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var source = f.AllUsers ? StartupSource.MachineFolder : StartupSource.UserFolder;
            var approved = ApprovedKey(source);
            list.Add(new StartupEntry(f.FileName, f.FullPath, source, IsEnabled(registry.GetValue(approved.Hive, approved.Key, f.FileName))));
        }
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SetEnabled(StartupEntry entry, bool enabled, DateTimeOffset? now = null)
    {
        var (hive, key) = ApprovedKey(entry.Source);
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes((now ?? DateTimeOffset.UtcNow).ToFileTime()).CopyTo(data, 4);
        registry.SetValue(hive, key, entry.Name, new RegValue(RegKind.Binary, data));
    }

    /// <summary>Nome amigável do programa a partir do comando (o executável sem caminho).</summary>
    public static string ExecutableOf(string command)
    {
        var c = command.Trim();
        string path;
        if (c.StartsWith('"')) { var end = c.IndexOf('"', 1); path = end > 0 ? c[1..end] : c.Trim('"'); }
        else
        {
            var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
        }
        var slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
