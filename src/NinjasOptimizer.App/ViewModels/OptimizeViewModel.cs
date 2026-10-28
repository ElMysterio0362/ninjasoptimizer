using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjasOptimizer.App.Services;
using NinjasOptimizer.Core.Abstractions;
using NinjasOptimizer.Core.Optimization;
using NinjasOptimizer.Core.Safety;
using NinjasOptimizer.Core.Settings;
using NinjasOptimizer.Windows.Platform;

namespace NinjasOptimizer.App.ViewModels;

public sealed partial class TweakItemViewModel(Tweak tweak) : ObservableObject
{
    public Tweak Tweak { get; } = tweak;
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string Benefit => Tweak.Benefit;
    public RiskLevel Risk => Tweak.Risk;
    public string RiskText => "Risco " + LabelsPt.Of(Tweak.Risk).ToLowerInvariant();
    public bool Reversible => Tweak.Reversible;
    public bool RequiresAdmin => Tweak.RequiresAdmin;
    public bool RequiresRestart => Tweak.RequiresRestart;
    public string ProfilesText => string.Join(" · ", Tweak.Profiles.Order().Select(LabelsPt.Of));
    public string ActionsText => string.Join(Environment.NewLine, Tweak.Actions.Select(a => "• " + a.Describe()));

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isApplied;
}

public sealed class TweakGroupViewModel(string title, string glyph, IEnumerable<TweakItemViewModel> items)
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public ObservableCollection<TweakItemViewModel> Items { get; } = new(items);
}

public sealed class HistoryItemViewModel(JournalEntry entry)
{
    public JournalEntry Entry { get; } = entry;
    public string Name => Entry.TweakName;
    public string When => Entry.AppliedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
    public JournalStatus Status => Entry.Status;
    public string StatusText => Entry.Status switch
    {
        JournalStatus.Applied => "Aplicado",
        JournalStatus.Reverted => $"Revertido em {Entry.RevertedAt?.LocalDateTime:dd/MM HH:mm}",
        _ => "Falhou",
    };
    public bool CanUndo => Entry.Status == JournalStatus.Applied;
    public string? Error => Entry.Error;
    public string Details => string.Join(Environment.NewLine, Entry.Snapshots.Select(s => "• " + s.Description))
        + (Entry.RestorePointCreated ? Environment.NewLine + "• Ponto de restauração criado antes" : "")
        + (Entry.RegistryBackups.Count > 0 ? Environment.NewLine + $"• {Entry.RegistryBackups.Count} backup(s) .reg salvos" : "");
}

public sealed partial class OptimizeViewModel : ObservableObject
{
    private readonly OptimizationEngine _engine;
    private readonly DialogService _dialogs;
    private readonly IElevationService _elevation;
    private readonly AppSettings _settings;
    private readonly AppLog _log;

    public ObservableCollection<TweakGroupViewModel> Groups { get; } = new();
    public ObservableCollection<HistoryItemViewModel> History { get; } = new();
    public IReadOnlyList<OptimizationProfile> Profiles { get; } = Enum.GetValues<OptimizationProfile>();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _appliedCount;

    public int TotalCount => AllItems.Count();
    public IEnumerable<TweakItemViewModel> AllItems => Groups.SelectMany(g => g.Items);

    /// <summary>Disparado quando o estado aplicado muda (o Dashboard escuta).</summary>
    public event EventHandler? StateChanged;

