namespace NinjaOptimizer.Core.Abstractions;

public enum RegHive { LocalMachine, CurrentUser }

public enum RegKind { DWord, QWord, String, ExpandString, MultiString, Binary }

/// <summary>Valor de registro tipado. <see cref="Data"/> é uint/ulong/string/string[]/byte[].</summary>
public sealed record RegValue(RegKind Kind, object Data)
{
    public static RegValue Dword(uint v) => new(RegKind.DWord, v);
    public static RegValue Str(string v) => new(RegKind.String, v);

    /// <summary>Serialização estável para o journal (JSON).</summary>
    public string Serialize() => Kind switch
    {
        RegKind.DWord => Convert.ToUInt32(Data).ToString(),
        RegKind.QWord => Convert.ToUInt64(Data).ToString(),
        RegKind.String or RegKind.ExpandString => (string)Data,
        RegKind.MultiString => string.Join("\u0000", (string[])Data),
        RegKind.Binary => Convert.ToBase64String((byte[])Data),
        _ => throw new NotSupportedException(Kind.ToString()),
    };

    public static RegValue Deserialize(RegKind kind, string raw) => kind switch
    {
        RegKind.DWord => new(kind, uint.Parse(raw)),
        RegKind.QWord => new(kind, ulong.Parse(raw)),
        RegKind.String or RegKind.ExpandString => new(kind, raw),
        RegKind.MultiString => new(kind, raw.Length == 0 ? Array.Empty<string>() : raw.Split('\u0000')),
        RegKind.Binary => new(kind, Convert.FromBase64String(raw)),
        _ => throw new NotSupportedException(kind.ToString()),
    };

    public bool ValueEquals(RegValue? other) =>
        other is not null && other.Kind == Kind && other.Serialize() == Serialize();
}

public interface IRegistryAccess
{
    RegValue? GetValue(RegHive hive, string key, string name);
    void SetValue(RegHive hive, string key, string name, RegValue value);
    void DeleteValue(RegHive hive, string key, string name);
}

public enum ServiceStartMode { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

public interface IServiceManager
{
    bool Exists(string serviceName);
    ServiceStartMode GetStartMode(string serviceName);
    void SetStartMode(string serviceName, ServiceStartMode mode);
    bool IsRunning(string serviceName);
    void Stop(string serviceName);
    void Start(string serviceName);
}

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken ct = default);
}

public sealed record RestorePointResult(bool Success, string Message);

public interface IRestorePointService
{
    Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default);
}

/// <summary>Exporta chaves de registro (.reg) antes de alterá-las.</summary>
public interface IRegistryBackup
{
    /// <returns>Arquivos .reg gerados (chaves inexistentes são ignoradas).</returns>
    Task<IReadOnlyList<string>> ExportAsync(IEnumerable<(RegHive Hive, string Key)> keys, string folder, CancellationToken ct = default);
}

public interface IElevationService
{
    bool IsElevated { get; }
}

public interface IAppPaths
{
    string DataFolder { get; }
    string BackupFolder { get; }
    string LogFolder { get; }
}

/// <summary>Agrupa o acesso ao sistema usado pelas ações de otimização.</summary>
public sealed class SystemContext(IRegistryAccess registry, IServiceManager services, ICommandRunner commands)
{
    public IRegistryAccess Registry { get; } = registry;
    public IServiceManager Services { get; } = services;
    public ICommandRunner Commands { get; } = commands;
}
