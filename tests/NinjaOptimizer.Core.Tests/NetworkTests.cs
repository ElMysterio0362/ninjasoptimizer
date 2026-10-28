using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Tests;

public sealed class NetworkTests
{
    private const string Ifaces = DnsServersAction.InterfacesKey;
    private const string A = "{AAAA0000-0000-0000-0000-000000000001}";
    private const string B = "{BBBB0000-0000-0000-0000-000000000002}";

    private static (SystemContext Ctx, FakeRegistry Reg, FakePowerCfg Cmd) New()
    {
        var reg = new FakeRegistry();
        var cmd = new FakePowerCfg { Registry = reg };
        cmd.Adapters.Clear();
        cmd.Adapters.AddRange([(12, A), (7, B)]);
        reg.SubKeys[$@"LocalMachine\{Ifaces}"] = [A, B, "{CCCC0000-0000-0000-0000-000000000003}"];
        return (new SystemContext(reg, new FakeServices(), cmd), reg, cmd);
    }

    [Fact]
    public async Task Dns_restores_each_adapter_to_its_previous_state()
    {
        var (ctx, reg, _) = New();
        reg.SetValue(RegHive.LocalMachine, $@"{Ifaces}\{B}", "NameServer", RegValue.Str("8.8.8.8,8.8.4.4"));
        var action = new DnsServersAction(["1.1.1.1", "1.0.0.1"], "Cloudflare");

        var snap = await action.CaptureAsync(ctx, default);
        await action.ApplyAsync(ctx, snap, default);
        Assert.True(await action.IsAppliedAsync(ctx, default));
        Assert.Equal("1.1.1.1,1.0.0.1", reg.GetValue(RegHive.LocalMachine, $@"{Ifaces}\{A}", "NameServer")!.Data);

        await DnsServersAction.RevertAsync(ctx, snap, default);
        Assert.Null(reg.GetValue(RegHive.LocalMachine, $@"{Ifaces}\{A}", "NameServer")); // volta ao automático
        Assert.Equal("8.8.8.8,8.8.4.4", reg.GetValue(RegHive.LocalMachine, $@"{Ifaces}\{B}", "NameServer")!.Data);
        Assert.False(await action.IsAppliedAsync(ctx, default));
    }

    [Fact]
    public async Task Dns_fails_clearly_without_connected_adapters()
    {
        var (ctx, _, cmd) = New();
        cmd.Adapters.Clear();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DnsServersAction(["1.1.1.1"], "x").CaptureAsync(ctx, default));
        Assert.Contains("placa de rede", ex.Message);
    }

    [Fact]
    public void Adapter_list_parsing_ignores_noise()
    {
        var list = DnsServersAction.ParseAdapters("12|{abc}\r\n\r\nlixo\r\nx|{d}\r\n7|{DEF}\r\n");
        Assert.Equal([(12, "{ABC}"), (7, "{DEF}")], list);
    }

    [Fact]
    public async Task Each_subkey_value_is_set_and_reverted_per_interface()
    {
        var (ctx, reg, _) = New();
        reg.SetValue(RegHive.LocalMachine, $@"{Ifaces}\{A}", "TCPNoDelay", RegValue.Dword(0));
        var action = new RegistryEachSubkeyAction(RegHive.LocalMachine, Ifaces, "TCPNoDelay", RegValue.Dword(1), "Placas");

        var snap = await action.CaptureAsync(ctx, default);
        await action.ApplyAsync(ctx, snap, default);
        Assert.True(await action.IsAppliedAsync(ctx, default));

        await RegistryEachSubkeyAction.RevertAsync(ctx, snap, default);
        Assert.Equal(0u, reg.GetValue(RegHive.LocalMachine, $@"{Ifaces}\{A}", "TCPNoDelay")!.Data);
        Assert.Null(reg.GetValue(RegHive.LocalMachine, $@"{Ifaces}\{B}", "TCPNoDelay"));
    }
}
