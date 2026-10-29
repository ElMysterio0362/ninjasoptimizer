using System.Net;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Network;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Tests;

internal sealed class FakeDnsQuery(Dictionary<string, double?> msByServer) : IDnsQuery
{
    public List<string> Asked { get; } = [];
    public Task<TimeSpan?> QueryAsync(IPAddress server, string domain, TimeSpan timeout, CancellationToken ct)
    {
        Asked.Add($"{server} {domain}");
        return Task.FromResult(msByServer.GetValueOrDefault(server.ToString()) is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null);
    }
}

public sealed class ExtraMTests
{
    [Fact]
    public void Every_provider_has_a_matching_dns_tweak_and_old_cloudflare_id_is_kept()
    {
        Assert.Equal("network.dns-cloudflare", DnsProviders.All[0].TweakId);
        foreach (var p in DnsProviders.All)
        {
            var t = TweakCatalog.Get(p.TweakId);
            Assert.Empty(t.Profiles);
            Assert.Contains("empresa ou escola", string.Concat(t.Consequences));
            Assert.All(p.Servers, s => Assert.True(DnsServersAction.IsIp(s), s));
        }
    }

    [Fact]
    public async Task Old_cloudflare_snapshot_from_journal_still_reverts()
    {
        // Formato gravado pela versão anterior (antes da tela DNS)
        var reg = new FakeRegistry();
        var cmd = new FakePowerCfg { Registry = reg };
        var ctx = new SystemContext(reg, new FakeServices(), cmd);
        var guid = cmd.Adapters[0].Guid;
        reg.SetValue(RegHive.LocalMachine, $@"{DnsServersAction.InterfacesKey}\{guid}", "NameServer", RegValue.Str("1.1.1.1,1.0.0.1"));
        var old = new ActionSnapshot { Kind = DnsServersAction.KindId, Description = "DNS" };
        old.Data["count"] = "1";
        old.Data["a0.index"] = "12";
        old.Data["a0.guid"] = guid;
        old.Data["a0.prev"] = "8.8.8.8,8.8.4.4";

        await ActionReverter.RevertAsync(ctx, old, default);
        Assert.Equal("8.8.8.8,8.8.4.4", reg.GetValue(RegHive.LocalMachine, $@"{DnsServersAction.InterfacesKey}\{guid}", "NameServer")!.Data);
    }

    [Fact]
    public async Task Revert_never_passes_unvalidated_registry_text_to_powershell()
    {
        var reg = new FakeRegistry();
        var cmd = new FakePowerCfg { Registry = reg };
        var ctx = new SystemContext(reg, new FakeServices(), cmd);
        var snap = new ActionSnapshot { Kind = DnsServersAction.KindId };
        snap.Data["count"] = "1";
        snap.Data["a0.index"] = "12";
        snap.Data["a0.prev"] = "8.8.8.8,'; Remove-Item C:\\ #";
        await ActionReverter.RevertAsync(ctx, snap, default);
        Assert.Contains(cmd.Log, l => l.Contains("-ServerAddresses ('8.8.8.8')"));
        Assert.DoesNotContain(cmd.Log, l => l.Contains("Remove-Item"));

        snap.Data["a0.index"] = "12; rm";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ActionReverter.RevertAsync(ctx, snap, default));
    }

    [Fact]
    public async Task Automatic_resets_static_dns_and_undo_restores_it()
    {
        var reg = new FakeRegistry();
        var cmd = new FakePowerCfg { Registry = reg };
        var ctx = new SystemContext(reg, new FakeServices(), cmd);
        var key = $@"{DnsServersAction.InterfacesKey}\{cmd.Adapters[0].Guid}";
        reg.SetValue(RegHive.LocalMachine, key, "NameServer", RegValue.Str("192.168.0.1"));
        var a = new DnsServersAction([], "Automático");
        var snap = await a.CaptureAsync(ctx, default);
        await a.ApplyAsync(ctx, snap, default);
        Assert.Null(reg.GetValue(RegHive.LocalMachine, key, "NameServer"));
        Assert.True(await a.IsAppliedAsync(ctx, default));
        await DnsServersAction.RevertAsync(ctx, snap, default);
        Assert.Equal("192.168.0.1", reg.GetValue(RegHive.LocalMachine, key, "NameServer")!.Data);
    }

    [Fact]
    public void Dns_action_rejects_non_ip_servers() =>
        Assert.Throws<ArgumentException>(() => new DnsServersAction(["1.1.1.1'; rm"], "x"));

    [Fact]
    public async Task Speed_test_uses_median_and_picks_fastest_reliable_provider()
    {
        var q = new FakeDnsQuery(new() { ["1.1.1.1"] = 12, ["8.8.8.8"] = 20, ["9.9.9.9"] = 5, ["208.67.222.222"] = null, ["192.168.0.1"] = 3 });
        var results = await new DnsSpeedTest(q).RunAsync(DnsProviders.All, IPAddress.Parse("192.168.0.1"), default);

        Assert.Equal(12, results.Single(r => r.Provider.Name == "Cloudflare").MedianMs);
        Assert.Null(results.Single(r => r.Provider.Name == "OpenDNS").MedianMs);
        Assert.Equal("Automático", DnsSpeedTest.Fastest(results)!.Provider.Name);
        // Só consulta o servidor direto, 2 rodadas × 5 domínios por provedor
        Assert.Equal(DnsProviders.All.Count * DnsSpeedTest.Rounds * DnsProviders.TestDomains.Length, q.Asked.Count);
    }

    [Fact]
    public async Task Automatic_without_known_router_dns_is_skipped()
    {
        var results = await new DnsSpeedTest(new FakeDnsQuery(new())).RunAsync(DnsProviders.All.Where(p => p.IsAutomatic), null, default);
        Assert.Equal(0, results.Single().Asked);
    }

    [Theory]
    [InlineData(new double[] { 5, 1, 3 }, 3)]
    [InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
    public void Median(double[] values, double expected) => Assert.Equal(expected, DnsSpeedTest.Median(values));

    [Fact]
    public void Query_packet_and_response_validation()
    {
        var q = UdpDnsQuery.BuildQuery(0xABCD, "google.com");
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0, 6, (byte)'g' }, q[..14]);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1 }, q[^5..]);
        Assert.Throws<ArgumentException>(() => UdpDnsQuery.BuildQuery(1, "bad domain.com"));

        var resp = (byte[])q.Clone();
        resp[2] |= 0x80;
        Assert.True(UdpDnsQuery.IsValidResponse(resp, 0xABCD));
        Assert.False(UdpDnsQuery.IsValidResponse(resp, 0x1111)); // outro id
        resp[3] = 0x03; // NXDOMAIN
        Assert.False(UdpDnsQuery.IsValidResponse(resp, 0xABCD));
    }
}
