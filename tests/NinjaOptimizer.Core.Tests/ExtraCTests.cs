using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Safety;

namespace NinjaOptimizer.Core.Tests;

public sealed class ExtraCTests : IDisposable
{
    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    [Fact]
    public async Task Visual_effects_restore_the_original_binary_mask_exactly()
    {
        var reg = new FakeRegistry();
        var original = new byte[] { 0x9E, 0x1E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00 };
        reg.SetValue(RegHive.CurrentUser, @"Control Panel\Desktop", "UserPreferencesMask", new RegValue(RegKind.Binary, original));
        var ctx = new SystemContext(reg, new FakeServices(), new FakePowerCfg()) { Profile = Fakes.FullProfile };
        var engine = new OptimizationEngine(ctx, new ChangeJournal(Path.Combine(_paths.DataFolder, "j.json")),
            new FakeRestorePoint(), new FakeBackup(), new FakeElevation(), _paths);
        var tweak = TweakCatalog.Get("system.visual-best-performance");

        await engine.ApplyAsync([tweak], new ApplyOptions());
        Assert.Equal(new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 },
            (byte[])reg.GetValue(RegHive.CurrentUser, @"Control Panel\Desktop", "UserPreferencesMask")!.Data);

        // O journal é relido do disco (como numa nova abertura do app) antes de reverter
        var reloaded = new OptimizationEngine(ctx, new ChangeJournal(Path.Combine(_paths.DataFolder, "j.json")),
            new FakeRestorePoint(), new FakeBackup(), new FakeElevation(), _paths);
        await reloaded.RevertAllAsync();

        Assert.Equal(original, (byte[])reg.GetValue(RegHive.CurrentUser, @"Control Panel\Desktop", "UserPreferencesMask")!.Data);
        Assert.Null(reg.GetValue(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting"));
    }
}
