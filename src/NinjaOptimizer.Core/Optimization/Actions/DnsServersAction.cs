using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

/// <summary>
/// Define servidores DNS (IPv4) nas placas de rede físicas conectadas. O snapshot guarda o DNS
/// fixo anterior de cada placa; sem DNS fixo, a reversão volta para o automático (DHCP).
/// </summary>
public sealed class DnsServersAction(string[] servers, string label) : TweakAction
{
    public const string KindId = "dns";
    internal const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    private const string ListAdapters =
        "-NoProfile -NonInteractive -Command \"Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | " +
        "ForEach-Object { \\\"$($_.ifIndex)|$($_.InterfaceGuid)\\\" }\"";

    public override string Kind => KindId;
    public override IEnumerable<(RegHive Hive, string Key)> RegistryKeys => [(RegHive.LocalMachine, InterfacesKey)];
    public override string Describe() => $"DNS das placas conectadas → {label} ({string.Join(", ", servers)})";

    internal static IReadOnlyList<(int Index, string Guid)> ParseAdapters(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && p[1].StartsWith('{'))
            .Select(p => (int.Parse(p[0]), p[1].ToUpperInvariant()))
            .ToList();

    private static async Task<IReadOnlyList<(int Index, string Guid)>> AdaptersAsync(SystemContext ctx, CancellationToken ct)
    {
        var r = await ctx.Commands.RunAsync("powershell", ListAdapters, ct);
        return r.Success ? ParseAdapters(r.StdOut) : [];
    }

    private static string? StaticDns(SystemContext ctx, string guid) =>
        ctx.Registry.GetValue(RegHive.LocalMachine, $@"{InterfacesKey}\{guid}", "NameServer")?.Data as string;

    internal static string Normalize(string? list) =>
        string.Join(",", (list ?? "").Split((char[])[',', ' '], StringSplitOptions.RemoveEmptyEntries));

    private static string ServerArg(IEnumerable<string> list) => "(" + string.Join(",", list.Select(s => $"'{s}'")) + ")";

    public override async Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var adapters = await AdaptersAsync(ctx, ct);
        if (adapters.Count == 0) throw new InvalidOperationException("Nenhuma placa de rede conectada foi encontrada.");
        var snap = NewSnapshot();
        snap.Data["count"] = adapters.Count.ToString();
        for (var i = 0; i < adapters.Count; i++)
        {
            snap.Data[$"a{i}.index"] = adapters[i].Index.ToString();
            snap.Data[$"a{i}.guid"] = adapters[i].Guid;
            snap.Data[$"a{i}.prev"] = Normalize(StaticDns(ctx, adapters[i].Guid));
        }
        return snap;
    }

    public override async Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        var count = int.Parse(snapshot.Get("count")!);
        for (var i = 0; i < count; i++)
        {
            var r = await ctx.Commands.RunAsync("powershell",
                $"-NoProfile -NonInteractive -Command \"Set-DnsClientServerAddress -InterfaceIndex {snapshot.Get($"a{i}.index")} -ServerAddresses {ServerArg(servers)}\"", ct);
            if (!r.Success) throw new InvalidOperationException($"O Windows recusou o DNS: {r.StdErr}".Trim());
        }
        await ctx.Commands.RunAsync("ipconfig", "/flushdns", ct);
    }

    public override async Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct)
    {
        var adapters = await AdaptersAsync(ctx, ct);
        var target = Normalize(string.Join(",", servers));
        return adapters.Count > 0 && adapters.All(a => Normalize(StaticDns(ctx, a.Guid)) == target);
    }

    public static async Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var count = int.Parse(s.Get("count")!);
        for (var i = 0; i < count; i++)
        {
            var prev = s.Get($"a{i}.prev");
            var arg = string.IsNullOrEmpty(prev) ? "-ResetServerAddresses" : "-ServerAddresses " + ServerArg(prev.Split(','));
            // Placa removida desde então: o comando falha e não há nada a restaurar.
            await ctx.Commands.RunAsync("powershell",
                $"-NoProfile -NonInteractive -Command \"Set-DnsClientServerAddress -InterfaceIndex {s.Get($"a{i}.index")} {arg}\"", ct);
        }
        await ctx.Commands.RunAsync("ipconfig", "/flushdns", ct);
    }
}
