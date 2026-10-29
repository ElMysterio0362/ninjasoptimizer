using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Hardware;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;
using NinjaOptimizer.Core.Safety;

namespace NinjaOptimizer.Core.Tests;

public sealed class ConditionTests : IDisposable
{
    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    private static SystemProfile With(DiskMediaType disk = DiskMediaType.Ssd, bool? portable = false,
        GpuVendor[]? gpus = null, int? build = 22631) =>
        new(true, disk, portable, gpus is null ? new HashSet<GpuVendor> { GpuVendor.Nvidia } : [.. gpus], build);

    private (OptimizationEngine Engine, SystemContext Ctx, FakeServices Svc) NewEngine(SystemProfile profile)
    {
        var svc = new FakeServices();
        svc.Services["SysMain"] = (ServiceStartMode.Automatic, true);
        var ctx = new SystemContext(new FakeRegistry(), svc, new FakePowerCfg()) { Profile = profile };
        var engine = new OptimizationEngine(ctx, new ChangeJournal(Path.Combine(_paths.DataFolder, Guid.NewGuid() + ".json")),
            new FakeRestorePoint(), new FakeBackup(), new FakeElevation(), _paths);
        return (engine, ctx, svc);
    }

    [Fact]
    public async Task SysMain_is_not_applied_on_a_hard_drive()
    {
        var (engine, _, svc) = NewEngine(With(DiskMediaType.Hdd));
        var report = await engine.ApplyAsync([TweakCatalog.Get("services.sysmain")], new ApplyOptions());
        var r = report.Results.Single();
        Assert.Equal(TweakOutcome.NotApplicable, r.Outcome);
        Assert.Contains("HD", r.Message);
        Assert.Equal(ServiceStartMode.Automatic, svc.Services["SysMain"].Mode);
        Assert.Empty(engine.Journal.Entries);
    }

    [Fact]
    public async Task SysMain_applies_on_ssd()
    {
        var (engine, _, svc) = NewEngine(With(DiskMediaType.Ssd));
        var report = await engine.ApplyAsync([TweakCatalog.Get("services.sysmain")], new ApplyOptions());
        Assert.Equal(TweakOutcome.Applied, report.Results.Single().Outcome);
        Assert.Equal(ServiceStartMode.Disabled, svc.Services["SysMain"].Mode);
    }

    [Theory]
    [InlineData(DiskMediaType.Unknown, "não foi possível confirmar")]
    [InlineData(DiskMediaType.Hdd, "HD")]
    public void Unknown_or_wrong_disk_blocks_with_reason(DiskMediaType disk, string reason)
    {
        var (_, ctx, _) = NewEngine(With(disk));
        var r = TweakCondition.SystemDriveIsSsd.Check(ctx);
        Assert.False(r.Applicable);
        Assert.Contains(reason, r.Reason);
    }

    [Fact]
    public void Profile_not_loaded_blocks_everything_with_conditions()
    {
        var (_, ctx, _) = NewEngine(SystemProfile.NotLoaded);
        Assert.False(TweakCondition.SystemDriveIsSsd.Check(ctx).Applicable);
        Assert.False(TweakCondition.IsDesktop.Check(ctx).Applicable);
        Assert.False(TweakCondition.HasGpuVendor(GpuVendor.Amd).Check(ctx).Applicable);
        Assert.False(TweakCondition.MinWindowsBuild(1).Check(ctx).Applicable);
    }

    [Fact]
    public async Task Profile_with_a_non_applicable_item_skips_it_and_applies_the_rest()
    {
        var (engine, _, _) = NewEngine(With(DiskMediaType.Hdd));
        var sysmain = TweakCatalog.Get("services.sysmain");
        var other = TweakCatalog.Get("privacy.ads-tips");
        var report = await engine.ApplyAsync([sysmain, other], new ApplyOptions());
        Assert.Equal(TweakOutcome.NotApplicable, report.Results.Single(r => r.Tweak == sysmain).Outcome);
        Assert.Equal(TweakOutcome.Applied, report.Results.Single(r => r.Tweak == other).Outcome);
    }

    [Fact]
    public void Chassis_gpu_service_and_build_conditions()
    {
        var (_, desktop, svc) = NewEngine(With(portable: false, gpus: [GpuVendor.Amd], build: 19045));
        var (_, laptop, _) = NewEngine(With(portable: true));
        var (_, unknown, _) = NewEngine(With(portable: null, gpus: null) with { GpuVendors = null });

        Assert.True(TweakCondition.IsDesktop.Check(desktop).Applicable);
        Assert.False(TweakCondition.IsPortable.Check(desktop).Applicable);
        Assert.True(TweakCondition.IsPortable.Check(laptop).Applicable);
        Assert.False(TweakCondition.IsDesktop.Check(laptop).Applicable);
        Assert.False(TweakCondition.IsDesktop.Check(unknown).Applicable);

        Assert.True(TweakCondition.HasGpuVendor(GpuVendor.Amd).Check(desktop).Applicable);
        Assert.False(TweakCondition.HasGpuVendor(GpuVendor.Nvidia).Check(desktop).Applicable);
        Assert.Contains("não foi possível", TweakCondition.HasGpuVendor(GpuVendor.Nvidia).Check(unknown).Reason);

        Assert.True(TweakCondition.ServiceExists("SysMain").Check(desktop).Applicable);
        Assert.False(TweakCondition.ServiceExists("NaoExiste").Check(desktop).Applicable);

        Assert.True(TweakCondition.MinWindowsBuild(19041).Check(desktop).Applicable);
        Assert.False(TweakCondition.MinWindowsBuild(22000).Check(desktop).Applicable);
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2684&SUBSYS_0", GpuVendor.Nvidia)]
    [InlineData(@"PCI\VEN_1002&DEV_73BF", GpuVendor.Amd)]
    [InlineData(@"PCI\VEN_8086&DEV_A780", GpuVendor.Intel)]
    public void Vendor_from_pnp_id(string pnp, GpuVendor vendor) => Assert.Equal(vendor, SystemProfile.VendorFromPnpId(pnp));

    [Fact]
    public void Vendor_unknown_and_chassis_detection()
    {
        Assert.Null(SystemProfile.VendorFromPnpId(@"PCI\VEN_1234&DEV_1111"));
        Assert.Null(SystemProfile.VendorFromPnpId(@"ROOT\DISPLAY\0000"));
        Assert.True(SystemProfile.IsPortableChassis([10], false));
        Assert.True(SystemProfile.IsPortableChassis([3], true));
        Assert.False(SystemProfile.IsPortableChassis([3], false));
    }

    [Fact]
    public void Every_catalog_condition_passes_on_a_pc_that_meets_everything()
    {
        var svc = new FakeServices();
        foreach (var n in TweakCatalog.All.SelectMany(t => t.Actions.OfType<ServiceStartupAction>()).Select(a => a.ServiceName)) svc.Services[n] = (ServiceStartMode.Manual, false);
        var ctx = new SystemContext(new FakeRegistry(), svc, new FakePowerCfg()) { Profile = Fakes.FullProfile };
        Assert.All(TweakCatalog.All.Where(t => !t.Conditions.Contains(TweakCondition.IsPortable) && !t.Conditions.Contains(TweakCondition.IsDesktop)),
            t => Assert.True(t.CheckConditions(ctx).Applicable, t.Id));
    }
}
