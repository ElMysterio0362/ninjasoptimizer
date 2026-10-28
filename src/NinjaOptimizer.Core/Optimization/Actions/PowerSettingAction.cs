using System.Text.RegularExpressions;
using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

/// <summary>
/// Muda uma configuração do plano de energia ativo (valor "na tomada"/AC) via powercfg, inclusive as
/// escondidas da interface do Windows, como o core parking. O snapshot guarda o plano e o valor anterior.
/// </summary>
public sealed partial class PowerSettingAction(string subgroupGuid, string settingGuid, uint acValue, string label) : TweakAction
{
    public const string KindId = "powersetting";

    public string Subgroup { get; } = subgroupGuid;
    public string Setting { get; } = settingGuid;
    public uint AcValue { get; } = acValue;
    public string Label { get; } = label;

    public override string Kind => KindId;
    public override string Describe() => $"Plano de energia ativo → {Label} = {AcValue} (na tomada)";

    [GeneratedRegex(@"0x([0-9a-fA-F]{1,8})")]
    private static partial Regex HexRegex();

    /// <summary>
    /// A saída do "powercfg /query" é traduzida pelo Windows, mas os dois últimos valores 0x… são sempre
    /// o índice atual na tomada (AC) e na bateria (DC).
    /// </summary>
    public static (uint Ac, uint Dc)? ParseQuery(string output)
    {
        var hex = HexRegex().Matches(output).Select(m => Convert.ToUInt32(m.Groups[1].Value, 16)).ToList();
        return hex.Count >= 2 ? (hex[^2], hex[^1]) : null;
    }

    private async Task<uint?> ReadAcAsync(ICommandRunner cmd, string scheme, CancellationToken ct)
    {
        // "/qh" inclui configurações escondidas (como o core parking), que "/query" omite.
        foreach (var verb in new[] { "/qh", "/query" })
        {
            var r = await cmd.RunAsync("powercfg", $"{verb} {scheme} {Subgroup} {Setting}", ct);
            if (r.Success && ParseQuery(r.StdOut) is { } values) return values.Ac;
        }
        return null;
    }

    public override async Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var scheme = await PowerPlanAction.GetActiveSchemeAsync(ctx.Commands, ct)
            ?? throw new InvalidOperationException("Não foi possível ler o plano de energia ativo.");
        var current = await ReadAcAsync(ctx.Commands, scheme, ct)
            ?? throw new InvalidOperationException($"O Windows não expõe a configuração \"{Label}\" neste PC.");
        var snap = NewSnapshot();
        snap.Data["scheme"] = scheme;
        snap.Data["subgroup"] = Subgroup;
        snap.Data["setting"] = Setting;
        snap.Data["ac"] = current.ToString();
        return snap;
    }

    public override async Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        var scheme = snapshot.Get("scheme")!;
        var r = await ctx.Commands.RunAsync("powercfg", $"/setacvalueindex {scheme} {Subgroup} {Setting} {AcValue}", ct);
        if (!r.Success) throw new InvalidOperationException($"powercfg recusou \"{Label}\": {r.StdOut} {r.StdErr}".Trim());
        await ctx.Commands.RunAsync("powercfg", $"/setactive {scheme}", ct); // reaplica o plano para valer na hora
    }

    public override async Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct)
    {
        var scheme = await PowerPlanAction.GetActiveSchemeAsync(ctx.Commands, ct);
        return scheme is not null && await ReadAcAsync(ctx.Commands, scheme, ct) == AcValue;
    }

    public static async Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var scheme = s.Get("scheme")!;
        // O plano pode ter sido apagado (ex.: plano Desempenho Máximo revertido antes): nada a restaurar.
        var list = await ctx.Commands.RunAsync("powercfg", "/list", ct);
        if (list.Success && !PowerPlanAction.ParseAllGuids(list.StdOut).Contains(scheme)) return;

        var r = await ctx.Commands.RunAsync("powercfg",
            $"/setacvalueindex {scheme} {s.Get("subgroup")} {s.Get("setting")} {s.Get("ac")}", ct);
        if (!r.Success) throw new InvalidOperationException($"powercfg: {r.StdOut} {r.StdErr}".Trim());

        var active = await PowerPlanAction.GetActiveSchemeAsync(ctx.Commands, ct);
        if (active == scheme) await ctx.Commands.RunAsync("powercfg", $"/setactive {scheme}", ct);
    }
}
