using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Tests;

public sealed class ExtraDTests
{
    private const string A = "{AAAA0000-0000-0000-0000-000000000001}";
    private const string B = "{BBBB0000-0000-0000-0000-000000000002}";
    private const string C = "{CCCC0000-0000-0000-0000-000000000003}";
    private const string D = "{DDDD0000-0000-0000-0000-000000000004}";

    private static (SystemContext Ctx, FakePowerCfg Cmd) New(params NetPowerState[] adapters)
    {
        var cmd = new FakePowerCfg();
        cmd.NetPower.Clear();
        cmd.NetPower.AddRange(adapters);
        return (new SystemContext(new FakeRegistry(), new FakeServices(), cmd), cmd);
    }

    [Fact]
    public async Task Turns_off_power_saving_only_where_it_was_on_and_reverts_exactly()
    {
        var (ctx, cmd) = New(new NetPowerState(A, "Up", "Enabled"), new NetPowerState(B, "Disconnected", "Disabled"), new NetPowerState(C, "Up", "Unsupported"), new NetPowerState(D, "Disabled", "Enabled"));
        var action = new NetAdapterPowerAction();

        var snap = await action.CaptureAsync(ctx, default);
        await action.ApplyAsync(ctx, snap, default);
        Assert.Equal("Disabled", cmd.NetPower.Single(a => a.Guid == A).Value);
        Assert.Equal("Enabled", cmd.NetPower.Single(a => a.Guid == D).Value); // placa desativada não é tocada
        Assert.True(await action.IsAppliedAsync(ctx, default));

        await NetAdapterPowerAction.RevertAsync(ctx, snap, default);
        Assert.Equal(["Enabled", "Disabled", "Unsupported", "Enabled"], cmd.NetPower.Select(a => a.Value));
    }

    [Fact]
    public async Task IsApplied_ignores_unsupported_and_disabled_adapters()
    {
        var (ctx, _) = New(new NetPowerState(A, "Up", "Disabled"), new NetPowerState(C, "Up", "Unsupported"), new NetPowerState(D, "Disabled", "Enabled"));
        Assert.True(await new NetAdapterPowerAction().IsAppliedAsync(ctx, default));
        var (ctx2, _) = New(new NetPowerState(A, "Up", "Enabled"));
        Assert.False(await new NetAdapterPowerAction().IsAppliedAsync(ctx2, default));
    }

    [Fact]
    public async Task Revert_ignores_adapters_that_no_longer_exist()
    {
        var (ctx, cmd) = New(new NetPowerState(A, "Up", "Enabled"), new NetPowerState(B, "Up", "Enabled"));
        var action = new NetAdapterPowerAction();
        var snap = await action.CaptureAsync(ctx, default);
        await action.ApplyAsync(ctx, snap, default);
        cmd.NetPower.RemoveAll(a => a.Guid == B); // placa USB removida

        await NetAdapterPowerAction.RevertAsync(ctx, snap, default);
        Assert.Equal("Enabled", cmd.NetPower.Single().Value);
    }

    [Fact]
    public async Task Driver_refusing_one_adapter_skips_it_and_refusing_all_fails()
    {
        var (ctx, cmd) = New(new NetPowerState(A, "Up", "Enabled"), new NetPowerState(B, "Up", "Enabled"));
        cmd.RefuseNetPower.Add(B);
        var action = new NetAdapterPowerAction();
        var snap = await action.CaptureAsync(ctx, default);
        await action.ApplyAsync(ctx, snap, default);
        Assert.Equal("Disabled", cmd.NetPower[0].Value);
        Assert.Equal("Enabled", cmd.NetPower[1].Value);

        var (ctx2, cmd2) = New(new NetPowerState(A, "Up", "Enabled"));
        cmd2.RefuseNetPower.Add(A);
        var snap2 = await action.CaptureAsync(ctx2, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.ApplyAsync(ctx2, snap2, default));
    }

    [Fact]
    public async Task No_supported_adapter_fails_clearly()
    {
        var (ctx, _) = New(new NetPowerState(C, "Up", "Unsupported"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new NetAdapterPowerAction().CaptureAsync(ctx, default));
        Assert.Contains("placa de rede", ex.Message);
    }

    [Theory]
    [InlineData("{AAAA0000-0000-0000-0000-000000000001}'; Remove-Item C:\\", "Disabled")]
    [InlineData("not-a-guid", "Disabled")]
    [InlineData("{AAAA0000-0000-0000-0000-000000000001}", "Enabled; rm x")]
    public void Command_only_accepts_validated_guid_and_value(string guid, string value) =>
        Assert.Throws<ArgumentException>(() => NetAdapterPowerAction.SetCommand(guid, value));

    [Fact]
    public void Parse_skips_garbage_lines() =>
        Assert.Single(NetAdapterPowerAction.Parse($"lixo\r\n{A}|Up|Enabled\r\n{{x}}|Up|Enabled\r\n"));
}
