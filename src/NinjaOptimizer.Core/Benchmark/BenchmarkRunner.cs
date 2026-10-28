using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NinjaOptimizer.Core.Benchmark;

public sealed record BenchmarkProgress(string Step, double Fraction);

public sealed class BenchmarkOptions
{
    /// <summary>Pasta do arquivo temporário do teste de disco (padrão: pasta temporária do usuário).</summary>
    public string DiskFolder { get; init; } = Path.GetTempPath();

    /// <summary>
    /// Opções extras do arquivo de disco. No Windows, o app passa FILE_FLAG_NO_BUFFERING para medir o disco
    /// e não o cache de memória.
    /// </summary>
    public FileOptions DiskFileOptions { get; init; } = FileOptions.WriteThrough;

    public TimeSpan CpuSampleDuration { get; init; } = TimeSpan.FromSeconds(2);
    public int Samples { get; init; } = 3;
    public long MemoryBlockBytes { get; init; } = 128L * 1024 * 1024;
    public long DiskFileBytes { get; init; } = 256L * 1024 * 1024;
    public TimeSpan DiskRandomDuration { get; init; } = TimeSpan.FromSeconds(1);
    public bool IncludeDisk { get; init; } = true;
}

/// <summary>
/// Benchmark sintético de CPU, memória e disco. Cada teste roda várias vezes e usa a mediana.
/// A pontuação é relativa a um PC de referência fixo (1000 pontos), para comparar execuções.
/// </summary>
public sealed class BenchmarkRunner
{
    // PC de referência = 1000 pontos: CPU de 6 núcleos/12 threads (classe Ryzen 5 3600), DDR4-3200 em dual channel,
    // SSD NVMe PCIe 3.0. Valores fixos: mudar invalida a comparação com o histórico.
    internal static readonly Dictionary<BenchmarkKind, double> Reference = new()
    {
        [BenchmarkKind.CpuSingle] = 8000,       // rodadas/s
        [BenchmarkKind.CpuMulti] = 50000,       // rodadas/s
        [BenchmarkKind.MemoryBandwidth] = 25,   // GB/s (leitura + escrita)
        [BenchmarkKind.MemoryLatency] = 90,     // ns (menor é melhor)
        [BenchmarkKind.DiskSequential] = 2000,  // MB/s (média geométrica de leitura e escrita)
        [BenchmarkKind.DiskRandom] = 60,        // MB/s em blocos de 4 KB, uma operação por vez
    };

    public async Task<BenchmarkRun> RunAsync(BenchmarkOptions options, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        var steps = options.IncludeDisk ? 6 : 4;
        var done = 0;
        void Report(string step) => progress?.Report(new BenchmarkProgress(step, (double)done / steps));

        var results = new List<BenchmarkResult>();

        Report("CPU · um núcleo");
        results.Add(await Task.Run(() => Sampled(BenchmarkKind.CpuSingle, "CPU · um núcleo", "rodadas/s", true, options.Samples,
            () => CpuThroughput(1, options.CpuSampleDuration, ct)), ct));
        done++;

        Report("CPU · todos os núcleos");
        results.Add(await Task.Run(() => Sampled(BenchmarkKind.CpuMulti, "CPU · todos os núcleos", "rodadas/s", true, options.Samples,
            () => CpuThroughput(Environment.ProcessorCount, options.CpuSampleDuration, ct)), ct));
        done++;

        Report("Memória · velocidade");
        results.Add(await Task.Run(() => Sampled(BenchmarkKind.MemoryBandwidth, "Memória · velocidade", "GB/s", true, options.Samples,
            () => MemoryBandwidth(options.MemoryBlockBytes, ct)), ct));
        done++;

        Report("Memória · latência");
        results.Add(await Task.Run(() => MemoryLatencyTest(options, ct), ct));
        done++;

        if (options.IncludeDisk)
        {
            Report("Disco · sequencial");
            var path = Path.Combine(options.DiskFolder, $"ninjaoptimizer-bench-{Guid.NewGuid():N}.tmp");
            try
            {
                results.Add(await Task.Run(() => DiskSequential(path, options, ct), ct));
                done++;
                Report("Disco · aleatório 4K");
                results.Add(await Task.Run(() => DiskRandom(path, options, ct), ct));
                done++;
            }
            finally
            {
                try { File.Delete(path); } catch { /* melhor esforço */ }
            }
        }

        foreach (var r in results) r.Score = ScoreOf(r);
        progress?.Report(new BenchmarkProgress("Concluído", 1));
        return new BenchmarkRun
        {
            At = DateTimeOffset.Now,
            Results = results,
            Overall = (int)Math.Round(Math.Exp(results.Average(r => Math.Log(Math.Max(1, r.Score))))),
        };
    }

