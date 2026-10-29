using System.Text.RegularExpressions;
using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

/// <summary>
/// Desmarca "Permitir que o computador desligue este dispositivo para economizar energia" nas placas de rede
/// físicas (só essa opção: Wake-on-LAN e o resto ficam como estão). O snapshot guarda, por GUID da placa
/// (o nome muda, o GUID não), o valor anterior.
/// </summary>
public sealed partial class NetAdapterPowerAction : TweakAction
{
    public const string KindId = "netadapter-power";

    internal const string ListCommand =
        "-NoProfile -NonInteractive -Command \"Get-NetAdapter -Physical | ForEach-Object { " +
        "$pm = Get-NetAdapterPowerManagement -Name $_.Name -ErrorAction SilentlyContinue; " +
        "\\\"$($_.InterfaceGuid)|$($_.Status)|$($pm.AllowComputerToTurnOffDevice)\\\" }\"";

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex GuidRegex();

    public override string Kind => KindId;
    public override string Describe() => "Placas de rede físicas → não deixar o Windows desligar para economizar energia";

    internal sealed record Adapter(string Guid, string Status, string Value)
    {
        /// <summary>Só "Enabled"/"Disabled" são valores reais; vazio ou "Unsupported" = driver não aceita a opção.</summary>
        public bool Supported => Value is "Enabled" or "Disabled";
    }

    internal static IReadOnlyList<Adapter> Parse(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length == 3 && GuidRegex().IsMatch(p[0]))
            .Select(p => new Adapter(p[0].ToUpperInvariant(), p[1], p[2]))
            .ToList();

    private static async Task<IReadOnlyList<Adapter>> ListAsync(SystemContext ctx, CancellationToken ct)
    {
        var r = await ctx.Commands.RunAsync("powershell", ListCommand, ct);
        return r.Success ? Parse(r.StdOut) : [];
    }

    internal static string SetCommand(string guid, string value)
    {
        // Só valores conhecidos e GUID validado entram no comando.
        if (!GuidRegex().IsMatch(guid)) throw new ArgumentException("GUID de placa inválido.", nameof(guid));
        if (value is not ("Enabled" or "Disabled")) throw new ArgumentException("Valor inválido.", nameof(value));
        return "-NoProfile -NonInteractive -Command \"Get-NetAdapter -Physical | Where-Object { $_.InterfaceGuid -eq '" + guid +
               "' } | Set-NetAdapterPowerManagement -AllowComputerToTurnOffDevice " + value + " -ErrorAction Stop\"";
    }

    public override async Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var adapters = (await ListAsync(ctx, ct)).Where(a => a.Supported).ToList();
        if (adapters.Count == 0) throw new InvalidOperationException("Nenhuma placa de rede aceita essa opção de energia.");
        var snap = NewSnapshot();
        snap.Data["count"] = adapters.Count.ToString();
        for (var i = 0; i < adapters.Count; i++)
        {
            snap.Data[$"a{i}.guid"] = adapters[i].Guid;
            snap.Data[$"a{i}.prev"] = adapters[i].Value;
            snap.Data[$"a{i}.status"] = adapters[i].Status;
        }
        return snap;
    }

    public override async Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        var count = int.Parse(snapshot.Get("count")!);
        int changed = 0, candidates = 0;
        for (var i = 0; i < count; i++)
        {
            // Placa desativada no Windows ou que já está como queremos: não mexe.
            if (snapshot.Get($"a{i}.status") == "Disabled" || snapshot.Get($"a{i}.prev") != "Enabled") continue;
            candidates++;
            var r = await ctx.Commands.RunAsync("powershell", SetCommand(snapshot.Get($"a{i}.guid")!, "Disabled"), ct);
            snapshot.Data[$"a{i}.applied"] = r.Success.ToString();
            if (r.Success) changed++;
        }
        if (candidates > 0 && changed == 0)
            throw new InvalidOperationException("O driver das placas de rede recusou a mudança.");
    }

    public override async Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct)
    {
        var supported = (await ListAsync(ctx, ct)).Where(a => a.Supported && a.Status != "Disabled").ToList();
        return supported.Count > 0 && supported.All(a => a.Value != "Enabled");
    }

    public static async Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var count = int.Parse(s.Get("count")!);
        var present = (await ListAsync(ctx, ct)).Select(a => a.Guid).ToHashSet();
        for (var i = 0; i < count; i++)
        {
            if (s.Get($"a{i}.applied") != "True") continue;
            var guid = s.Get($"a{i}.guid")!;
            if (!present.Contains(guid)) continue; // placa removida desde então: nada a restaurar
            var r = await ctx.Commands.RunAsync("powershell", SetCommand(guid, s.Get($"a{i}.prev")!), ct);
            if (!r.Success) throw new InvalidOperationException($"Não foi possível restaurar a placa {guid}: {r.StdErr}".Trim());
        }
    }
}
