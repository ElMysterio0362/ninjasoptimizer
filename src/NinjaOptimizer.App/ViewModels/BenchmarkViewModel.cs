using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.Core.Benchmark;

namespace NinjaOptimizer.App.ViewModels;

public sealed record ResultCard(string Icon, string Name, string ValueText, string Unit, string? Detail,
    string ScoreText, string ChangeText, StatusKind ChangeKind);

public sealed record BenchmarkHistoryRow(BenchmarkRun Run, string When, string OverallText, string TweaksText,
    bool IsShown, bool IsBaseline);

public sealed partial class BenchmarkViewModel : ObservableObject
{
    private readonly BenchmarkStore _store;
    private readonly OptimizeViewModel _optimize;
    private readonly DialogService _dialogs;
    private readonly AppLog _log;
    private readonly BenchmarkOptions _options;
    private CancellationTokenSource? _cts;
    private List<BenchmarkRun> _runs;
    private bool _baselineChosen; // o usuário escolheu a base ("antes") manualmente

    public ObservableCollection<ResultCard> Cards { get; } = new();
    public ObservableCollection<BenchmarkHistoryRow> History { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _step = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private BenchmarkRun? _shown;
    [ObservableProperty] private BenchmarkRun? _baseline;
    [ObservableProperty] private string _overallText = "—";
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _baselineText = "";
    [ObservableProperty] private string _overallChangeText = "";
    [ObservableProperty] private StatusKind _overallChangeKind;

    public bool HasResult => Shown is not null;
    public bool IsEmpty => Shown is null && !IsRunning;

    public BenchmarkViewModel(BenchmarkStore store, OptimizeViewModel optimize, DialogService dialogs, AppLog log, BenchmarkOptions options)
    {
        _store = store;
        _optimize = optimize;
        _dialogs = dialogs;
        _log = log;
        _options = options;
        _runs = store.Load();
        Shown = _runs.FirstOrDefault();
        Baseline = Shown is null ? null : _runs.Skip(1).FirstOrDefault();
        Refresh();
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;
        if (_optimize.IsBusy)
        {
            _dialogs.Info("Espere a otimização terminar", "O benchmark mede melhor com o PC parado.");
            return;
        }

        IsRunning = true;
        Progress = 0;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<BenchmarkProgress>(p => { Step = p.Step; Progress = p.Fraction * 100; });
            var run = await new BenchmarkRunner().RunAsync(_options, progress, _cts.Token);
            run.ActiveTweaks = _optimize.AllItems.Where(i => i.IsApplied).Select(i => i.Tweak.Id).ToList();
            _store.Add(run);
            _log.Info($"Benchmark: {run.Overall} pontos");

            // Sem base escolhida, compara com a medição anterior.
            if (!_baselineChosen) Baseline = _runs.FirstOrDefault();
            Shown = run;
            _runs = _store.Load();
        }
        catch (OperationCanceledException)
        {
            Step = "";
        }
        catch (Exception ex)
        {
            _log.Error("Benchmark", ex);
            _dialogs.Info("Não conseguimos concluir o benchmark", ex.Message, kind: DialogKind.Danger);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            Refresh();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Show(BenchmarkHistoryRow? row)
    {
        if (row is null) return;
        Shown = row.Run;
        if (!_baselineChosen || Baseline?.Id == row.Run.Id)
        {
            _baselineChosen = false;
            Baseline = _runs.SkipWhile(r => r.Id != row.Run.Id).Skip(1).FirstOrDefault();
        }
        Refresh();
    }

    [RelayCommand]
    private void SetBaseline(BenchmarkHistoryRow? row)
    {
        if (row is null || row.Run.Id == Shown?.Id) return;
        Baseline = row.Run;
        _baselineChosen = true;
        Refresh();
    }

    [RelayCommand]
    private void Delete(BenchmarkHistoryRow? row)
    {
        if (row is null) return;
        if (!_dialogs.Confirm("Excluir esta medição?", $"{row.When} · {row.OverallText}. As demais continuam no histórico.",
                kind: DialogKind.Danger, confirmText: "Excluir"))
            return;
        _store.Delete(row.Run.Id);
        _runs = _store.Load();
        if (Shown?.Id == row.Run.Id) Shown = _runs.FirstOrDefault();
        if (Baseline?.Id == row.Run.Id || Baseline?.Id == Shown?.Id || !_baselineChosen)
        {
            _baselineChosen = false;
            Baseline = Shown is null ? null : _runs.SkipWhile(r => r.Id != Shown.Id).Skip(1).FirstOrDefault();
        }
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(IsEmpty));

        History.Clear();
        foreach (var r in _runs)
            History.Add(new BenchmarkHistoryRow(r, Format.When(r.At), $"{r.Overall:N0} pontos",
                $"{r.ActiveTweaks.Count} otimização(ões) ativa(s)", r.Id == Shown?.Id, r.Id == Baseline?.Id));

        Cards.Clear();
        if (Shown is null)
        {
            OverallText = "—";
            Headline = "Meça o desempenho do seu PC";
            BaselineText = "";
            OverallChangeText = "";
            return;
        }

        var comparisons = Baseline is null ? null : BenchmarkComparer.Compare(Baseline, Shown).ToDictionary(c => c.Kind);
        foreach (var r in Shown.Results)
        {
            var (text, kind) = comparisons is null ? ("Primeira medição", StatusKind.Neutral)
                : comparisons.TryGetValue(r.Kind, out var c) ? ChangeLabel(c.Verdict, c.PercentChange)
                : ("Sem comparação", StatusKind.Neutral);
            Cards.Add(new ResultCard(IconOf(r.Kind), r.Name, FormatValue(r), r.Unit, r.Detail, $"{r.Score:N0} pontos", text, kind));
        }

        OverallText = $"{Shown.Overall:N0}";
        if (Baseline is null)
        {
            Headline = $"{Shown.Overall:N0} pontos";
            BaselineText = $"Medido {Format.When(Shown.At)} com {Shown.ActiveTweaks.Count} otimização(ões) ativa(s). " +
                           "Aplique otimizações e rode de novo para comparar.";
            OverallChangeText = "";
            OverallChangeKind = StatusKind.Neutral;
            return;
        }

        var change = (double)Shown.Overall / Math.Max(1, Baseline.Overall) - 1;
        var verdict = Math.Abs(change) < BenchmarkComparer.MinNoise ? ChangeVerdict.WithinNoise
            : change > 0 ? ChangeVerdict.Better : ChangeVerdict.Worse;
        (OverallChangeText, OverallChangeKind) = ChangeLabel(verdict, change * 100);
        Headline = verdict switch
        {
            ChangeVerdict.Better => $"Pronto. O PC subiu de {Baseline.Overall:N0} para {Shown.Overall:N0}.",
            ChangeVerdict.Worse => $"O PC caiu de {Baseline.Overall:N0} para {Shown.Overall:N0}.",
            _ => $"{Shown.Overall:N0} pontos, sem diferença além da variação normal.",
        };
        var added = Shown.ActiveTweaks.Except(Baseline.ActiveTweaks).Count();
        var removed = Baseline.ActiveTweaks.Except(Shown.ActiveTweaks).Count();
        var diff = (added, removed) switch
        {
            (0, 0) => "mesmas otimizações ativas",
            (> 0, 0) => $"{added} otimização(ões) a mais",
            (0, > 0) => $"{removed} otimização(ões) a menos",
            _ => $"{added} a mais e {removed} a menos",
        };
        BaselineText = $"Comparado com a medição de {Format.When(Baseline.At)} ({Baseline.Overall:N0} pontos): {diff}. " +
                       $"Diferenças abaixo de {BenchmarkComparer.MinNoise:P0} são variação normal do PC.";
    }

    private static (string, StatusKind) ChangeLabel(ChangeVerdict v, double pct) => v switch
    {
        ChangeVerdict.Better => ($"+{pct:0.0}%", StatusKind.Ok),
        ChangeVerdict.Worse => ($"{pct:0.0}%".Replace("-", "−"), StatusKind.Warning),
        _ => ("Sem mudança", StatusKind.Neutral),
    };

    private static string IconOf(BenchmarkKind k) => k switch
    {
        BenchmarkKind.CpuSingle or BenchmarkKind.CpuMulti => "Icon.Cpu",
        BenchmarkKind.MemoryBandwidth or BenchmarkKind.MemoryLatency => "Icon.Memory",
        _ => "Icon.Disk",
    };

    private static string FormatValue(BenchmarkResult r) => r.Kind switch
    {
        BenchmarkKind.MemoryBandwidth or BenchmarkKind.MemoryLatency => r.Value.ToString("0.0"),
        BenchmarkKind.DiskRandom => r.Value.ToString("N1"),
        _ => r.Value.ToString("N0"),
    };
}
