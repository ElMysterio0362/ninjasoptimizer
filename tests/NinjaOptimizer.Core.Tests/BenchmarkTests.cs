using NinjaOptimizer.Core.Benchmark;
using Xunit.Abstractions;

namespace NinjaOptimizer.Core.Tests;

public sealed class BenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    private static BenchmarkResult R(BenchmarkKind k, double v, bool higher = true, params double[] samples) =>
        new() { Kind = k, Name = k.ToString(), Value = v, HigherIsBetter = higher, Samples = samples.Length > 0 ? samples.ToList() : [v, v, v] };

    [Fact]
    public async Task Quick_run_produces_every_result_and_deletes_the_temp_file()
    {
        Directory.CreateDirectory(_paths.DataFolder);
        var options = new BenchmarkOptions
        {
            DiskFolder = _paths.DataFolder,
            CpuSampleDuration = TimeSpan.FromMilliseconds(150),
            MemoryBlockBytes = 16L * 1024 * 1024,
            DiskFileBytes = 8L * 1024 * 1024,
            DiskRandomDuration = TimeSpan.FromMilliseconds(100),
            DiskFileOptions = FileOptions.None,
        };
        var steps = new List<string>();
        var run = await new BenchmarkRunner().RunAsync(options, new SyncProgress(p => steps.Add(p.Step)), CancellationToken.None);

        Assert.Equal(Enum.GetValues<BenchmarkKind>().Length, run.Results.Count);
        Assert.All(run.Results, r => Assert.True(r.Value > 0 && r.Score > 0, r.Name));
        Assert.True(run.Overall > 0);
        Assert.Contains("Concluído", steps);
        Assert.Empty(Directory.GetFiles(_paths.DataFolder, "ninjaoptimizer-bench-*"));
        foreach (var r in run.Results) output.WriteLine($"{r.Name}: {r.Value:N1} {r.Unit} → {r.Score} {r.Detail}");
    }

    [Fact]
    public async Task Cancellation_stops_the_run()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BenchmarkRunner().RunAsync(
            new BenchmarkOptions { IncludeDisk = false, CpuSampleDuration = TimeSpan.FromSeconds(5) }, null, cts.Token));
    }

    [Fact]
    public void Score_is_relative_to_the_reference_and_inverts_for_latency()
    {
        Assert.Equal(1000, BenchmarkRunner.ScoreOf(R(BenchmarkKind.CpuSingle, BenchmarkRunner.Reference[BenchmarkKind.CpuSingle])));
        Assert.Equal(2000, BenchmarkRunner.ScoreOf(R(BenchmarkKind.CpuSingle, 2 * BenchmarkRunner.Reference[BenchmarkKind.CpuSingle])));
        // Latência pela metade = o dobro de pontos
        Assert.Equal(2000, BenchmarkRunner.ScoreOf(R(BenchmarkKind.MemoryLatency, BenchmarkRunner.Reference[BenchmarkKind.MemoryLatency] / 2, false)));
    }

    [Fact]
    public void Median_of_samples() => Assert.Equal(3, BenchmarkRunner.Median([5, 1, 3]));

    [Fact]
    public void Compare_flags_real_changes_and_ignores_noise()
    {
        var before = new BenchmarkRun { Results = [R(BenchmarkKind.CpuMulti, 1000), R(BenchmarkKind.MemoryLatency, 80, false), R(BenchmarkKind.DiskRandom, 50)] };
        var after = new BenchmarkRun { Results = [R(BenchmarkKind.CpuMulti, 1100), R(BenchmarkKind.MemoryLatency, 100, false), R(BenchmarkKind.DiskRandom, 51)] };

        var c = BenchmarkComparer.Compare(before, after).ToDictionary(x => x.Kind);
        Assert.Equal(ChangeVerdict.Better, c[BenchmarkKind.CpuMulti].Verdict);
        Assert.Equal(10, c[BenchmarkKind.CpuMulti].PercentChange, 3);
        Assert.Equal(ChangeVerdict.Worse, c[BenchmarkKind.MemoryLatency].Verdict);      // latência subiu = piorou
        Assert.Equal(ChangeVerdict.WithinNoise, c[BenchmarkKind.DiskRandom].Verdict);   // +2% < 3%
    }

    [Fact]
    public void Noisy_samples_widen_the_noise_band()
    {
        var before = new BenchmarkRun { Results = [R(BenchmarkKind.CpuSingle, 1000, true, 800, 1000, 1200)] };   // ±20%
        var after = new BenchmarkRun { Results = [R(BenchmarkKind.CpuSingle, 1080, true, 1080, 1080, 1080)] };
        Assert.Equal(ChangeVerdict.WithinNoise, BenchmarkComparer.Compare(before, after).Single().Verdict);
    }

    [Fact]
    public void Store_keeps_history_newest_first()
    {
        var store = new BenchmarkStore(Path.Combine(_paths.DataFolder, "benchmarks.json"));
        var a = new BenchmarkRun { At = DateTimeOffset.Now.AddHours(-1), Overall = 900 };
        var b = new BenchmarkRun { At = DateTimeOffset.Now, Overall = 950, ActiveTweaks = ["x"] };
        store.Add(a);
        store.Add(b);
        var runs = store.Load();
        Assert.Equal([b.Id, a.Id], runs.Select(r => r.Id));
        Assert.Equal(["x"], runs[0].ActiveTweaks);
        store.Delete(a.Id);
        Assert.Single(store.Load());
    }

    private sealed class SyncProgress(Action<BenchmarkProgress> a) : IProgress<BenchmarkProgress>
    {
        public void Report(BenchmarkProgress value) => a(value);
    }
}

public sealed class BenchmarkNoiseTests
{
    private static BenchmarkRun Run(int overall, double cpu, params string[] tweaks) => new()
    {
        Overall = overall,
        ActiveTweaks = tweaks.ToList(),
        Results = [new BenchmarkResult { Kind = BenchmarkKind.CpuSingle, Value = cpu, Samples = [cpu, cpu, cpu] }],
    };

    [Fact]
    public void Learns_noise_from_repeated_runs_with_the_same_tweaks()
    {
        // Mesmas 2 otimizações, sem mexer em nada: 7.550 → 7.097 (6,2% de variação natural)
        var history = new[] { Run(1002, 7550, "a", "b"), Run(973, 7097, "b", "a"), Run(900, 6000, "a") };
        var noise = BenchmarkComparer.LearnedNoise(history);
        Assert.InRange(noise[BenchmarkKind.CpuSingle], 0.06, 0.065);
        Assert.InRange(BenchmarkComparer.LearnedOverallNoise(history), 0.029, 0.03);
        Assert.Equal(2, BenchmarkComparer.RepeatedRuns(history));
    }

    [Fact]
    public void Same_config_swing_is_not_reported_as_worse()
    {
        var a = Run(1002, 7550, "a");
        var b = Run(973, 7097, "a");
        var c = BenchmarkComparer.Compare(a, b, [a, b]).Single();
        Assert.Equal(ChangeVerdict.WithinNoise, c.Verdict);
    }

    [Fact]
    public void Real_gain_beyond_learned_noise_is_reported()
    {
        var a = Run(1000, 7000, "a");
        var b = Run(990, 7200, "a");      // ruído aprendido ~2,8% (abaixo do mínimo de 5%)
        var c = Run(1100, 8400, "a", "b"); // +20%
        Assert.Equal(ChangeVerdict.Better, BenchmarkComparer.Compare(b, c, [a, b, c]).Single().Verdict);
    }
}
