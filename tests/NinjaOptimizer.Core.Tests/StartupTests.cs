using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Startup;

namespace NinjaOptimizer.Core.Tests;

public sealed class StartupTests
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void Lists_registry_and_folder_items_with_their_state()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegHive.CurrentUser, Run, "Discord", RegValue.Str("\"C:\\Apps\\Discord\\Update.exe\" --processStart Discord.exe"));
        reg.SetValue(RegHive.LocalMachine, Run, "SecurityHealth", RegValue.Str(@"%windir%\system32\SecurityHealthSystray.exe"));
        reg.SetValue(RegHive.CurrentUser, StartupManager.Approved + @"\Run", "Discord",
            new RegValue(RegKind.Binary, new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }));
        var mgr = new StartupManager(reg, () => [new("Steam.lnk", @"C:\x\Steam.lnk", false), new("desktop.ini", @"C:\x\desktop.ini", false)]);

        var list = mgr.List();
        Assert.Equal(["Discord", "SecurityHealth", "Steam.lnk"], list.Select(e => e.Name));
        Assert.False(list[0].Enabled);
        Assert.True(list[1].Enabled);
        Assert.True(list[1].AllUsers);
        Assert.Equal(StartupSource.UserFolder, list[2].Source);
    }

    [Fact]
    public void Disable_and_enable_round_trip_without_touching_the_command()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegHive.CurrentUser, Run, "App", RegValue.Str("app.exe"));
        var mgr = new StartupManager(reg, () => []);

        mgr.SetEnabled(mgr.List()[0], false);
        Assert.False(mgr.List()[0].Enabled);
        mgr.SetEnabled(mgr.List()[0], true);
        Assert.True(mgr.List()[0].Enabled);
        Assert.Equal("app.exe", reg.GetValue(RegHive.CurrentUser, Run, "App")!.Data);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Steam\\steam.exe\" -silent", "steam.exe")]
    [InlineData(@"C:\Windows\system32\SecurityHealthSystray.exe", "SecurityHealthSystray.exe")]
    [InlineData(@"C:\Program Files\App\app.exe /min", "app.exe")]
    [InlineData("rundll32.exe shell32.dll,Foo", "rundll32.exe")]
    public void Executable_name_is_extracted(string cmd, string exe) => Assert.Equal(exe, StartupManager.ExecutableOf(cmd));
}
