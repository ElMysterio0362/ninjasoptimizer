using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Safety;
using NinjaOptimizer.Core.Settings;
using NinjaOptimizer.Windows.Platform;

namespace NinjaOptimizer.App.ViewModels;

public sealed partial class TweakItemViewModel(Tweak tweak) : ObservableObject
{
    public Tweak Tweak { get; } = tweak;
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string Benefit => Tweak.Benefit;
    public string Icon => OptimizeViewModel.CategoryIcon(Tweak.Category);
    public string CategoryTitle => LabelsPt.Of(Tweak.Category);

    public string ImpactText => LabelsPt.Of(Tweak.Impact);
    public StatusKind ImpactKind => Tweak.Impact switch
    {
        ImpactLevel.High => StatusKind.Accent,
        ImpactLevel.Medium => StatusKind.Neutral,
        _ => StatusKind.Neutral,
    };

    public RiskLevel Risk => Tweak.Risk;
    public string RiskText => "Risco " + LabelsPt.Of(Tweak.Risk).ToLowerInvariant();
    public StatusKind RiskKind => Tweak.Risk switch
    {
        RiskLevel.Low => StatusKind.Ok,
        RiskLevel.Medium => StatusKind.Warning,
        _ => StatusKind.Danger,
    };

    public bool Reversible => Tweak.Reversible;
    public bool RequiresAdmin => Tweak.RequiresAdmin;
    public bool RequiresRestart => Tweak.RequiresRestart;

    /// <summary>Detalhes técnicos (tooltip "O que muda").</summary>
    public string ActionsText =>
        "O que muda:" + Environment.NewLine +
        string.Join(Environment.NewLine, Tweak.Actions.Select(a => "• " + a.Describe())) +
        Environment.NewLine + Environment.NewLine +
        string.Join(" · ", new[]
        {
            Reversible ? "Reversível" : "Não reversível",
            RequiresAdmin ? "Requer administrador" : null,
            RequiresRestart ? "Requer reiniciar" : null,
            "Perfis: " + string.Join(", ", Tweak.Profiles.Order().Select(LabelsPt.Of)),
        }.Where(x => x is not null));

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isApplied;

    /// <summary>"Aplicado" (pelo app, desfazível no histórico) ou "Já configurado" (o sistema já estava assim).</summary>
    [ObservableProperty] private string _appliedText = "Aplicado";
}

public sealed partial class CategoryFilter(string title, TweakCategory? category) : ObservableObject
{
    public string Title { get; } = title;
    public TweakCategory? Category { get; } = category;
    [ObservableProperty] private bool _isChecked;
}

public sealed class HistoryItemViewModel(JournalEntry entry)
{
    public JournalEntry Entry { get; } = entry;
    public string Name => Entry.TweakName;
    public string When => Format.When(Entry.AppliedAt);
    public JournalStatus Status => Entry.Status;
    public string StatusText => Entry.Status switch
    {
        JournalStatus.Applied => "Aplicado",
        JournalStatus.Reverted => $"Revertido {Format.When(Entry.RevertedAt ?? Entry.AppliedAt)}",
        _ => "Falhou",
    };
    public bool CanUndo => Entry.Status == JournalStatus.Applied;
    public string? Error => Entry.Error;
    public string Details => string.Join(Environment.NewLine, Entry.Snapshots.Select(s => "• " + s.Description))
        + (Entry.RestorePointCreated ? Environment.NewLine + "• Ponto de restauração criado antes" : "")
        + (Entry.RegistryBackups.Count > 0 ? Environment.NewLine + $"• {Entry.RegistryBackups.Count} backup(s) do registro salvos" : "");
}

public sealed partial class OptimizeViewModel : ObservableObject
{
    private readonly OptimizationEngine _engine;
    private readonly DialogService _dialogs;
    private readonly IElevationService _elevation;
    private readonly AppSettings _settings;
    private readonly AppLog _log;

