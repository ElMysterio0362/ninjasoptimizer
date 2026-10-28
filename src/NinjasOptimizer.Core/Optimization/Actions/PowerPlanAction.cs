using System.Text.RegularExpressions;
using NinjasOptimizer.Core.Abstractions;

namespace NinjasOptimizer.Core.Optimization.Actions;

/// <summary>
/// Ativa o plano "Desempenho Máximo" (Ultimate Performance) usando um GUID fixo do app,
/// para detectar se está aplicado e removê-lo na reversão sem deixar planos duplicados.
/// Se o Windows não permitir (ex.: notebooks com Modern Standby), cai para "Alto desempenho".
/// </summary>
public sealed partial class PowerPlanAction : TweakAction
{
    public const string KindId = "powerplan";

    public const string UltimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    /// <summary>GUID do plano criado pelo Ninjas Optimizer a partir do modelo Ultimate.</summary>
    public const string AppUltimateGuid = "a1b2c3d4-0000-4e1a-9c0a-44088c000001";

    public override string Kind => KindId;

    public override string Describe() => "Plano de energia → Desempenho Máximo (fallback: Alto desempenho)";

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();

    /// <summary>Extrai o primeiro GUID da saída do powercfg (independe do idioma do Windows).</summary>
    public static string? ParseGuid(string output)
    {
        var m = GuidRegex().Match(output);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public static IReadOnlyList<string> ParseAllGuids(string output) =>
        GuidRegex().Matches(output).Select(m => m.Value.ToLowerInvariant()).ToList();

    public static async Task<string?> GetActiveSchemeAsync(ICommandRunner cmd, CancellationToken ct)
    {
        var r = await cmd.RunAsync("powercfg", "/getactivescheme", ct);
        return r.Success ? ParseGuid(r.StdOut) : null;
    }

    public override async Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var snap = NewSnapshot();
        snap.Data["previous"] = await GetActiveSchemeAsync(ctx.Commands, ct) ?? BalancedGuid;
        return snap;
    }

    public override async Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        var list = await ctx.Commands.RunAsync("powercfg", "/list", ct);
        var exists = list.Success && ParseAllGuids(list.StdOut).Contains(AppUltimateGuid);

        if (!exists)
        {
            var dup = await ctx.Commands.RunAsync("powercfg", $"/duplicatescheme {UltimateTemplateGuid} {AppUltimateGuid}", ct);
            if (dup.Success) snapshot.Data["created"] = AppUltimateGuid;
        }

        var activate = await ctx.Commands.RunAsync("powercfg", $"/setactive {AppUltimateGuid}", ct);
        if (activate.Success)
        {
            snapshot.Data["applied"] = AppUltimateGuid;
            return;
        }

        var fallback = await ctx.Commands.RunAsync("powercfg", $"/setactive {HighPerformanceGuid}", ct);
        if (!fallback.Success)
            throw new InvalidOperationException(
                "O Windows não permitiu ativar planos de alto desempenho (comum em notebooks com Modern Standby). " +
                fallback.StdErr + fallback.StdOut);
        snapshot.Data["applied"] = HighPerformanceGuid;
    }

    public override async Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct)
    {
        var active = await GetActiveSchemeAsync(ctx.Commands, ct);
        return active is AppUltimateGuid or HighPerformanceGuid;
    }

    public static async Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var previous = s.Get("previous") ?? BalancedGuid;
        var r = await ctx.Commands.RunAsync("powercfg", $"/setactive {previous}", ct);
        if (!r.Success)
            await ctx.Commands.RunAsync("powercfg", $"/setactive {BalancedGuid}", ct);

        // Remove o plano criado pelo app (só se fomos nós que o criamos).
        if (s.Get("created") is { } created && created != previous)
            await ctx.Commands.RunAsync("powercfg", $"/delete {created}", ct);
    }
}