    internal static int ScoreOf(BenchmarkResult r)
    {
        var reference = Reference[r.Kind];
        var ratio = r.HigherIsBetter ? r.Value / reference : reference / r.Value;
        return (int)Math.Round(1000 * ratio);
    }

    private static BenchmarkResult Sampled(BenchmarkKind kind, string name, string unit, bool higherIsBetter, int samples, Func<double> sample)
    {
        var values = Enumerable.Range(0, samples).Select(_ => sample()).ToList();
        return new BenchmarkResult { Kind = kind, Name = name, Unit = unit, HigherIsBetter = higherIsBetter, Samples = values, Value = Median(values) };
    }

    internal static double Median(IReadOnlyList<double> v)
    {
        var s = v.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    // ───────────────────────── CPU ─────────────────────────

    /// <summary>Uma "rodada": SHA-256 de 16 KB, crivo de primos até 8.000 e um trecho de Mandelbrot (ponto flutuante).</summary>
    internal static int CpuRound(byte[] buffer, bool[] sieve)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);

        Array.Clear(sieve);
        var primes = 0;
        for (var i = 2; i < sieve.Length; i++)
        {
            if (sieve[i]) continue;
            primes++;
            for (var j = i * i; j < sieve.Length; j += i) sieve[j] = true;
        }