    public ObservableCollection<TweakItemViewModel> Items { get; } = new();
    public ObservableCollection<TweakItemViewModel> VisibleItems { get; } = new();
    public ObservableCollection<CategoryFilter> Filters { get; } = new();
    public ObservableCollection<HistoryItemViewModel> History { get; } = new();
    public IReadOnlyList<OptimizationProfile> Profiles { get; } = Enum.GetValues<OptimizationProfile>();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _appliedCount;

    public int TotalCount => Items.Count;
    public IEnumerable<TweakItemViewModel> AllItems => Items;

    /// <summary>Data da última otimização aplicada pelo app (para o Dashboard).</summary>
    public DateTimeOffset? LastAppliedAt => _engine.Journal.Entries
        .Where(e => e.Status != JournalStatus.Failed).Select(e => (DateTimeOffset?)e.AppliedAt).Max();

    /// <summary>Disparado quando o estado aplicado muda (o Dashboard escuta).</summary>
    public event EventHandler? StateChanged;

    public OptimizeViewModel(OptimizationEngine engine, DialogService dialogs, IElevationService elevation, AppSettings settings, AppLog log)
    {
        _engine = engine;
        _dialogs = dialogs;
        _elevation = elevation;
        _settings = settings;
        _log = log;

        foreach (var t in TweakCatalog.All.OrderBy(t => t.Category).ThenByDescending(t => t.Impact))
        {
            var item = new TweakItemViewModel(t);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TweakItemViewModel.IsSelected)) UpdateCounts();
            };
            Items.Add(item);
        }

        Filters.Add(new CategoryFilter("Todas", null) { IsChecked = true });
        foreach (var c in Items.Select(i => i.Tweak.Category).Distinct().Order())
            Filters.Add(new CategoryFilter(LabelsPt.Of(c), c));
        foreach (var f in Filters)
            f.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CategoryFilter.IsChecked) && f.IsChecked) ApplyFilter(f.Category);
            };
        ApplyFilter(null);
        RefreshHistory();
    }

    public static string CategoryIcon(TweakCategory c) => c switch
    {
        TweakCategory.ProcessesServices => "Icon.List",
        TweakCategory.Memory => "Icon.Memory",
        TweakCategory.Cpu => "Icon.Cpu",
        TweakCategory.Gpu => "Icon.Gpu",
        TweakCategory.Disk => "Icon.Disk",
        TweakCategory.Network => "Icon.Network",
        TweakCategory.SystemPrivacy => "Icon.Shield",
        TweakCategory.Gaming => "Icon.Gamepad",
        _ => "Icon.Settings",
    };

    private void ApplyFilter(TweakCategory? category)
    {
        VisibleItems.Clear();
        foreach (var i in Items.Where(i => category is null || i.Tweak.Category == category)) VisibleItems.Add(i);
    }

    private void UpdateCounts()
    {
        SelectedCount = Items.Count(i => i.IsSelected);
        AppliedCount = Items.Count(i => i.IsApplied);
    }

    /// <summary>Lê o estado real do sistema para marcar o que já está aplicado.</summary>
    [RelayCommand]
    public async Task RefreshStatusAsync()
    {
        var byApp = _engine.Journal.ActiveEntries.Select(e => e.TweakId).ToHashSet();
        foreach (var item in Items)
        {
            item.AppliedText = byApp.Contains(item.Tweak.Id) ? "Aplicado" : "Já configurado";
            try { item.IsApplied = await Task.Run(() => _engine.IsAppliedAsync(item.Tweak)); }
            catch (Exception ex) { _log.Error($"Status de {item.Tweak.Id}", ex); }
        }
        UpdateCounts();
        RefreshHistory();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var e in _engine.Journal.Entries.OrderByDescending(e => e.AppliedAt))
            History.Add(new HistoryItemViewModel(e));
    }

    [RelayCommand]
    private void SelectProfile(OptimizationProfile profile)
    {
        var ids = TweakCatalog.ForProfile(profile).Select(t => t.Id).ToHashSet();
        foreach (var item in Items) item.IsSelected = ids.Contains(item.Tweak.Id);
        Status = $"Perfil {LabelsPt.Of(profile)}: {ids.Count} ajuste(s) selecionado(s).";
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items) item.IsSelected = false;
        Status = "";
    }

    public void SelectByIds(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        foreach (var item in Items) item.IsSelected = set.Contains(item.Tweak.Id);
    }

    [RelayCommand]
    private Task ApplySelectedAsync() => ApplyAsync(Items.Where(i => i.IsSelected).Select(i => i.Tweak).ToList());

    /// <summary>Usado pela "Otimização rápida" do Dashboard.</summary>
    public Task<ApplyReport?> QuickOptimizeAsync(OptimizationProfile profile)
    {
        SelectProfile(profile);
        return ApplyAsync(TweakCatalog.ForProfile(profile));
    }

    /// <summary>Usado pelas recomendações do Dashboard.</summary>
    public Task<ApplyReport?> ApplyOneAsync(Tweak tweak)
    {
        SelectByIds([tweak.Id]);
        return ApplyAsync([tweak]);
    }

    private async Task<ApplyReport?> ApplyAsync(IReadOnlyList<Tweak> tweaks)
    {
        if (IsBusy) return null;
        if (tweaks.Count == 0)
        {
            Status = "Selecione ao menos um ajuste.";
            return null;
        }
        var pending = tweaks.Where(t => Items.First(i => i.Tweak.Id == t.Id).IsApplied == false).ToList();
        if (pending.Count == 0)
        {
            _dialogs.Info("Nada a aplicar", "Os ajustes selecionados já estão ativos.", kind: DialogKind.Success);
            return null;
        }

        if (pending.Any(t => t.RequiresAdmin) && !EnsureElevated(pending.Select(t => t.Id)))
            return null;

        if (!_dialogs.Confirm(
                pending.Count == 1 ? $"Aplicar \"{pending[0].Name}\"?" : $"Aplicar {pending.Count} ajustes?",
                (_settings.CreateRestorePoint ? "Antes, criamos um ponto de restauração e um backup do registro. " : "Antes, salvamos um backup do registro. ")
                + "Você pode reverter cada ajuste quando quiser.",
                pending.Select(t => $"{t.Name} · {LabelsPt.Of(t.Impact).ToLowerInvariant()} · risco {LabelsPt.Of(t.Risk).ToLowerInvariant()}").ToList(),
                confirmText: "Aplicar alterações"))
            return null;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var options = new ApplyOptions
            {
                CreateRestorePoint = _settings.CreateRestorePoint,
                AbortIfRestorePointFails = _settings.AbortIfRestorePointFails,
                Progress = progress,
                ConfirmHighRisk = high => Task.FromResult(_dialogs.Confirm(
                    "Aplicar ajustes de risco alto?",
                    "Estes ajustes podem desativar recursos do Windows ou causar instabilidade. Continue só se souber o que fazem.",
                    high.Select(t => $"{t.Name}: {t.Description}").ToList(),
                    DialogKind.Danger, "Aplicar mesmo assim", "Pular estes")),
            };

            var report = await Task.Run(() => _engine.ApplyAsync(pending, options));
            _log.Info($"Aplicação: {report.AppliedCount} ok, {report.FailedCount} falhas, abortado={report.Aborted}");
            ShowReport(report);
            return report;
        }
        catch (Exception ex)
        {
            _log.Error("Falha ao aplicar otimizações", ex);
            _dialogs.Info("Não foi possível aplicar", ex.Message + " Nada além do que foi listado no histórico mudou.", kind: DialogKind.Danger);
            return null;
        }
        finally
        {
            IsBusy = false;
            Status = "";
            await RefreshStatusAsync();
        }
    }

    private void ShowReport(ApplyReport report)
    {
        if (report.Aborted)
        {
            _dialogs.Info("Nada foi alterado", report.AbortReason ?? "", kind: DialogKind.Danger);
            return;
        }

        var lines = new List<string>();
        if (report.RestorePoint is { } rp)
            lines.Add((rp.Success ? "Ponto de restauração criado." : "Ponto de restauração não criado: " + rp.Message));
        foreach (var r in report.Results)
        {
            lines.Add(r.Outcome switch
            {
                TweakOutcome.Applied => $"Aplicado: {r.Tweak.Name}",
                TweakOutcome.AlreadyApplied => $"Já estava ativo: {r.Tweak.Name}",
                TweakOutcome.SkippedHighRisk => $"Pulado (risco alto): {r.Tweak.Name}",
                _ => $"Falhou: {r.Tweak.Name}. {r.Message}",
            });
        }

        var title = report.FailedCount == 0
            ? $"Pronto. {report.AppliedCount} ajuste(s) aplicado(s)."
            : $"{report.AppliedCount} aplicado(s), {report.FailedCount} com falha";
        var message = "Você pode reverter cada um no histórico de alterações.";
        if (report.RequiresRestart) message = "Reinicie o PC para concluir. " + message;
        _dialogs.Info(title, message, lines, report.FailedCount > 0 ? DialogKind.Danger : DialogKind.Success);
    }

    [RelayCommand]
    private async Task UndoAsync(HistoryItemViewModel? item)
    {
        if (item is null || !item.CanUndo || IsBusy) return;
        if (_engine.RevertRequiresAdmin(item.Entry) && !EnsureElevated([])) return;
        if (!_dialogs.Confirm($"Desfazer \"{item.Name}\"?", "Este ajuste volta ao estado de antes. Você pode aplicá-lo de novo quando quiser.",
                confirmText: "Desfazer"))
            return;

        IsBusy = true;
        try
        {
            var r = await Task.Run(() => _engine.RevertAsync(item.Entry.Id));
            _log.Info($"Reverter {item.Entry.TweakId}: {r.Message}");
            if (!r.Success) _dialogs.Info("Não conseguimos desfazer tudo", r.Message, kind: DialogKind.Danger);
        }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    public async Task RevertAllAsync()
    {
        var active = _engine.Journal.ActiveEntries;
        if (IsBusy) return;
        if (active.Count == 0)
        {
            _dialogs.Info("Nada para reverter", "Nenhum ajuste feito pelo NinjaOptimizer está ativo.");
            return;
        }
        if (active.Any(_engine.RevertRequiresAdmin) && !EnsureElevated([])) return;
        if (!_dialogs.Confirm("Reverter tudo?", "Cada ajuste volta ao estado de antes, do mais recente para o mais antigo.",
                active.Select(e => e.TweakName).ToList(), DialogKind.Danger, "Reverter tudo"))
            return;

        IsBusy = true;
        try
        {
            Status = "Revertendo…";
            var results = await Task.Run(() => _engine.RevertAllAsync());
            var failed = results.Where(r => !r.Success).ToList();
            _log.Info($"Reverter tudo: {results.Count - failed.Count} ok, {failed.Count} falhas");
            if (failed.Count > 0)
                _dialogs.Info($"{failed.Count} ajuste(s) não foram revertidos", "Os demais voltaram ao estado de antes.",
                    failed.Select(f => $"{f.Entry.TweakName}: {f.Message}").ToList(), DialogKind.Danger);
            else
                _dialogs.Info($"Pronto. {results.Count} ajuste(s) revertido(s).", "O PC voltou ao estado de antes desses ajustes.", kind: DialogKind.Success);
        }
        finally
        {
            IsBusy = false;
            Status = "";
            await RefreshStatusAsync();
        }
    }

    /// <summary>
    /// Pede elevação somente quando necessário: reinicia o app como administrador
    /// preservando a seleção atual.
    /// </summary>
    private bool EnsureElevated(IEnumerable<string> selectionToRestore)
    {
        if (_elevation.IsElevated) return true;
        if (!_dialogs.Confirm("Abrir como administrador?",
                "Este ajuste muda configurações do Windows. O NinjaOptimizer reabre como administrador e mantém sua seleção.",
                confirmText: "Reabrir como administrador"))
            return false;

        var ids = string.Join(",", selectionToRestore);
        var args = "--page optimize" + (ids.Length > 0 ? $" --select {ids}" : "");
        if (WindowsElevationService.RelaunchElevated(args))
            Application.Current.Shutdown();
        return false;
    }
}
