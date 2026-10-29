using NinjaOptimizer.Core.Optimization.Actions;
using NinjaOptimizer.Core.Safety;

namespace NinjaOptimizer.Core.Optimization;

/// <summary>
/// Prioridade de CPU por jogo (página Modo Gamer). Cada jogo vira um ajuste próprio, com o mesmo motor,
/// histórico e "Desfazer" dos ajustes do catálogo.
/// </summary>
public static class GamePriority
{
    public const string IdPrefix = "gaming.process-priority:";

    public static string IdFor(string exe) => IdPrefix + exe.ToLowerInvariant();

    public static Tweak Create(string exe, CpuPriority priority) => new()
    {
        Id = IdFor(exe),
        Name = $"Prioridade de CPU {LabelsPriority.Of(priority).ToLowerInvariant()}: {exe}",
        Description = "Faz o Windows abrir sempre esse jogo com prioridade de CPU maior que a dos outros programas. Vale na próxima vez que o jogo abrir.",
        Benefit = "Ajuda em jogos limitados pela CPU quando há coisas pesadas em segundo plano. Em jogo limitado pela placa de vídeo, quase nada muda.",
        Category = TweakCategory.Gaming,
        Risk = RiskLevel.Medium,
        Impact = ImpactLevel.Low,
        Consequences = priority == CpuPriority.High
            ? ["Áudio do Discord, OBS e de outros programas pode engasgar enquanto o jogo roda.", "Alguns anti-cheats ignoram ou rebaixam a prioridade."]
            : ["Outros programas abertos ao mesmo tempo que o jogo recebem um pouco menos de CPU."],
        Actions = [new ProcessPriorityAction(exe, priority)],
    };

    /// <summary>Entrada ativa do histórico para este jogo (se houver).</summary>
    public static JournalEntry? ActiveEntry(ChangeJournal journal, string exe) =>
        journal.ActiveEntries.LastOrDefault(e => e.TweakId == IdFor(exe));
}
