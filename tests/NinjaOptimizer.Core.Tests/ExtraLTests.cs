using NinjaOptimizer.Core.GameMode;
using NinjaOptimizer.Core.Optimization;

namespace NinjaOptimizer.Core.Tests;

internal sealed class FakeBoosts : IGameBoosts
{
    public (long, long)? Memory { get; set; } = (2000, 500);
    public int Purged { get; private set; }
    public bool? Timer { get; private set; }
    public (long StandbyMb, long FreeMb)? ReadMemory() => Memory;
    public bool PurgeStandby() { Purged++; return true; }
    public bool SetHighResolutionTimer(bool on) { Timer = on; return true; }
}

internal sealed class StepTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class ExtraLTests
{
    [Fact]
    public void Defaults_are_off_with_1024_mb_thresholds()
    {
        var s = new GameModeSettings();
        Assert.False(s.AutoPurgeStandby);
        Assert.False(s.HighResolutionTimer);
        Assert.Equal(1024, s.StandbyThresholdMb);
        Assert.Equal(1024, s.FreeThresholdMb);
    }

    [Theory]
    [InlineData(2000, 500, true)]    // muita espera e pouca livre: limpa
    [InlineData(2000, 3000, false)]  // memória livre de sobra
    [InlineData(500, 500, false)]    // pouca coisa em espera
    public void Purges_only_when_both_thresholds_are_crossed(long standby, long free, bool expected)
    {
        var s = new GameModeSettings { AutoPurgeStandby = true };
        Assert.Equal(expected, new StandbyAutoPurge().ShouldPurge(standby, free, s));
    }

    [Fact]
    public void Disabled_setting_never_purges_and_cooldown_spaces_purges()
    {
        var boosts = new FakeBoosts();
        var time = new StepTime();
        var purge = new StandbyAutoPurge(time);
        Assert.False(purge.Tick(boosts, new GameModeSettings()));

        var s = new GameModeSettings { AutoPurgeStandby = true };
        Assert.True(purge.Tick(boosts, s));
        time.Now += TimeSpan.FromSeconds(5);
        Assert.False(purge.Tick(boosts, s)); // ainda no intervalo mínimo
        time.Now += StandbyAutoPurge.Cooldown;
        Assert.True(purge.Tick(boosts, s));
        Assert.Equal(2, boosts.Purged);

        boosts.Memory = null; // leitura falhou: não faz nada
        time.Now += StandbyAutoPurge.Cooldown;
        Assert.False(purge.Tick(boosts, s));
    }

    [Fact]
    public void Global_timer_tweak_is_windows_11_only_and_outside_profiles()
    {
        var t = TweakCatalog.Get("gaming.global-timer-resolution");
        Assert.Empty(t.Profiles);
        Assert.True(t.RequiresRestart);
        var ctx = new NinjaOptimizer.Core.Abstractions.SystemContext(new FakeRegistry(), new FakeServices(), new FakePowerCfg());
        ctx.Profile = Fakes.FullProfile with { WindowsBuild = 19045 };
        Assert.False(t.CheckConditions(ctx).Applicable);
        ctx.Profile = Fakes.FullProfile;
        Assert.True(t.CheckConditions(ctx).Applicable);
    }
}
