using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NinjaOptimizer.Core.Network;

/// <summary>Provedor de DNS que a tela DNS oferece. <see cref="TweakId"/> é o ajuste que aplica (com histórico).</summary>
public sealed record DnsProvider(string Name, string Note, string[] Servers, string TweakId)
{
    public bool IsAutomatic => Servers.Length == 0;
}

public static class DnsProviders
{
    public static IReadOnlyList<DnsProvider> All { get; } =
    [
        new("Cloudflare", "Rápido e com foco em privacidade.", ["1.1.1.1", "1.0.0.1"], "network.dns-cloudflare"),
        new("Google", "Rápido e muito usado.", ["8.8.8.8", "8.8.4.4"], "network.dns-google"),
        new("Quad9", "Bloqueia sites maliciosos conhecidos.", ["9.9.9.9", "149.112.112.112"], "network.dns-quad9"),
        new("OpenDNS", "Da Cisco; tem opção de filtro para família.", ["208.67.222.222", "208.67.220.220"], "network.dns-opendns"),
        new("Automático", "O DNS que o roteador ou o provedor de internet entrega.", [], "network.dns-automatic"),
    ];

    /// <summary>Ids de todos os ajustes de DNS (só um pode ficar ativo por vez).</summary>
    public static IReadOnlySet<string> TweakIds { get; } = All.Select(p => p.TweakId).ToHashSet();

    /// <summary>Domínios comuns usados no teste de velocidade.</summary>
    public static readonly string[] TestDomains = ["google.com", "youtube.com", "microsoft.com", "steampowered.com", "wikipedia.org"];
}

/// <summary>Uma consulta DNS a um servidor específico; devolve o tempo ou null se não respondeu.</summary>
public interface IDnsQuery
{
    Task<TimeSpan?> QueryAsync(IPAddress server, string domain, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Consulta tipo A por UDP direto ao servidor (sem mexer no DNS do sistema).</summary>
public sealed class UdpDnsQuery : IDnsQuery
{
    /// <summary>Monta o pacote da pergunta (cabeçalho + nome + tipo A + classe IN).</summary>
    public static byte[] BuildQuery(ushort id, string domain)
    {
        var labels = domain.Trim('.').Split('.');
        if (labels.Any(l => l.Length is 0 or > 63 || !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("Domínio inválido.", nameof(domain));
        var packet = new List<byte>(32);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 0x01; // recursão desejada
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // 1 pergunta
        packet.AddRange(header.ToArray());
        foreach (var l in labels)
        {
            packet.Add((byte)l.Length);
            packet.AddRange(l.Select(c => (byte)c));
        }
        packet.AddRange(new byte[] { 0, 0, 1, 0, 1 }); // fim do nome, tipo A, classe IN
        return [.. packet];
    }

    /// <summary>Resposta válida: mesmo id, bit de resposta e sem erro (RCODE 0).</summary>
    public static bool IsValidResponse(ReadOnlySpan<byte> data, ushort id) =>
        data.Length >= 12 && BinaryPrimitives.ReadUInt16BigEndian(data) == id && (data[2] & 0x80) != 0 && (data[3] & 0x0F) == 0;

    public async Task<TimeSpan?> QueryAsync(IPAddress server, string domain, TimeSpan timeout, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(id, domain);
        using var udp = new UdpClient(server.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(query, new IPEndPoint(server, 53), cts.Token);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                if (IsValidResponse(r.Buffer, id)) return sw.Elapsed;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }
}

public sealed record DnsSpeedResult(DnsProvider Provider, double? MedianMs, int Answered, int Asked);

/// <summary>Mede cada provedor com algumas consultas e usa a mediana (ignora as que não responderam).</summary>
public sealed class DnsSpeedTest(IDnsQuery query)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    public const int Rounds = 2;

    public async Task<IReadOnlyList<DnsSpeedResult>> RunAsync(IEnumerable<DnsProvider> providers, IPAddress? automaticServer, CancellationToken ct)
    {
        var results = new List<DnsSpeedResult>();
        foreach (var p in providers)
        {
            var server = p.IsAutomatic ? automaticServer : IPAddress.Parse(p.Servers[0]);
            if (server is null) { results.Add(new(p, null, 0, 0)); continue; }
            var times = new List<double>();
            var asked = 0;
            for (var round = 0; round < Rounds; round++)
                foreach (var d in DnsProviders.TestDomains)
                {
                    asked++;
                    if (await query.QueryAsync(server, d, Timeout, ct) is { } t) times.Add(t.TotalMilliseconds);
                }
            results.Add(new(p, Median(times), times.Count, asked));
        }
        return results;
    }

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var s = values.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>Mais rápido entre os que responderam pelo menos metade das consultas.</summary>
    public static DnsSpeedResult? Fastest(IEnumerable<DnsSpeedResult> results) =>
        results.Where(r => r.MedianMs is not null && r.Answered * 2 >= r.Asked).MinBy(r => r.MedianMs);
}
