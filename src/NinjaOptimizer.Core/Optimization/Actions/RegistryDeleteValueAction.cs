using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

/// <summary>
/// Apaga um valor do registro (ex.: um bloqueio de política). O snapshot tem o mesmo formato do
/// <see cref="RegistryValueAction"/>, então a reversão restaura o valor exatamente como era.
/// </summary>
public sealed class RegistryDeleteValueAction(RegHive hive, string key, string name) : TweakAction
{
    public RegHive Hive { get; } = hive;
    public string Key { get; } = key;
    public string Name { get; } = name;

    public override string Kind => RegistryValueAction.KindId;
    public override bool RequiresAdmin => Hive == RegHive.LocalMachine;
    public override IEnumerable<(RegHive Hive, string Key)> RegistryKeys => [(Hive, Key)];
    public override string Describe() => $"{(Hive == RegHive.LocalMachine ? "HKLM" : "HKCU")}\\{Key} → apagar {Name}";

    public override Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var snap = NewSnapshot();
        snap.Data["hive"] = Hive.ToString();
        snap.Data["key"] = Key;
        snap.Data["name"] = Name;
        var previous = ctx.Registry.GetValue(Hive, Key, Name);
        snap.Data["existed"] = (previous is not null).ToString();
        if (previous is not null)
        {
            snap.Data["kind"] = previous.Kind.ToString();
            snap.Data["value"] = previous.Serialize();
        }
        return Task.FromResult(snap);
    }

    public override Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        ctx.Registry.DeleteValue(Hive, Key, Name);
        return Task.CompletedTask;
    }

    public override Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct) =>
        Task.FromResult(ctx.Registry.GetValue(Hive, Key, Name) is null);
}
