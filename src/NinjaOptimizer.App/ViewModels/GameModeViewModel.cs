using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.GameMode;
using NinjaOptimizer.Core.Settings;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;
using NinjaOptimizer.Windows.Platform;

namespace NinjaOptimizer.App.ViewModels;

/// <summary>Um programa em segundo plano (processos com o mesmo nome agrupados).</summary>
public sealed partial class BackgroundAppViewModel(string name, string? path, long memory, int count, GameModeViewModel owner) : ObservableObject
{
    public string Name { get; } = name;
    public string? Path { get; } = path;
    public string MemoryText { get; } = Format.Bytes((ulong)memory) + (count > 1 ? $" · {count} processos" : "");
    public long Memory { get; } = memory;

    public bool Pause
    {
        get => owner.IsInPauseList(Name);
        set { owner.SetPause(Name, value); OnPropertyChanged(); }
    }
}

public sealed record PriorityOption(CpuPriority? Value, string Title);

/// <summary>Um jogo com a prioridade de CPU escolhida (Normal = sem ajuste).</summary>
public sealed partial class GamePriorityItem(string exe, CpuPriority? current, GameModeViewModel owner) : ObservableObject
{
    public static IReadOnlyList<PriorityOption> Options { get; } =
    [
        new(null, "Normal (sem ajuste)"),
        new(CpuPriority.AboveNormal, "Acima do normal (recomendado)"),
        new(CpuPriority.High, "Alta (avançado)"),
    ];

    public string Exe { get; } = exe;
    public string? Note => Exe.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)
        ? "javaw.exe vale para todos os programas Java, não só o Minecraft." : null;
    public bool HasNote => Note is not null;

    private PriorityOption _selected = Options.FirstOrDefault(o => o.Value == current) ?? Options[0];
    public PriorityOption Selected
    {
        get => _selected;
        set
        {
            if (value is null || value == _selected) return;
            var previous = _selected;
            _selected = value;
            OnPropertyChanged();
            _ = ChangeAsync(previous, value);
        }
    }

    private async Task ChangeAsync(PriorityOption previous, PriorityOption value)
    {
        if (!await owner.SetPriorityAsync(Exe, value.Value))
        {
            _selected = previous; // cancelado ou falhou: volta o combo
            OnPropertyChanged(nameof(Selected));
        }
    }
}

public sealed partial class GameModeViewModel : ObservableObject
{
    private readonly GameModeService _service;
    private readonly IProcessControl _processes;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly DialogService _dialogs;
    private readonly AppLog _log;
    private readonly OptimizationEngine _engine;
    private readonly IElevationService _elevation;
    private readonly OptimizeViewModel _optimize;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _activatedByAuto;
    private bool _checking;

    private GameModeSettings Gm => _settings.GameMode;

    public ObservableCollection<BackgroundAppViewModel> Apps { get; } = new();
    public ObservableCollection<string> UserGames { get; } = new();
    public ObservableCollection<GamePriorityItem> Priorities { get; } = new();
    public bool HasPriorities => Priorities.Count > 0;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _statusTitle = "Modo Gamer desligado";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _newGame = "";

    public bool AutoActivate
    {
        get => Gm.AutoActivate;
        set { Gm.AutoActivate = value; Save(); OnPropertyChanged(); }
    }

    public string PauseSummary => $"{Gm.PauseList.Count} programa(s) na lista para pausar";
    public bool HasUserGames => UserGames.Count > 0;

