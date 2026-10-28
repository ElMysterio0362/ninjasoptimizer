using System.Text.Json;

namespace NinjaOptimizer.Core.GameMode;

public sealed record ProcessInfo(int Id, string Name, string? Path, long MemoryBytes, int SessionId, long StartTicks);

/// <summary>Acesso aos processos do Windows (fácil de simular nos testes).</summary>
public interface IProcessControl
{
    IReadOnlyList<ProcessInfo> List();
    int CurrentSessionId { get; }
    int? ForegroundProcessId();
    bool Suspend(int pid);
    bool Resume(int pid);
}

/// <summary>Decide o que é jogo e o que nunca pode ser pausado.</summary>
public static class ProcessRules
{
    /// <summary>Partes do Windows, segurança, áudio, drivers e anti-cheat: pausar qualquer um trava o PC ou o jogo.</summary>
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "audiodg", "conhost", "dllhost", "taskhostw", "runtimebroker",
        "startmenuexperiencehost", "shellexperiencehost", "searchhost", "textinputhost", "applicationframehost", "lockapp",
        "securityhealthservice", "securityhealthsystray", "msmpeng", "nissrv", "mpdefendercoreservice", "smartscreen",
        "spoolsv", "wmiprvse", "wudfhost", "dashost", "audiosrv", "rtkauduservice64", "nvdisplay.container", "nvcontainer",
        "amdrsserv", "radeonsoftware", "atieclxx", "atiesrxx", "igfxem", "igfxcuiservice",
        "easyanticheat", "easyanticheat_eos", "beservice", "battleye", "vgc", "vgtray", "faceitclient", "faceit", "gameguard", "xigncode",
        "ninjaoptimizer", "taskmgr", "procexp", "procexp64",
    };

    /// <summary>Apps que costumam pesar em segundo plano e não fazem falta durante o jogo.</summary>
    public static readonly string[] DefaultPauseList =
    [
        "OneDrive", "Dropbox", "GoogleDriveFS", "ms-teams", "Teams", "Skype", "PhoneExperienceHost", "YourPhone",
        "AdobeCollabSync", "CCXProcess", "Creative Cloud", "AdobeIPCBroker", "acrotray",
    ];

    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\gog galaxy\games\", @"\xboxgames\",
        @"\battle.net\", @"\ubisoft game launcher\games\", @"\ea games\", @"\origin games\", @"\windowsapps\microsoft.minecraft",
    ];

    private static readonly string[] NotGames = ["launcher", "crashhandler", "crashreport", "unitycrashhandler", "easyanticheat", "uninstall", "setup", "redist", "vc_redist", "dxsetup"];

    public static bool IsProtected(string name) => Protected.Contains(Strip(name));

    public static string Strip(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>É jogo se está numa pasta de jogos conhecida ou na lista do usuário.</summary>
    public static bool IsGame(ProcessInfo p, IReadOnlyCollection<string> userGames)
    {
        var name = Strip(p.Name);
        if (userGames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (p.Path is null) return false;
        var lower = name.ToLowerInvariant();
        if (NotGames.Any(lower.Contains)) return false;
        var path = p.Path.ToLowerInvariant();
        return GameFolders.Any(path.Contains);
    }
}

public sealed class GameModeSettings
{
    public bool AutoActivate { get; set; } = true;
    public List<string> PauseList { get; set; } = [.. ProcessRules.DefaultPauseList];
    public List<string> UserGames { get; set; } = [];
}

/// <summary>
/// Pausa e retoma processos. Os pausados ficam gravados em disco: se o app fechar ou travar,
/// ele retoma tudo na próxima abertura (nenhum programa fica congelado para sempre).
/// </summary>
public sealed class GameModeService(IProcessControl processes, string statePath)
{
    public sealed record Paused(int Id, string Name, long StartTicks);
    private sealed record State(List<Paused> Paused, string? Game);

    private readonly object _gate = new();
    private List<Paused> _paused = [];

    public bool IsActive { get; private set; }
    public string? Game { get; private set; }
    public IReadOnlyList<Paused> PausedProcesses { get { lock (_gate) return [.. _paused]; } }

    /// <summary>Retoma processos que uma sessão anterior deixou pausados. Chamar ao abrir o app.</summary>
    public int RecoverFromPreviousRun()
    {
        if (!File.Exists(statePath)) return 0;
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(statePath));
            if (state is null) return 0;
            lock (_gate) _paused = state.Paused;
            return ResumeAll();
        }
        catch (JsonException) { File.Delete(statePath); return 0; }
    }

    /// <summary>Pausa os processos da lista (nunca os protegidos, nem o jogo, nem os de outras contas).</summary>
    public IReadOnlyList<Paused> Activate(IEnumerable<string> pauseList, string? game = null, int? gamePid = null)
    {
        var wanted = new HashSet<string>(pauseList.Select(ProcessRules.Strip), StringComparer.OrdinalIgnoreCase);
        var session = processes.CurrentSessionId;
        var done = new List<Paused>();
        lock (_gate)
        {
            foreach (var p in processes.List())
            {
                if (p.Id == gamePid || p.SessionId != session || ProcessRules.IsProtected(p.Name)) continue;
                if (!wanted.Contains(ProcessRules.Strip(p.Name))) continue;
                if (_paused.Any(x => x.Id == p.Id)) continue;
                if (!processes.Suspend(p.Id)) continue;
                var entry = new Paused(p.Id, ProcessRules.Strip(p.Name), p.StartTicks);
                _paused.Add(entry);
                done.Add(entry);
                Save(game); // grava a cada pausa: mesmo um travamento no meio não perde nenhum
            }
            IsActive = true;
            Game = game;
            Save(game);
        }
        return done;
    }

    /// <summary>Retoma todos os pausados. Confere o horário de início para não mexer num PID reaproveitado.</summary>
    public int Deactivate() => ResumeAll();

    private int ResumeAll()
    {
        lock (_gate)
        {
            var alive = processes.List().ToDictionary(p => p.Id);
            var count = 0;
            foreach (var p in _paused)
                if (alive.TryGetValue(p.Id, out var cur) && cur.StartTicks == p.StartTicks && processes.Resume(p.Id)) count++;
            _paused.Clear();
            IsActive = false;
            Game = null;
            try { File.Delete(statePath); } catch (IOException) { }
            return count;
        }
    }

    private void Save(string? game)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, JsonSerializer.Serialize(new State(_paused, game)));
    }

    /// <summary>Jogo aberto agora: prefere o que está em primeiro plano.</summary>
    public ProcessInfo? FindRunningGame(IReadOnlyCollection<string> userGames)
    {
        var games = processes.List().Where(p => p.SessionId == processes.CurrentSessionId && ProcessRules.IsGame(p, userGames)).ToList();
        if (games.Count == 0) return null;
        var fg = processes.ForegroundProcessId();
        return games.FirstOrDefault(g => g.Id == fg) ?? games.MaxBy(g => g.MemoryBytes);
    }
}
