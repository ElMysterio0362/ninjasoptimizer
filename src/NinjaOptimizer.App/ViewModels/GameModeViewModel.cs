using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.GameMode;
using NinjaOptimizer.Core.Settings;

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

public sealed partial class GameModeViewModel : ObservableObject
{
    private readonly GameModeService _service;
    private readonly IProcessControl _processes;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly DialogService _dialogs;
    private readonly AppLog _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _activatedByAuto;
    private bool _checking;

    private GameModeSettings Gm => _settings.GameMode;

    public ObservableCollection<BackgroundAppViewModel> Apps { get; } = new();
    public ObservableCollection<string> UserGames { get; } = new();

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

    public GameModeViewModel(GameModeService service, IProcessControl processes, AppSettings settings, SettingsStore store, DialogService dialogs, AppLog log)
    {
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