    public GameModeViewModel(GameModeService service, IProcessControl processes, AppSettings settings, SettingsStore store, DialogService dialogs, AppLog log,
        OptimizationEngine engine, IElevationService elevation, OptimizeViewModel optimize)
    {
        _engine = engine;
        _elevation = elevation;
        _optimize = optimize;
        Priorities.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPriorities));
        _service = service;
        _processes = processes;
        _settings = settings;
        _store = store;
        _dialogs = dialogs;
        _log = log;
        foreach (var g in Gm.UserGames) UserGames.Add(g);
        UserGames.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasUserGames));
        _timer.Tick += async (_, _) => await CheckForGameAsync();
        UpdateStatus();
    }

    /// <summary>Retoma o que ficou pausado de uma sessão anterior e liga a detecção automática.</summary>
    public void Start()
    {
        try
        {
            var n = _service.RecoverFromPreviousRun();
            if (n > 0) _log.Info($"Modo Gamer: {n} processo(s) retomado(s) de uma sessão anterior");
        }
        catch (Exception ex) { _log.Error("Recuperar Modo Gamer", ex); }
        _timer.Start();
    }

    /// <summary>Ao fechar o app nada pode ficar pausado.</summary>
    public void Shutdown()
    {
        _timer.Stop();
        try { if (_service.IsActive) _service.Deactivate(); }
        catch (Exception ex) { _log.Error("Desligar Modo Gamer", ex); }
    }

    private async Task CheckForGameAsync()
    {
        if (_checking || IsBusy) return;
        _checking = true;
        try
        {
            if (!Gm.AutoActivate && !_activatedByAuto) return;
            var games = Gm.UserGames.ToList();
            var game = await Task.Run(() => _service.FindRunningGame(games));
            if (game is not null && !_service.IsActive && Gm.AutoActivate)
            {
                await ActivateCoreAsync(game);
                _activatedByAuto = true;
            }
            else if (game is null && _service.IsActive && _activatedByAuto)
            {
                await DeactivateCoreAsync();
            }
        }
        catch (Exception ex) { _log.Error("Detecção de jogo", ex); }
        finally { _checking = false; }
    }

    private async Task ActivateCoreAsync(ProcessInfo? game)
    {
        var list = Gm.PauseList.ToList();
        var name = game is null ? null : ProcessRules.Strip(game.Name);
        var paused = await Task.Run(() => _service.Activate(list, name, game?.Id));
        _log.Info($"Modo Gamer ligado ({name ?? "manual"}): {string.Join(", ", paused.Select(p => p.Name))}");
        UpdateStatus();
    }

    private async Task DeactivateCoreAsync()
    {
        var n = await Task.Run(_service.Deactivate);
        _activatedByAuto = false;
        _log.Info($"Modo Gamer desligado: {n} processo(s) retomado(s)");
        UpdateStatus();
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        IsBusy = true;
        try
        {
            if (_service.IsActive) await DeactivateCoreAsync();
            else
            {
                var games = Gm.UserGames.ToList();
                await ActivateCoreAsync(await Task.Run(() => _service.FindRunningGame(games)));
                _activatedByAuto = false; // ligado à mão: só desliga à mão
            }
        }
        catch (Exception ex)
        {
            _log.Error("Modo Gamer", ex);
            _dialogs.Info("Não deu para mudar o Modo Gamer", ex.Message, kind: DialogKind.Danger);
        }
        finally { IsBusy = false; }
    }

    private void UpdateStatus()
    {
        IsActive = _service.IsActive;
        var paused = _service.PausedProcesses;
        StatusTitle = IsActive ? (_service.Game is { } g ? $"Modo Gamer ligado · {g}" : "Modo Gamer ligado") : "Modo Gamer desligado";
        StatusText = IsActive
            ? (paused.Count == 0 ? "Nenhum programa da lista estava aberto." : $"Pausados: {string.Join(", ", paused.Select(p => p.Name).Distinct())}. Voltam ao normal quando o jogo fechar.")
            : Gm.AutoActivate ? "Liga sozinho quando um jogo abrir e desliga quando ele fechar." : "Ligue antes de jogar; nada é pausado sem você pedir.";
    }

    [RelayCommand]
    public async Task RefreshAppsAsync()
    {
        var session = _processes.CurrentSessionId;
        var procs = await Task.Run(() => _processes.List());
        var groups = procs
            .Where(p => p.SessionId == session && !ProcessRules.IsProtected(p.Name) && p.Path is not null
                        && !p.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => ProcessRules.Strip(p.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Path: g.First().Path, Mem: g.Sum(p => p.MemoryBytes), Count: g.Count()))
            .ToList();
        // Itens da lista que não estão abertos agora continuam visíveis para poder tirar
        foreach (var n in Gm.PauseList.Where(n => groups.All(g => !g.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
            groups.Add((n, null, 0, 0));
        Apps.Clear();
        foreach (var g in groups.OrderByDescending(g => IsInPauseList(g.Name)).ThenByDescending(g => g.Mem))
            Apps.Add(new BackgroundAppViewModel(g.Name, g.Path, g.Mem, g.Count, this));
        RefreshPriorities(procs);
    }

    /// <summary>Jogos para a prioridade: os abertos agora, os da sua lista e os que já têm prioridade.</summary>
    private void RefreshPriorities(IReadOnlyList<ProcessInfo> procs)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs.Where(p => ProcessRules.IsGame(p, Gm.UserGames))) names.Add(ProcessRules.Strip(p.Name) + ".exe");
        foreach (var g in Gm.UserGames) names.Add(g + ".exe");
        foreach (var e in _engine.Journal.ActiveEntries.Where(e => e.TweakId.StartsWith(GamePriority.IdPrefix)))
            if (e.Snapshots.FirstOrDefault()?.Get("exe") is { } exe) names.Add(exe);
        Priorities.Clear();
        foreach (var exe in names.Where(ProcessPriorityAction.IsValidExe))
            Priorities.Add(new GamePriorityItem(exe, ProcessPriorityAction.Current(_engine.Context.Registry, exe), this));
    }

    /// <summary>Troca a prioridade de um jogo pelo motor (com histórico e Desfazer). false = não mudou.</summary>
    internal async Task<bool> SetPriorityAsync(string exe, CpuPriority? priority)
    {
        if (!_elevation.IsElevated)
        {
            if (_dialogs.Confirm("Abrir como administrador?",
                    "A prioridade por jogo fica numa parte do registro que exige administrador. O NinjaOptimizer reabre nesta mesma página.",
                    confirmText: "Reabrir como administrador")
                && WindowsElevationService.RelaunchElevated("--page gamemode"))
                Application.Current.Shutdown();
            return false;
        }
        if (priority == CpuPriority.High && !_dialogs.Confirm($"Prioridade alta para {exe}?",
                "Use só se o jogo engasga por causa de programas em segundo plano.",
                ["Áudio do Discord, OBS ou chamadas pode falhar enquanto o jogo roda.",
                 "Alguns anti-cheats ignoram ou rebaixam a prioridade.",
                 "Pode desfazer quando quiser no histórico ou voltando para Normal."],
                DialogKind.Danger, "Usar prioridade alta"))
            return false;

        IsBusy = true;
        try
        {
            // Tira a prioridade anterior deste jogo (se houver) antes de aplicar a nova.
            if (GamePriority.ActiveEntry(_engine.Journal, exe) is { } old)
            {
                var r = await _engine.RevertAsync(old.Id);
                if (!r.Success) throw new InvalidOperationException(r.Message);
            }
            if (priority is { } p)
            {
                var report = await _engine.ApplyAsync([GamePriority.Create(exe, p)], new ApplyOptions
                {
                    CreateRestorePoint = _settings.CreateRestorePoint,
                    AbortIfRestorePointFails = _settings.AbortIfRestorePointFails,
                });
                var result = report.Results.SingleOrDefault();
                if (report.Aborted || result?.Outcome == TweakOutcome.Failed)
                    throw new InvalidOperationException(report.AbortReason ?? result?.Message);
            }
            _log.Info($"Prioridade de {exe}: {(priority is { } x ? LabelsPriority.Of(x) : "Normal")}");
            await _optimize.RefreshStatusAsync();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Prioridade por jogo", ex);
            _dialogs.Info("Não deu para mudar a prioridade", ex.Message, kind: DialogKind.Danger);
            return false;
        }
        finally { IsBusy = false; }
    }

    internal bool IsInPauseList(string name) => Gm.PauseList.Contains(name, StringComparer.OrdinalIgnoreCase);

    internal void SetPause(string name, bool pause)
    {
        Gm.PauseList.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pause) Gm.PauseList.Add(name);
        Save();
        OnPropertyChanged(nameof(PauseSummary));
    }

    [RelayCommand]
    private void MarkAsGame(BackgroundAppViewModel app) => AddGame(app.Name);

    [RelayCommand]
    private void AddNewGame()
    {
        AddGame(ProcessRules.Strip(System.IO.Path.GetFileName(NewGame.Trim())));
        NewGame = "";
    }

    private void AddGame(string name)
    {
        if (name.Length == 0 || ProcessRules.IsProtected(name) || Gm.UserGames.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        Gm.UserGames.Add(name);
        UserGames.Add(name);
        SetPause(name, false); // um jogo nunca é pausado
        foreach (var a in Apps.Where(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) a.Pause = false;
        Save();
    }

    [RelayCommand]
    private void RemoveGame(string name)
    {
        Gm.UserGames.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        UserGames.Remove(name);
        Save();
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        Gm.PauseList = [.. ProcessRules.DefaultPauseList];
        Save();
        OnPropertyChanged(nameof(PauseSummary));
        await RefreshAppsAsync();
    }

    private void Save()
    {
        try { _store.Save(_settings); }
        catch (Exception ex) { _log.Error("Salvar Modo Gamer", ex); }
        if (!IsActive) UpdateStatus();
    }
}
