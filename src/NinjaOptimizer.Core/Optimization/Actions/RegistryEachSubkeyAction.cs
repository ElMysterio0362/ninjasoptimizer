using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

/// <summary>
/// Define o mesmo valor em todas as subchaves de uma chave (ex.: uma por placa de rede em
/// Tcpip\Parameters\Interfaces). O snapshot guarda o valor anterior de cada subchave.
/// </summary>
public sealed class RegistryEachSubkeyAction(RegHive hive, string parentKey, string name, RegValue value, string label) : TweakAction
{
    public const string KindId = "registry-each";

    public override string Kind => KindId;
    public override bool RequiresAdmin => hive == RegHive.LocalMachine;
    public override IEnumerable<(RegHive Hive, string Key)> RegistryKeys => [(hive, parentKey)];
    public override string Describe() => $@"{label}: {name} = {value.Serialize()} em cada subchave de {parentKey}";

    public override Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var subs = ctx.Registry.GetSubKeyNames(hive, parentKey);
        if (subs.Count == 0) throw new InvalidOperationException($"Nenhum item encontrado para \"{label}\".");
        var snap = NewSnapshot();
        snap.Data["hive"] = hive.ToString();
        snap.Data["parent"] = parentKey;
        snap.Data["name"] = name;
        snap.Data["subkeys"] = string.Join("|", subs);
        foreach (var sub in subs)
        {
            var prev = ctx.Registry.GetValue(hive, $@"{parentKey}\{sub}", name);
            snap.Data["s:" + sub] = prev is null ? null : $"{prev.Kind}:{prev.Serialize()}";
        }
        return Task.FromResult(snap);
    }

    public override Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        foreach (var sub in snapshot.Get("subkeys")!.Split('|'))
            ctx.Registry.SetValue(hive, $@"{parentKey}\{sub}", name, value);
        return Task.CompletedTask;
    }

    public override Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct)
    {
        var subs = ctx.Registry.GetSubKeyNames(hive, parentKey);
        return Task.FromResult(subs.Count > 0 && subs.All(s => value.ValueEquals(ctx.Registry.GetValue(hive, $@"{parentKey}\{s}", name))));
    }

    public static Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var hive = Enum.Parse<RegHive>(s.Get("hive")!);
        var parent = s.Get("parent")!;
        var name = s.Get("name")!;
        foreach (var sub in s.Get("subkeys")!.Split('|'))
        {
            var key = $@"{parent}\{sub}";
            if (s.Get("s:" + sub) is { } prev)
            {
                var sep = prev.IndexOf(':');
                ctx.Registry.SetValue(hive, key, name, RegValue.Deserialize(Enum.Parse<RegKind>(prev[..sep]), prev[(sep + 1)..]));
            }
            else
            {
                ctx.Registry.DeleteValue(hive, key, name);
            }
        }
        return Task.CompletedTask;
    }
}
