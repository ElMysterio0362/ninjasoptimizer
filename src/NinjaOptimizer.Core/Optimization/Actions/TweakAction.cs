using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

/// <summary>
/// Estado anterior de uma ação, salvo no journal. É autossuficiente: a reversão
/// usa apenas estes dados, então funciona mesmo se o catálogo mudar entre versões.
/// </summary>
public sealed class ActionSnapshot
{
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, string?> Data { get; set; } = new();

    public string? Get(string key) => Data.TryGetValue(key, out var v) ? v : null;
}

public abstract class TweakAction
{
    public abstract string Kind { get; }
    public abstract string Describe();
    public virtual bool RequiresAdmin => true;
    public virtual bool Reversible => true;

    /// <summary>Chaves de registro que serão exportadas (.reg) antes de aplicar.</summary>
    public virtual IEnumerable<(RegHive Hive, string Key)> RegistryKeys => [];

    /// <summary>Lê o estado atual do sistema, antes de qualquer alteração.</summary>
    public abstract Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct);

    /// <summary>Aplica o ajuste. Pode acrescentar dados ao snapshot (ex.: GUID criado).</summary>
    public abstract Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct);

    public abstract Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct);

    protected ActionSnapshot NewSnapshot() => new() { Kind = Kind, Description = Describe() };
}

/// <summary>Reverte snapshots pelo tipo, sem depender da instância da ação.</summary>
public static class ActionReverter
{
    private static readonly Dictionary<string, Func<SystemContext, ActionSnapshot, CancellationToken, Task>> Reverters = new()
    {
        [RegistryValueAction.KindId] = RegistryValueAction.RevertAsync,
        [ServiceStartupAction.KindId] = ServiceStartupAction.RevertAsync,
        [PowerPlanAction.KindId] = PowerPlanAction.RevertAsync,
        [PowerSettingAction.KindId] = PowerSettingAction.RevertAsync,
        [CommandStateAction.KindId] = CommandStateAction.RevertAsync,
        [RegistryEachSubkeyAction.KindId] = RegistryEachSubkeyAction.RevertAsync,
        [DnsServersAction.KindId] = DnsServersAction.RevertAsync,
        [NetAdapterPowerAction.KindId] = NetAdapterPowerAction.RevertAsync,
    };

    public static Task RevertAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        if (!Reverters.TryGetValue(snapshot.Kind, out var revert))
            throw new NotSupportedException($"Tipo de ação desconhecido no journal: {snapshot.Kind}");
        return revert(ctx, snapshot, ct);
    }
}
