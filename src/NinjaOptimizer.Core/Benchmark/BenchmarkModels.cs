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
    /// <summary>Diferenças abaixo de 3% (ou abaixo do ruído medido) não contam como ganho nem perda.</summary>
    public const double MinNoise = 0.03;

    public static IReadOnlyList<TestComparison> Compare(BenchmarkRun baseline, BenchmarkRun current)
    {
        var list = new List<TestComparison>();
        foreach (var now in current.Results)
        {
            var before = baseline.Results.FirstOrDefault(r => r.Kind == now.Kind);
            if (before is null || before.Value <= 0) continue;
            // Positivo = melhorou, independentemente de "maior é melhor" ou "menor é melhor".
            var change = now.HigherIsBetter ? now.Value / before.Value - 1 : before.Value / now.Value - 1;
            var noise = Math.Max(MinNoise, Math.Max(now.Spread, before.Spread) / 2);
            var verdict = Math.Abs(change) < noise ? ChangeVerdict.WithinNoise
                : change > 0 ? ChangeVerdict.Better : ChangeVerdict.Worse;
            list.Add(new TestComparison(now.Kind, now.Name, change * 100, verdict));
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
