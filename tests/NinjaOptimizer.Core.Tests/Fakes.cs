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

    public Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        Log.Add(arguments);
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
