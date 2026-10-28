using NinjasOptimizer.Core.Abstractions;

namespace NinjasOptimizer.Core.Optimization.Actions;

/// <summary>Define um valor de registro; a reversão restaura o valor anterior ou o remove se não existia.</summary>
public sealed class RegistryValueAction(RegHive hive, string key, string name, RegValue value) : TweakAction
{
    public const string KindId = "registry";

    public RegHive Hive { get; } = hive;
    public string Key { get; } = key;
    public string Name { get; } = name;
    public RegValue Value { get; } = value;

    public override string Kind => KindId;

    // HKCU não precisa de administrador.
    public override bool RequiresAdmin => Hive == RegHive.LocalMachine;

    public override IEnumerable<(RegHive Hive, string Key)> RegistryKeys => [(Hive, Key)];

    public override string Describe() =>
        $"{(Hive == RegHive.LocalMachine ? "HKLM" : "HKCU")}\\{Key} → {Name} = {Value.Serialize()} ({Value.Kind})";

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
        ctx.Registry.SetValue(Hive, Key, Name, Value);
        return Task.CompletedTask;
    }

    public override Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct) =>
        Task.FromResult(Value.ValueEquals(ctx.Registry.GetValue(Hive, Key, Name)));

    public static Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var hive = Enum.Parse<RegHive>(s.Get("hive")!);
        var key = s.Get("key")!;
        var name = s.Get("name")!;
        if (bool.Parse(s.Get("existed") ?? "false"))
        {
            var kind = Enum.Parse<RegKind>(s.Get("kind")!);
            ctx.Registry.SetValue(hive, key, name, RegValue.Deserialize(kind, s.Get("value") ?? ""));
        }
        else
        {
            ctx.Registry.DeleteValue(hive, key, name);
        }
        return Task.CompletedTask;
    }
}
