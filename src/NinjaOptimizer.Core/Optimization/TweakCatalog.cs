using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization.Actions;
using static NinjaOptimizer.Core.Optimization.OptimizationProfile;

namespace NinjaOptimizer.Core.Optimization;

/// <summary>
/// Catálogo de otimizações. Cada item é declarativo: novas otimizações são só novas entradas,
/// reaproveitando as ações (registro, serviço, plano de energia…) e o sistema de reversão.
/// </summary>
public static class TweakCatalog
{
    private const string HKLM_DataCollectionPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string HKLM_DataCollection = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection";

    public static IReadOnlyList<Tweak> All { get; } = Build();

    public static Tweak Get(string id) => All.First(t => t.Id == id);

    public static IReadOnlyList<Tweak> ForProfile(OptimizationProfile profile) =>
        All.Where(t => t.Profiles.Contains(profile)).ToList();

    private static HashSet<OptimizationProfile> P(params OptimizationProfile[] p) => [.. p];

    private static List<Tweak> Build() =>
    [
        // ───────────── Sistema e privacidade ─────────────
        new Tweak
        {
            Id = "privacy.telemetry",
            Name = "Desativar telemetria do Windows",
            Description =
                "Reduz a coleta de dados de diagnóstico ao nível mínimo permitido pela edição do Windows " +
                "e desativa o serviço 'Experiências do Usuário Conectado e Telemetria' (DiagTrack).",
            Benefit = "Menos uso de disco/rede em segundo plano e mais privacidade.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Safe, Balanced, MaxPerformance, Gamer),
            Actions =
            [
                new RegistryValueAction(RegHive.LocalMachine, HKLM_DataCollectionPolicy, "AllowTelemetry", RegValue.Dword(0)),
                new RegistryValueAction(RegHive.LocalMachine, HKLM_DataCollection, "AllowTelemetry", RegValue.Dword(0)),
                new ServiceStartupAction("DiagTrack", ServiceStartMode.Disabled),
            ],
        },

        // ───────────── Jogos ─────────────
        new Tweak
        {
            Id = "gaming.gamedvr",
            Name = "Desativar Game DVR (gravação em segundo plano)",
            Description =
                "Desliga a captura contínua de jogo do Xbox Game Bar, que grava os últimos minutos de " +
                "jogo o tempo todo. A Game Bar continua abrindo normalmente com Win+G.",
            Benefit = "Libera GPU/codificador de vídeo e pode reduzir quedas de FPS e stutter.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Medium,
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions =
            [
                new RegistryValueAction(RegHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", RegValue.Dword(0)),
                new RegistryValueAction(RegHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegValue.Dword(0)),
                new RegistryValueAction(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", RegValue.Dword(0)),
            ],
        },

        // ───────────── CPU ─────────────
        new Tweak
        {
            Id = "cpu.ultimate-power-plan",
            Name = "Plano de energia Desempenho Máximo",
            Description =
                "Cria e ativa o plano 'Desempenho Máximo' (Ultimate Performance), que mantém a CPU em " +
                "frequências altas e elimina micro-latências de economia de energia. Se o Windows não " +
                "permitir, ativa 'Alto desempenho'. Ao reverter, o plano anterior volta a ser o ativo.",
            Benefit = "Resposta mais rápida da CPU e menos variação de frametime em jogos.",
            Category = TweakCategory.Cpu,
            Risk = RiskLevel.Medium, // aumenta consumo e temperatura, principalmente em notebooks
            Impact = ImpactLevel.High,
            Profiles = P(MaxPerformance, Gamer),
            Actions = [new PowerPlanAction()],
        },
    ];
}