    public OptimizeViewModel(OptimizationEngine engine, DialogService dialogs, IElevationService elevation, AppSettings settings, AppLog log)
    {
        _engine = engine;
        _dialogs = dialogs;
        _elevation = elevation;
        _settings = settings;
        _log = log;

        foreach (var g in TweakCatalog.All.GroupBy(t => t.Category).OrderBy(g => g.Key))
        {
            var group = new TweakGroupViewModel(LabelsPt.Of(g.Key), CategoryGlyph(g.Key), g.Select(t => new TweakItemViewModel(t)));
            foreach (var item in group.Items)
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TweakItemViewModel.IsSelected)) UpdateCounts();
                };
            Groups.Add(group);
        }
        RefreshHistory();
    }

    private static string CategoryGlyph(TweakCategory c) => c switch
    {
        TweakCategory.ProcessesServices => "",
        TweakCategory.Memory => "",
        TweakCategory.Cpu => "",
        TweakCategory.Gpu => "",
        TweakCategory.Disk => "",
        TweakCategory.Network => "",
        TweakCategory.SystemPrivacy => "",
        TweakCategory.Gaming => "",
        _ => "",
    };

    private void UpdateCounts()
    {
        SelectedCount = AllItems.Count(i => i.IsSelected);
        AppliedCount = AllItems.Count(i => i.IsApplied);
    }

    /// <summary>Lê o estado real do sistema para marcar o que já está aplicado.</summary>
    [RelayCommand]
    public async Task RefreshStatusAsync()
    {
        foreach (var item in AllItems)
        {
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
        foreach (var item in AllItems) item.IsSelected = ids.Contains(item.Tweak.Id);
        Status = $"Perfil {LabelsPt.Of(profile)}: {ids.Count} otimização(ões) selecionada(s). Revise e clique em \"Aplicar selecionadas\".";
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in AllItems) item.IsSelected = false;
    }

    public void SelectByIds(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        foreach (var item in AllItems) item.IsSelected = set.Contains(item.Tweak.Id);
    }

    [RelayCommand]
    private Task ApplySelectedAsync() => ApplyAsync(AllItems.Where(i => i.IsSelected).Select(i => i.Tweak).ToList(), askConfirmation: true);

    /// <summary>Usado pela "Otimização rápida" do Dashboard.</summary>
    public Task QuickOptimizeAsync(OptimizationProfile profile)
    {
        SelectProfile(profile);
        return ApplyAsync(TweakCatalog.ForProfile(profile), askConfirmation: true);
    }

    private async Task ApplyAsync(IReadOnlyList<Tweak> tweaks, bool askConfirmation)
    {
        if (IsBusy) return;
        var pending = tweaks.Where(t => AllItems.First(i => i.Tweak.Id == t.Id).IsApplied == false).ToList();
        if (tweaks.Count == 0)
        {
            Status = "Selecione ao menos uma otimização.";
            return;
        }
        if (pending.Count == 0)
        {
            _dialogs.Info("Nada a fazer", "Todas as otimizações selecionadas já estão aplicadas.", kind: DialogKind.Success);
            return;
        }

        if (pending.Any(t => t.RequiresAdmin) && !EnsureElevated(pending.Select(t => t.Id)))
            return;

        if (askConfirmation && !_dialogs.Confirm(
                $"Aplicar {pending.Count} otimização(ões)?",
                (_settings.CreateRestorePoint ? "Antes de qualquer alteração será criado um ponto de restauração do sistema e um backup das chaves de registro. " : "Será criado um backup das chaves de registro. ")
                + "Tudo poderá ser desfeito item a item no histórico.",
                pending.Select(t => $"{t.Name}  ·  risco {LabelsPt.Of(t.Risk).ToLowerInvariant()}").ToList(),
                confirmText: "Aplicar"))
            return;

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
                    "Atenção: otimizações de risco alto",
                    "Estes ajustes podem causar instabilidade ou desativar recursos do Windows. Só continue se souber o que está fazendo.",
                    high.Select(t => $"{t.Name} — {t.Description}").ToList(),
                    DialogKind.Danger, "Aplicar mesmo assim")),
            };

            var report = await Task.Run(() => _engine.ApplyAsync(pending, options));
            _log.Info($"Aplicação: {report.AppliedCount} ok, {report.FailedCount} falhas, abortado={report.Aborted}");
            ShowReport(report);
        }
        catch (Exception ex)
        {
            _log.Error("Falha ao aplicar otimizações", ex);
            _dialogs.Info("Erro", ex.Message, kind: DialogKind.Danger);
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
            _dialogs.Info("Operação cancelada", report.AbortReason ?? "", kind: DialogKind.Danger);
            return;
        }

        var lines = new List<string>();
        if (report.RestorePoint is { } rp)
            lines.Add((rp.Success ? "✔ " : "⚠ ") + "Ponto de restauração: " + rp.Message);
        foreach (var r in report.Results)
        {
            lines.Add(r.Outcome switch
            {
                TweakOutcome.Applied => $"✔ {r.Tweak.Name}",
                TweakOutcome.AlreadyApplied => $"• {r.Tweak.Name} (já estava aplicado)",
                TweakOutcome.SkippedHighRisk => $"⏭ {r.Tweak.Name} (risco alto não confirmado)",
                _ => $"✖ {r.Tweak.Name}: {r.Message}",
            });
        }

        var message = $"{report.AppliedCount} aplicada(s), {report.FailedCount} com falha.";
        if (report.RequiresRestart) message += " Reinicie o computador para concluir algumas alterações.";
        _dialogs.Info("Otimização concluída", message, lines, report.FailedCount > 0 ? DialogKind.Danger : DialogKind.Success);
    }

    [RelayCommand]
    private async Task UndoAsync(HistoryItemViewModel? item)
    {
        if (item is null || !item.CanUndo || IsBusy) return;
        if (_engine.RevertRequiresAdmin(item.Entry) && !EnsureElevated([])) return;
        if (!_dialogs.Confirm("Desfazer alteração?", $"\"{item.Name}\" voltará ao estado anterior.", confirmText: "Desfazer")) return;

        IsBusy = true;
        try
        {
            var r = await Task.Run(() => _engine.RevertAsync(item.Entry.Id));
            _log.Info($"Reverter {item.Entry.TweakId}: {r.Message}");
            if (!r.Success) _dialogs.Info("Não foi possível reverter tudo", r.Message, kind: DialogKind.Danger);
        }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        var active = _engine.Journal.ActiveEntries;
        if (IsBusy) return;
        if (active.Count == 0)
        {
            _dialogs.Info("Nada para reverter", "Nenhuma otimização aplicada pelo Ninjas Optimizer está ativa.");
            return;
        }
        if (active.Any(_engine.RevertRequiresAdmin) && !EnsureElevated([])) return;
        if (!_dialogs.Confirm("Reverter tudo?", "Todas as otimizações aplicadas voltarão ao estado original, da mais recente para a mais antiga.",
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
                _dialogs.Info("Alguns itens não foram revertidos", "Veja os detalhes abaixo.",
                    failed.Select(f => $"{f.Entry.TweakName}: {f.Message}").ToList(), DialogKind.Danger);
            else
                _dialogs.Info("Tudo revertido", $"{results.Count} otimização(ões) desfeita(s).", kind: DialogKind.Success);
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
        if (!_dialogs.Confirm("Permissão de administrador necessária",
                "Esta ação altera configurações do sistema e precisa de privilégios de administrador. " +
                "O Ninjas Optimizer será reaberto como administrador, mantendo sua seleção.",
                confirmText: "Reabrir como administrador"))
            return false;

        var ids = string.Join(",", selectionToRestore);
        var args = "--page optimize" + (ids.Length > 0 ? $" --select {ids}" : "");
        if (WindowsElevationService.RelaunchElevated(args))
            Application.Current.Shutdown();
        return false;
    }
}
