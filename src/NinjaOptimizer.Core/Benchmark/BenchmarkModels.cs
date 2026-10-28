using System.Text.Json;
using System.Text.Json.Serialization;

namespace NinjaOptimizer.Core.Benchmark;

public enum BenchmarkKind { CpuSingle, CpuMulti, MemoryBandwidth, MemoryLatency, DiskSequential, DiskRandom }

/// <summary>Resultado de um teste: valor bruto (mediana das amostras) e pontuação relativa.</summary>
public sealed class BenchmarkResult
{
    public BenchmarkKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public bool HigherIsBetter { get; set; } = true;
    public double Value { get; set; }
    public List<double> Samples { get; set; } = new();
    public int Score { get; set; }

    /// <summary>Detalhe opcional (ex.: "Leitura 3.400 MB/s · escrita 2.900 MB/s").</summary>
    public string? Detail { get; set; }

    /// <summary>Variação entre amostras (max−min)/mediana: mede o "ruído" do próprio PC.</summary>
    [JsonIgnore]
    public double Spread => Samples.Count < 2 || Value == 0 ? 0 : (Samples.Max() - Samples.Min()) / Value;
}

public sealed class BenchmarkRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset At { get; set; }
    public int Overall { get; set; }
    public List<BenchmarkResult> Results { get; set; } = new();
    /// <summary>Otimizações ativas no momento do teste (para saber o "antes" e o "depois").</summary>
    public List<string> ActiveTweaks { get; set; } = new();
}

public enum ChangeVerdict { Better, Worse, WithinNoise }

public sealed record TestComparison(BenchmarkKind Kind, string Name, double PercentChange, ChangeVerdict Verdict);

public static class BenchmarkComparer
{
    /// <summary>Margem mínima por teste: diferenças menores nunca contam como ganho ou perda.</summary>
    public const double MinNoise = 0.05;

    /// <summary>Margem mínima da pontuação geral (a média de vários testes oscila menos).</summary>
    public const double MinOverallNoise = 0.03;

    private const double MaxNoise = 0.25;

    private static string ConfigKey(BenchmarkRun r) => string.Join(",", r.ActiveTweaks.Order());

    private static double RelativeRange(IReadOnlyList<double> v)
    {
        var median = BenchmarkRunner.Median(v);
        return median <= 0 ? 0 : (v.Max() - v.Min()) / median;
    }

    /// <summary>
    /// Variação natural aprendida do próprio PC: entre medições feitas com as MESMAS otimizações ativas,
    /// qualquer diferença é ruído. Usa a maior variação observada por teste.
    /// </summary>
    public static Dictionary<BenchmarkKind, double> LearnedNoise(IEnumerable<BenchmarkRun> history)
    {
        var noise = new Dictionary<BenchmarkKind, double>();
        foreach (var group in history.GroupBy(ConfigKey).Where(g => g.Count() >= 2))
        foreach (var kind in Enum.GetValues<BenchmarkKind>())
        {
            var values = group.SelectMany(r => r.Results.Where(x => x.Kind == kind).Select(x => x.Value)).Where(v => v > 0).ToList();
            if (values.Count < 2) continue;
            noise[kind] = Math.Max(noise.GetValueOrDefault(kind), Math.Min(MaxNoise, RelativeRange(values)));
        }
        return noise;
    }

    /// <summary>Variação natural da pontuação geral, aprendida do mesmo jeito.</summary>
    public static double LearnedOverallNoise(IEnumerable<BenchmarkRun> history) =>
        Math.Min(MaxNoise, history.GroupBy(ConfigKey).Where(g => g.Count() >= 2)
            .Select(g => RelativeRange(g.Select(r => (double)r.Overall).ToList())).DefaultIfEmpty(0).Max());

    /// <summary>Quantas medições repetidas (mesmas otimizações) sustentam a variação aprendida.</summary>
    public static int RepeatedRuns(IEnumerable<BenchmarkRun> history) =>
        history.GroupBy(ConfigKey).Where(g => g.Count() >= 2).Sum(g => g.Count());

    public static ChangeVerdict Verdict(double change, double noise) =>
        Math.Abs(change) < noise ? ChangeVerdict.WithinNoise : change > 0 ? ChangeVerdict.Better : ChangeVerdict.Worse;

    public static IReadOnlyList<TestComparison> Compare(BenchmarkRun baseline, BenchmarkRun current, IEnumerable<BenchmarkRun>? history = null)
    {
        var learned = history is null ? new() : LearnedNoise(history);
        var list = new List<TestComparison>();
        foreach (var now in current.Results)
        {
            var before = baseline.Results.FirstOrDefault(r => r.Kind == now.Kind);
            if (before is null || before.Value <= 0) continue;
            // Positivo = melhorou, independentemente de "maior é melhor" ou "menor é melhor".
            var change = now.HigherIsBetter ? now.Value / before.Value - 1 : before.Value / now.Value - 1;
            var noise = Math.Max(MinNoise, Math.Max(Math.Max(now.Spread, before.Spread) / 2, learned.GetValueOrDefault(now.Kind)));
            list.Add(new TestComparison(now.Kind, now.Name, change * 100, Verdict(change, noise)));
        }
        return list;
    }
}

/// <summary>Histórico de execuções em JSON (benchmarks.json), mais recentes primeiro.</summary>
public sealed class BenchmarkStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private const int MaxRuns = 100;

    public List<BenchmarkRun> Load()
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<List<BenchmarkRun>>(File.ReadAllText(path), Json) ?? new()).OrderByDescending(r => r.At).ToList()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Add(BenchmarkRun run)
    {
        var runs = Load();
        runs.Insert(0, run);
        Save(runs.Take(MaxRuns).ToList());
    }

    public void Delete(Guid id) => Save(Load().Where(r => r.Id != id).ToList());

    private void Save(List<BenchmarkRun> runs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(runs, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
