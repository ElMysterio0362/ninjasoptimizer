using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

public sealed record CommandLine(string File, string Arguments);

/// <summary>
/// Liga/desliga um recurso do Windows por comandos (ex.: compressão de memória, hibernação).
/// Um comando consulta o estado atual; o snapshot guarda esse estado e os comandos de ligar e
/// desligar, para a reversão funcionar sozinha a partir do journal.
/// </summary>
public sealed class CommandStateAction(
    string label,
    CommandLine query,
    Func<string, bool?> parseState,
    CommandLine turnOn,
    CommandLine turnOff,
    bool target,
    Func<SystemContext, bool?>? stateReader = null) : TweakAction
{
    public const string KindId = "commandstate";

    public string Label { get; } = label;
    public bool Target { get; } = target;

    public override string Kind => KindId;
    public override string Describe() => $"{Label} → {(Target ? "ligado" : "desligado")} ({(Target ? turnOn : turnOff).File} {(Target ? turnOn : turnOff).Arguments})";

    private async Task<bool?> ReadAsync(SystemContext ctx, CancellationToken ct)
    {
        if (stateReader is not null) return stateReader(ctx);
        var r = await ctx.Commands.RunAsync(query.File, query.Arguments, ct);
        return r.Success ? parseState(r.StdOut) : null;
    }

    public override async Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var state = await ReadAsync(ctx, ct) ?? throw new InvalidOperationException($"Não foi possível ler o estado de \"{Label}\".");
        var snap = NewSnapshot();
        snap.Data["was"] = state.ToString();
        snap.Data["onFile"] = turnOn.File;
        snap.Data["onArgs"] = turnOn.Arguments;
        snap.Data["offFile"] = turnOff.File;
        snap.Data["offArgs"] = turnOff.Arguments;
        return snap;
    }

    public override async Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        var cmd = Target ? turnOn : turnOff;
        var r = await ctx.Commands.RunAsync(cmd.File, cmd.Arguments, ct);
        if (!r.Success) throw new InvalidOperationException($"\"{Label}\": {r.StdErr} {r.StdOut}".Trim());
    }

    public override async Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct) => await ReadAsync(ctx, ct) == Target;

    public static async Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var was = bool.Parse(s.Get("was") ?? "true");
        var r = was
            ? await ctx.Commands.RunAsync(s.Get("onFile")!, s.Get("onArgs")!, ct)
            : await ctx.Commands.RunAsync(s.Get("offFile")!, s.Get("offArgs")!, ct);
        if (!r.Success) throw new InvalidOperationException($"{r.StdErr} {r.StdOut}".Trim());
    }

    // ───────── Leitores de estado comuns (independentes do idioma) ─────────

    /// <summary>"True"/"False" impresso pelo PowerShell.</summary>
    public static bool? ParseBool(string output) =>
        output.Trim().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) switch
        {
            var l when string.Equals(l, "True", StringComparison.OrdinalIgnoreCase) => true,
            var l when string.Equals(l, "False", StringComparison.OrdinalIgnoreCase) => false,
            _ => null,
        };

    /// <summary>Saída de "reg query ... /v Nome": "Nome  REG_DWORD  0x1".</summary>
    public static bool? ParseRegDword(string output)
    {
        var token = output.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(t => t.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
        return token is null ? null : Convert.ToUInt32(token[2..], 16) != 0;
    }
}
