using NinjaOptimizer.Core.GameMode;

namespace NinjaOptimizer.Core.Tests;

internal sealed class FakeProcesses : IProcessControl
{
    public List<ProcessInfo> Items { get; } = [];
    public HashSet<int> Suspended { get; } = [];
    public int CurrentSessionId => 1;
    public int? Foreground { get; set; }
    public IReadOnlyList<ProcessInfo> List() => [.. Items];
    public int? ForegroundProcessId() => Foreground;
    public bool Suspend(int pid) => Items.Any(p => p.Id == pid) && Suspended.Add(pid);
    public bool Resume(int pid) => Suspended.Remove(pid);
}

public sealed class GameModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ninja-gm-" + Guid.NewGuid().ToString("N"));
    private string State => Path.Combine(_dir, "gamemode.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static FakeProcesses World()
    {
        var f = new FakeProcesses();
        f.Items.AddRange([
            new(10, "OneDrive.exe", @"C:\Users\x\OneDrive.exe", 100, 1, 1),
            new(11, "explorer.exe", @"C:\Windows\explorer.exe", 100, 1, 1),
            new(12, "OneDrive.exe", null, 100, 0, 1),          // outra sessão (serviço)
            new(13, "cs2.exe", @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", 4000, 1, 1),
            new(14, "Dropbox.exe", @"C:\Dropbox.exe", 50, 1, 7),
        ]);
        return f;
    }

    [Fact]
    public void Pauses_only_listed_unprotected_processes_of_this_session()
    {
        var f = World();
        var gm = new GameModeService(f, State);
        var paused = gm.Activate(["OneDrive", "explorer", "Dropbox.exe", "cs2"], "cs2", gamePid: 13);
        Assert.Equal([10, 14], paused.Select(p => p.Id).Order());
        Assert.True(gm.IsActive);

        Assert.Equal(2, gm.Deactivate());
        Assert.Empty(f.Suspended);
        Assert.False(File.Exists(State));
    }

    [Fact]
    public void Recovers_paused_processes_after_a_crash_and_skips_reused_pids()
    {
        var f = World();
        new GameModeService(f, State).Activate(["OneDrive", "Dropbox"]); // app "travou" aqui
        Assert.True(File.Exists(State));
        // PID 14 morreu e foi reaproveitado por outro processo
        f.Items.RemoveAll(p => p.Id == 14);
        f.Items.Add(new(14, "Other.exe", null, 1, 1, StartTicks: 99));

        var fresh = new GameModeService(f, State);
        Assert.Equal(1, fresh.RecoverFromPreviousRun());
        Assert.DoesNotContain(10, f.Suspended);
        Assert.Contains(14, f.Suspended); // o fake ainda lembra; o real nunca foi tocado
        Assert.False(File.Exists(State));
    }

    [Fact]
    public void Detects_games_by_folder_or_user_list_and_prefers_foreground()
    {
        var f = World();
        f.Items.Add(new(20, "MyIndie.exe", @"C:\Jogos\MyIndie.exe", 10, 1, 1));
        f.Items.Add(new(21, "UnityCrashHandler64.exe", @"D:\Steam\steamapps\common\X\UnityCrashHandler64.exe", 9000, 1, 1));
        var gm = new GameModeService(f, State);

        Assert.Equal(13, gm.FindRunningGame([])!.Id);
        f.Foreground = 20;
        Assert.Equal(20, gm.FindRunningGame(["MyIndie"])!.Id);
        f.Items.RemoveAll(p => p.Id == 13);
        Assert.Null(gm.FindRunningGame([]));
    }

    [Fact]
    public void Default_pause_list_never_contains_protected_processes() =>
        Assert.DoesNotContain(ProcessRules.DefaultPauseList, ProcessRules.IsProtected);
}
