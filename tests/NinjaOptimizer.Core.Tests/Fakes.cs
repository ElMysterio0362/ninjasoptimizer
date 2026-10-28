using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Tests;

internal sealed class FakeRegistry : IRegistryAccess
{
    public Dictionary<string, RegValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? FailOnSetName { get; set; }

    private static string K(RegHive h, string key, string name) => $"{h}\\{key}\\{name}";

    public RegValue? GetValue(RegHive hive, string key, string name) =>
        Values.TryGetValue(K(hive, key, name), out var v) ? v : null;

    public void SetValue(RegHive hive, string key, string name, RegValue value)
    {
        if (name == FailOnSetName) throw new UnauthorizedAccessException("Acesso negado (simulado)");
        Values[K(hive, key, name)] = value;
    }

    public void DeleteValue(RegHive hive, string key, string name) => Values.Remove(K(hive, key, name));
}

internal sealed class FakeServices : IServiceManager
{
    public Dictionary<string, (ServiceStartMode Mode, bool Running)> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string n) => Services.ContainsKey(n);
    public ServiceStartMode GetStartMode(string n) => Services[n].Mode;
    public void SetStartMode(string n, ServiceStartMode mode) => Services[n] = (mode, Services[n].Running);
    public bool IsRunning(string n) => Services[n].Running;
    public void Stop(string n) => Services[n] = (Services[n].Mode, false);
    public void Start(string n) => Services[n] = (Services[n].Mode, true);
}

/// <summary>Simula o powercfg com planos e plano ativo em memória.</summary>
internal sealed class FakePowerCfg : ICommandRunner
{
    public string Active { get; set; } = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public HashSet<string> Schemes { get; } = ["381b4222-f694-41f0-9685-ff5bb260df2e", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"];
    public bool BlockUltimate { get; set; }
    public List<string> Log { get; } = new();

    /// <summary>Valores AC das configurações por "plano|subgrupo|configuração". Padrão: 50.</summary>
    public Dictionary<string, uint> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Configurações escondidas: "powercfg /query" não as lista (igual ao Windows).</summary>
    public HashSet<string> HiddenSettings { get; } = ["0cc5b647-c1df-4637-891a-dec35c318583"];
    private uint GetSetting(string scheme, string sub, string set) => Settings.TryGetValue($"{scheme}|{sub}|{set}", out var v) ? v : 50;

    /// <summary>Estados de recursos ligados/desligados por comando.</summary>
    public bool MemoryCompression { get; set; } = true;
    public bool Hibernate { get; set; } = true;

    /// <summary>Registro onde o powercfg grava HibernateEnabled (opcional).</summary>
    public FakeRegistry? Registry { get; set; }

    public Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        Log.Add(arguments);
        if (fileName == "powershell")
        {
            if (arguments.Contains("(Get-MMAgent).MemoryCompression")) return Task.FromResult(new CommandResult(0, MemoryCompression ? "True\r\n" : "False\r\n", ""));
            if (arguments.Contains("Enable-MMAgent -MemoryCompression")) { MemoryCompression = true; return Task.FromResult(new CommandResult(0, "", "")); }
            if (arguments.Contains("Disable-MMAgent -MemoryCompression")) { MemoryCompression = false; return Task.FromResult(new CommandResult(0, "", "")); }
            return Task.FromResult(new CommandResult(1, "", "comando desconhecido"));
        }
        if (fileName == "reg" && arguments.Contains("HibernateEnabled"))
            return Task.FromResult(new CommandResult(0,
                $"\r\nHKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Power\r\n    HibernateEnabled    REG_DWORD    0x{(Hibernate ? 1 : 0)}\r\n", ""));
        if (arguments is "/h on" or "/h off")
        {
            Hibernate = arguments == "/h on";
            Registry?.SetValue(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", RegValue.Dword(Hibernate ? 1u : 0u));
            return Task.FromResult(new CommandResult(0, "", ""));
        }
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CommandResult ok(string o = "") => new(0, o, "");
        CommandResult fail() => new(1, "", "erro");
        return Task.FromResult(parts[0] switch
        {
            "/getactivescheme" => ok($"GUID do Esquema de Energia: {Active}  (Ativo)"),
            "/list" => ok(string.Join("\n", Schemes.Select(s => $"GUID do Esquema de Energia: {s}  (x)"))),
            "/duplicatescheme" when BlockUltimate => fail(),
            "/duplicatescheme" => Do(() => Schemes.Add(parts[2])),
            "/setactive" when Schemes.Contains(parts[1]) => Do(() => Active = parts[1]),
            "/setactive" => fail(),
            "/delete" => Do(() => Schemes.Remove(parts[1])),
            "/query" when Schemes.Contains(parts[1]) && HiddenSettings.Contains(parts[3]) =>
                ok($"GUID do Esquema de Energia: {parts[1]}\n  GUID do Subgrupo: {parts[2]}\n"),
            "/query" or "/qh" when Schemes.Contains(parts[1]) => ok(
                $"GUID do Esquema de Energia: {parts[1]}\n  GUID do Subgrupo: {parts[2]}\n    GUID da Configuração de Energia: {parts[3]}\n" +
                "      Configuração Mínima Possível: 0x00000000\n      Configuração Máxima Possível: 0x00000064\n" +
                "      Incremento de Configurações Possíveis: 0x00000001\n      Unidades de Configurações Possíveis: %\n" +
                $"    Índice de Configuração de Energia CA Atual: 0x{GetSetting(parts[1], parts[2], parts[3]):x8}\n" +
                "    Índice de Configuração de Energia CC Atual: 0x00000032"),
            "/query" or "/qh" => fail(),
            "/setacvalueindex" when Schemes.Contains(parts[1]) => Do(() => Settings[$"{parts[1]}|{parts[2]}|{parts[3]}"] = uint.Parse(parts[4])),
            "/setacvalueindex" => fail(),
            _ => fail(),
        });

        CommandResult Do(Action a) { a(); return ok(); }
    }
}

internal sealed class FakeRestorePoint(bool success = true) : IRestorePointService
{
    public int Calls { get; private set; }
    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new RestorePointResult(success, success ? "ok" : "falhou"));
    }
}

internal sealed class FakeBackup : IRegistryBackup
{
    public List<(RegHive, string)> Exported { get; } = new();
    public Task<IReadOnlyList<string>> ExportAsync(IEnumerable<(RegHive Hive, string Key)> keys, string folder, CancellationToken ct = default)
    {
        var list = keys.ToList();
        Exported.AddRange(list);
        return Task.FromResult<IReadOnlyList<string>>(list.Select((k, i) => Path.Combine(folder, $"{i}.reg")).ToList());
    }
}

internal sealed class FakeElevation(bool elevated = true) : IElevationService
{
    public bool IsElevated { get; set; } = elevated;
}

internal sealed class TempPaths : IAppPaths, IDisposable
{
    public string DataFolder { get; } = Path.Combine(Path.GetTempPath(), "ninjas-tests-" + Guid.NewGuid().ToString("N"));
    public string BackupFolder => Path.Combine(DataFolder, "backups");
    public string LogFolder => Path.Combine(DataFolder, "logs");
    public void Dispose() { try { Directory.Delete(DataFolder, true); } catch { } }
}
