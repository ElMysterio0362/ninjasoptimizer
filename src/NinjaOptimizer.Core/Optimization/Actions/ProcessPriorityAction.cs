using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

public enum CpuPriority : uint
{
    Idle = 1,
    Normal = 2,
    High = 3,
    BelowNormal = 5,
    AboveNormal = 6,
}

/// <summary>
/// Faz o Windows abrir um executável sempre com uma prioridade de CPU
/// (IFEO\&lt;exe&gt;\PerfOptions\CpuPriorityClass). Só escreve esse valor: nada de Debugger, GlobalFlag etc.
/// </summary>
public sealed class ProcessPriorityAction : TweakAction
{
    public const string KindId = "process-priority";
    internal const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string ValueName = "CpuPriorityClass";

    public string Exe { get; }
    public CpuPriority Priority { get; }

    public ProcessPriorityAction(string exe, CpuPriority priority)
    {
        if (!IsValidExe(exe)) throw new ArgumentException($"Nome de executável inválido: \"{exe}\".", nameof(exe));
        if (!Enum.IsDefined(priority)) throw new ArgumentException("Prioridade inválida.", nameof(priority)); // 4 (Tempo real) não existe no enum
        Exe = exe;
        Priority = priority;
    }

    /// <summary>Só o nome do arquivo, terminado em .exe, sem pastas nem caracteres inválidos.</summary>
    public static bool IsValidExe(string? exe) =>
        exe is { Length: > 4 and <= 255 } &&
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        exe.Trim() == exe &&
        exe.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0 &&
        !exe.Any(char.IsControl) &&
        exe.Trim('.').Length == exe.Length;

    private static string ExeKey(string exe) => $@"{Ifeo}\{exe}";
    private static string PerfKey(string exe) => $@"{Ifeo}\{exe}\PerfOptions";

    public override string Kind => KindId;
    public override IEnumerable<(RegHive Hive, string Key)> RegistryKeys => [(RegHive.LocalMachine, ExeKey(Exe))];
    public override string Describe() => $@"{Exe} → prioridade de CPU {LabelsPriority.Of(Priority)} (IFEO\{Exe}\PerfOptions\{ValueName} = {(uint)Priority})";

    public override Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var r = ctx.Registry;
        var snap = NewSnapshot();
        snap.Data["exe"] = Exe;
        snap.Data["exeKeyExisted"] = r.GetSubKeyNames(RegHive.LocalMachine, Ifeo).Contains(Exe, StringComparer.OrdinalIgnoreCase).ToString();
        snap.Data["perfKeyExisted"] = r.GetSubKeyNames(RegHive.LocalMachine, ExeKey(Exe)).Contains("PerfOptions", StringComparer.OrdinalIgnoreCase).ToString();
        snap.Data["prev"] = r.GetValue(RegHive.LocalMachine, PerfKey(Exe), ValueName) is { Kind: RegKind.DWord } v ? v.Serialize() : null;
        return Task.FromResult(snap);
    }

    public override Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        ctx.Registry.SetValue(RegHive.LocalMachine, PerfKey(Exe), ValueName, RegValue.Dword((uint)Priority));
        return Task.CompletedTask;
    }

    public override Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct) =>
        Task.FromResult(RegValue.Dword((uint)Priority).ValueEquals(ctx.Registry.GetValue(RegHive.LocalMachine, PerfKey(Exe), ValueName)));

    /// <summary>Prioridade configurada hoje para o executável (null = nenhuma).</summary>
    public static CpuPriority? Current(IRegistryAccess registry, string exe) =>
        IsValidExe(exe) && registry.GetValue(RegHive.LocalMachine, PerfKey(exe), ValueName) is { Data: uint v } && Enum.IsDefined((CpuPriority)v)
            ? (CpuPriority)v : null;

    public static Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var exe = s.Get("exe");
        // O journal fica em disco: confere de novo antes de escrever no registro.
        if (!IsValidExe(exe)) throw new InvalidOperationException("Registro do histórico com executável inválido.");
        var r = ctx.Registry;
        if (s.Get("prev") is { } prev) r.SetValue(RegHive.LocalMachine, PerfKey(exe!), ValueName, RegValue.Deserialize(RegKind.DWord, prev));
        else r.DeleteValue(RegHive.LocalMachine, PerfKey(exe!), ValueName);
        // Apaga só as chaves que este ajuste criou, e só se ficaram vazias.
        if (s.Get("perfKeyExisted") != "True") r.DeleteKeyIfEmpty(RegHive.LocalMachine, PerfKey(exe!));
        if (s.Get("exeKeyExisted") != "True") r.DeleteKeyIfEmpty(RegHive.LocalMachine, ExeKey(exe!));
        return Task.CompletedTask;
    }
}

public static class LabelsPriority
{
    public static string Of(CpuPriority p) => p switch
    {
        CpuPriority.Idle => "Ociosa",
        CpuPriority.BelowNormal => "Abaixo do normal",
        CpuPriority.Normal => "Normal",
        CpuPriority.AboveNormal => "Acima do normal",
        CpuPriority.High => "Alta",
        _ => p.ToString(),
    };
}
