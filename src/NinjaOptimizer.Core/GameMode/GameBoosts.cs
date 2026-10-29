namespace NinjaOptimizer.Core.GameMode;

/// <summary>Leituras e ações de memória/timer usadas durante o jogo (o que o ISLC fazia).</summary>
public interface IGameBoosts
{
    /// <summary>Lista em espera e memória livre, em MB (null = não deu para ler).</summary>
    (long StandbyMb, long FreeMb)? ReadMemory();
    bool PurgeStandby();
    /// <summary>Pede (true) ou solta (false) a resolução de timer de 0,5 ms.</summary>
    bool SetHighResolutionTimer(bool on);
}

/// <summary>Decide quando limpar a memória em espera: os dois limites juntos e um intervalo mínimo entre limpezas.</summary>
public sealed class StandbyAutoPurge(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Depois de limpar, espera isso antes de poder limpar de novo (evita engasgos seguidos).</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    public int Purges { get; private set; }

    public bool ShouldPurge(long standbyMb, long freeMb, GameModeSettings s) =>
        s.AutoPurgeStandby && standbyMb > s.StandbyThresholdMb && freeMb < s.FreeThresholdMb && _time.GetUtcNow() - _last >= Cooldown;

    /// <summary>Confere e limpa se precisar. true = limpou.</summary>
    public bool Tick(IGameBoosts boosts, GameModeSettings s)
    {
        if (boosts.ReadMemory() is not { } m || !ShouldPurge(m.StandbyMb, m.FreeMb, s)) return false;
        if (!boosts.PurgeStandby()) return false;
        _last = _time.GetUtcNow();
        Purges++;
        return true;
    }
}

public static class GameBoostTexts
{
    public static readonly string[] PurgeConsequences =
        ["Pode causar um engasgo curto no momento da limpeza; arquivos e programas usados há pouco recarregam do disco."];

    public static readonly string[] TimerConsequences =
    [
        "Mais consumo de energia e CPU um pouco mais ativa; em notebook a bateria dura menos.",
        "O ganho de latência é pequeno e varia por jogo.",
    ];
}
