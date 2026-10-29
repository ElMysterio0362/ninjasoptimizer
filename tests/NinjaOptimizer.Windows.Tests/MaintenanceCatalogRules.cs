using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Maintenance;
using NinjaOptimizer.Core.Safety;
using NinjaOptimizer.Windows.Maintenance;

namespace NinjaOptimizer.Windows.Tests;

/// <summary>Constrói o catálogo de manutenção real (sem executar nada) e confere as regras.</summary>
public sealed class MaintenanceCatalogRules : IDisposable
{
    private sealed class NoCommands : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string f, string a, CancellationToken ct = default) => throw new InvalidOperationException("não deve executar");
    }
    private sealed class NoServices : IServiceManager
    {
        public bool Exists(string n) => false;
        public ServiceStartMode GetStartMode(string n) => throw new InvalidOperationException();
        public void SetStartMode(string n, ServiceStartMode m) => throw new InvalidOperationException();
        public bool IsRunning(string n) => false;
        public void Stop(string n) => throw new InvalidOperationException();
        public void Start(string n) => throw new InvalidOperationException();
    }
    private sealed class Elevation : IElevationService { public bool IsElevated => false; }
    private sealed class Paths(string dir) : IAppPaths
    {
        public string DataFolder => dir;
        public string BackupFolder => Path.Combine(dir, "b");
        public string LogFolder => Path.Combine(dir, "l");
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ninja-wt-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private IReadOnlyList<MaintenanceTask> Build() => WindowsMaintenanceCatalog.Build(new NoCommands(), new NoServices(), new Elevation(),
        new ChangeJournal(Path.Combine(_dir, "j.json")), new Paths(_dir));

    [Fact]
    public void Every_task_with_side_effects_lists_its_consequences()
    {
        var tasks = Build();
        Assert.NotEmpty(tasks);
        Assert.All(tasks.Where(t => t.HasSideEffects), t =>
        {
            Assert.True(t.Consequences.Count > 0, $"{t.Id} sem consequências");
            Assert.All(t.Consequences, c => Assert.False(string.IsNullOrWhiteSpace(c), t.Id));
        });
    }

    [Fact]
    public void Every_advanced_option_lists_its_consequences_and_starts_unchecked()
    {
        Assert.All(Build().SelectMany(t => t.Options.Select(o => (t, o))), x =>
        {
            Assert.NotEmpty(x.o.Consequences);
            Assert.False(x.t.IsSelected(x.o.Id), $"{x.t.Id}/{x.o.Id} veio marcada");
        });
    }

    [Fact]
    public void Ids_are_unique_and_texts_have_no_exclamation()
    {
        var tasks = Build();
        Assert.Equal(tasks.Count, tasks.Select(t => t.Id).Distinct().Count());
        Assert.All(tasks, t => Assert.DoesNotContain("!", t.Name + t.Description + string.Concat(t.Consequences)));
    }
}
