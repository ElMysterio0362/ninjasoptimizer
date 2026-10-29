using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Optimization;

public enum RiskLevel { Low, Medium, High }

/// <summary>Ganho esperado do ajuste (exibido como "Alto/Médio/Baixo impacto").</summary>
public enum ImpactLevel { Low, Medium, High }

public enum TweakCategory
{
    ProcessesServices,
    Memory,
    Cpu,
    Gpu,
    Disk,
    Network,
    SystemPrivacy,
    Gaming,
}

public enum OptimizationProfile { Safe, Balanced, MaxPerformance, Gamer }

/// <summary>Uma otimização individual, composta por uma ou mais ações reversíveis.</summary>
public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Benefit { get; init; }
    public required TweakCategory Category { get; init; }
    public required RiskLevel Risk { get; init; }
    public ImpactLevel Impact { get; init; } = ImpactLevel.Medium;
    public required IReadOnlyList<TweakAction> Actions { get; init; }

    /// <summary>Perfis prontos que incluem este ajuste.</summary>
    public IReadOnlySet<OptimizationProfile> Profiles { get; init; } = new HashSet<OptimizationProfile>();

    public bool RequiresRestart { get; init; }

    /// <summary>
    /// O que pode acontecer depois de aplicar, em frases curtas para leigo. Obrigatório em risco Médio ou Alto
    /// (garantido por teste). Mostrado antes de aplicar.
    /// </summary>
    public IReadOnlyList<string> Consequences { get; init; } = [];

    /// <summary>Consequências para este PC (soma o aviso de notebook quando for o caso).</summary>
    public IReadOnlyList<string> ConsequencesFor(bool? portable) =>
        portable == true && PortableWarning is { } w ? [.. Consequences, w] : Consequences;

    /// <summary>Aviso extra mostrado só em notebook (ex.: gasta mais bateria).</summary>
    public string? PortableWarning { get; init; }

    /// <summary>Condições para fazer sentido neste PC (vazio = sempre aplicável).</summary>
    public IReadOnlyList<TweakCondition> Conditions { get; init; } = [];

    /// <summary>Primeira condição que falha, ou Ok.</summary>
    public ConditionResult CheckConditions(Abstractions.SystemContext ctx)
    {
        foreach (var c in Conditions)
            if (c.Check(ctx) is { Applicable: false } r) return r;
        return ConditionResult.Ok;
    }

    /// <summary>Reversível se todas as ações guardam o estado anterior.</summary>
    public bool Reversible => Actions.All(a => a.Reversible);

    public bool RequiresAdmin => Actions.Any(a => a.RequiresAdmin);

    public override string ToString() => $"{Id} ({Name})";
}

public static class LabelsPt
{
    public static string Of(RiskLevel r) => r switch
    {
        RiskLevel.Low => "Baixo",
        RiskLevel.Medium => "Médio",
        _ => "Alto",
    };

    public static string Of(ImpactLevel i) => i switch
    {
        ImpactLevel.High => "Alto impacto",
        ImpactLevel.Medium => "Médio impacto",
        _ => "Baixo impacto",
    };

    public static string Of(TweakCategory c) => c switch
    {
        TweakCategory.ProcessesServices => "Processos e serviços",
        TweakCategory.Memory => "Memória (RAM)",
        TweakCategory.Cpu => "CPU",
        TweakCategory.Gpu => "GPU",
        TweakCategory.Disk => "Disco",
        TweakCategory.Network => "Rede",
        TweakCategory.SystemPrivacy => "Sistema e privacidade",
        TweakCategory.Gaming => "Jogos",
        _ => c.ToString(),
    };

    public static string Of(OptimizationProfile p) => p switch
    {
        OptimizationProfile.Safe => "Seguro",
        OptimizationProfile.Balanced => "Equilibrado",
        OptimizationProfile.MaxPerformance => "Máximo Desempenho",
        OptimizationProfile.Gamer => "Gamer",
        _ => p.ToString(),
    };
}