        var inside = 0;
        for (var y = 0; y < 24; y++)
        for (var x = 0; x < 24; x++)
        {
            double cr = -2 + x * 2.5 / 24, ci = -1.25 + y * 2.5 / 24, zr = 0, zi = 0;
            var k = 0;
            while (k < 48 && zr * zr + zi * zi < 4) { (zr, zi) = (zr * zr - zi * zi + cr, 2 * zr * zi + ci); k++; }
            if (k == 48) inside++;
        }
        return hash[0] + primes + inside; // evita que o compilador descarte o trabalho
    }

    private static double CpuThroughput(int threads, TimeSpan duration, CancellationToken ct)
    {
        var counts = new long[threads];
        var start = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var buffer = new byte[16 * 1024];
            new Random(t).NextBytes(buffer);
            var sieve = new bool[8000];
            start.Wait();
            var sw = Stopwatch.StartNew();
            long n = 0, sink = 0;
            while (sw.Elapsed < duration && !ct.IsCancellationRequested) { sink += CpuRound(buffer, sieve); n++; }
            counts[t] = n + (sink == long.MinValue ? 1 : 0);
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal }).ToList();

        workers.ForEach(w => w.Start());
        start.Set();
        workers.ForEach(w => w.Join());
        ct.ThrowIfCancellationRequested();
        return counts.Sum() / duration.TotalSeconds;
    }

    // ───────────────────────── Memória ─────────────────────────

    private static double MemoryBandwidth(long blockBytes, CancellationToken ct)
    {
        var src = GC.AllocateUninitializedArray<byte>((int)blockBytes);
        var dst = GC.AllocateUninitializedArray<byte>((int)blockBytes);
        src.AsSpan().Fill(1);
        src.AsSpan().CopyTo(dst); // aquece as páginas

        var sw = Stopwatch.StartNew();
        long copied = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(1) && !ct.IsCancellationRequested)
        {
            src.AsSpan().CopyTo(dst);
            copied += blockBytes;
        }
        ct.ThrowIfCancellationRequested();
        // Cópia = leitura + escrita: conta os dois sentidos, como os testes de memória costumam reportar.
        return 2.0 * copied / sw.Elapsed.TotalSeconds / 1e9;
    }

    private static BenchmarkResult MemoryLatencyTest(BenchmarkOptions options, CancellationToken ct)
    {
        // Ciclo aleatório único (algoritmo de Sattolo): cada acesso depende do anterior e não pode ser previsto.
        var n = (int)(options.MemoryBlockBytes / sizeof(int));
        var next = new int[n];
        for (var i = 0; i < n; i++) next[i] = i;
        var rng = new Random(42);
        for (var i = n - 1; i > 0; i--)
        {
            var j = rng.Next(i);
            (next[i], next[j]) = (next[j], next[i]);
        }

        const int steps = 4_000_000;
        var samples = new List<double>();
        var p = 0;
        for (var i = 0; i < 200_000; i++) p = next[p]; // aquecimento
        for (var s = 0; s < options.Samples; s++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < steps; i++) p = next[p];
            samples.Add(sw.Elapsed.TotalNanoseconds / steps + (p == -1 ? 1 : 0));
        }
        return new BenchmarkResult
        {
            Kind = BenchmarkKind.MemoryLatency, Name = "Memória · latência", Unit = "ns", HigherIsBetter = false,
            Samples = samples, Value = Median(samples),
        };
    }

    // ───────────────────────── Disco ─────────────────────────

    /// <summary>Buffer de memória nativa alinhado a 4 KB (exigido pela leitura sem cache do Windows).</summary>
    private sealed unsafe class AlignedBuffer : MemoryManager<byte>
    {
        private readonly byte* _ptr;
        private readonly int _length;

        public AlignedBuffer(int length)
        {
            _length = length;
            _ptr = (byte*)NativeMemory.AlignedAlloc((nuint)length, 4096);
            new Span<byte>(_ptr, length).Fill(0xA5);
        }

        public override Span<byte> GetSpan() => new(_ptr, _length);
        public override MemoryHandle Pin(int elementIndex = 0) => new(_ptr + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => NativeMemory.AlignedFree(_ptr);
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path, FileMode mode, BenchmarkOptions o) =>
        File.OpenHandle(path, mode, FileAccess.ReadWrite, FileShare.None, o.DiskFileOptions);

    private static BenchmarkResult DiskSequential(string path, BenchmarkOptions o, CancellationToken ct)
    {
        const int block = 1024 * 1024;
        using var buffer = new AlignedBuffer(block);
        var blocks = o.DiskFileBytes / block;
        var writes = new List<double>();
        var reads = new List<double>();

        for (var s = 0; s < Math.Min(2, o.Samples); s++)
        {
            using (var h = Open(path, FileMode.OpenOrCreate, o))
            {
                var sw = Stopwatch.StartNew();
                for (long i = 0; i < blocks; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, buffer.GetSpan(), i * block);
                }
                writes.Add(o.DiskFileBytes / sw.Elapsed.TotalSeconds / 1e6);
            }
            using (var h = Open(path, FileMode.Open, o))
            {
                var sw = Stopwatch.StartNew();
                for (long i = 0; i < blocks; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, buffer.GetSpan(), i * block);
                }
                reads.Add(o.DiskFileBytes / sw.Elapsed.TotalSeconds / 1e6);
            }
        }

        double read = Median(reads), write = Median(writes);
        var combined = reads.Zip(writes, (r, w) => Math.Sqrt(r * w)).ToList();
        return new BenchmarkResult
        {
            Kind = BenchmarkKind.DiskSequential, Name = "Disco · sequencial", Unit = "MB/s", HigherIsBetter = true,
            Samples = combined, Value = Math.Sqrt(read * write),
            Detail = $"Leitura {read:N0} MB/s · escrita {write:N0} MB/s",
        };
    }

    private static BenchmarkResult DiskRandom(string path, BenchmarkOptions o, CancellationToken ct)
    {
        const int block = 4096;
        using var buffer = new AlignedBuffer(block);
        var blocks = o.DiskFileBytes / block;
        var rng = new Random(7);
        var reads = new List<double>();
        var writes = new List<double>();

        using var h = Open(path, FileMode.Open, o);
        double Measure(bool write)
        {
            var sw = Stopwatch.StartNew();
            long ops = 0;
            while (sw.Elapsed < o.DiskRandomDuration)
            {
                ct.ThrowIfCancellationRequested();
                var offset = rng.NextInt64(blocks) * block;
                if (write) RandomAccess.Write(h, buffer.GetSpan(), offset);
                else RandomAccess.Read(h, buffer.GetSpan(), offset);
                ops++;
            }
            return ops * block / sw.Elapsed.TotalSeconds / 1e6;
        }

        for (var s = 0; s < o.Samples; s++)
        {
            reads.Add(Measure(false));
            writes.Add(Measure(true));
        }

        double read = Median(reads), write = Median(writes);
        return new BenchmarkResult
        {
            Kind = BenchmarkKind.DiskRandom, Name = "Disco · aleatório 4K", Unit = "MB/s", HigherIsBetter = true,
            Samples = reads.Zip(writes, (r, w) => Math.Sqrt(r * w)).ToList(), Value = Math.Sqrt(read * write),
            Detail = $"Leitura {read:N1} MB/s · escrita {write:N1} MB/s",
        };
    }
}
