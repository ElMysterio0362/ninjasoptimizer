using System.Text.Json;
using System.Text.RegularExpressions;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Debloat;

public sealed record InstalledApp(string Name, string PackageFullName, string PackageFamilyName, bool IsFramework, bool NonRemovable, string SignatureKind);

/// <summary>Lista os apps instalados para o usuário atual (Get-AppxPackage → JSON).</summary>
public static partial class Appx
{
    internal const string ListCommand =
        "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Select-Object Name,PackageFullName,PackageFamilyName,IsFramework,NonRemovable," +
        "@{n='SignatureKind';e={$_.SignatureKind.ToString()}} | ConvertTo-Json -Compress\"";

    public static IReadOnlyList<InstalledApp> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        static string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        static bool B(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
        return items.Select(e => new InstalledApp(S(e, "Name"), S(e, "PackageFullName"), S(e, "PackageFamilyName"),
            B(e, "IsFramework"), B(e, "NonRemovable"), S(e, "SignatureKind"))).ToList();
    }

    /// <summary>Removíveis: sem frameworks, sem não-removíveis, sem pacotes do sistema, sem protegidos, com nomes válidos.</summary>
    public static IReadOnlyList<InstalledApp> Removable(IEnumerable<InstalledApp> apps) =>
        apps.Where(a => !a.IsFramework && !a.NonRemovable && !a.SignatureKind.Equals("System", StringComparison.OrdinalIgnoreCase)
                        && !AppCatalog.IsProtected(a.Name)
                        && AppCatalog.IsValidPackageName(a.Name) && AppCatalog.IsValidPackageName(a.PackageFullName)
                        && AppCatalog.IsValidPackageName(a.PackageFamilyName))
            .DistinctBy(a => a.PackageFullName).ToList();

    public static async Task<IReadOnlyList<InstalledApp>> ListAsync(ICommandRunner commands, CancellationToken ct)
    {
        var r = await commands.RunAsync("powershell", ListCommand, ct);
        if (!r.Success) throw new InvalidOperationException("Não foi possível listar os apps instalados.");
        return Parse(r.StdOut);
    }

    /// <summary>Caminho do manifesto do pacote provisionado (null = não está provisionado).</summary>
    internal static async Task<(string PackageName, string Manifest)?> ProvisionedAsync(ICommandRunner commands, string name, CancellationToken ct)
    {
        if (!AppCatalog.IsValidPackageName(name)) return null;
        var r = await commands.RunAsync("powershell",
            "-NoProfile -NonInteractive -Command \"Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq '" + name +
            "' } | ForEach-Object { \\\"$($_.PackageName)|$($_.InstallLocation)\\\" }\"", ct);
        if (!r.Success) return null;
        var line = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line?.Split('|') is not [var pkg, var loc] || !AppCatalog.IsValidPackageName(pkg) || !IsSafePath(loc)) return null;
        var manifest = loc.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? loc : loc.TrimEnd('\\') + @"\AppXManifest.xml";
        return (pkg, Environment.ExpandEnvironmentVariables(manifest));
    }

    [GeneratedRegex(@"^(%SYSTEMDRIVE%|[A-Za-z]:)\\[A-Za-z0-9 ._()\\~-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SafePath();
    internal static bool IsSafePath(string? p) => p is not null && SafePath().IsMatch(p);
}

/// <summary>
/// Remove um app da Store do usuário atual (opcionalmente também das contas novas). Desfazer reinstala:
/// pacote provisionado → winget (Id da Store) → página do app na Store.
/// </summary>
public sealed class AppxRemoveAction : TweakAction
{
    public const string KindId = "appx-remove";

    public InstalledApp App { get; }
    public bool AlsoProvisioned { get; }
    public string? StoreId { get; }

    public AppxRemoveAction(InstalledApp app, bool alsoProvisioned, string? storeId)
    {
        if (!AppCatalog.IsValidPackageName(app.Name) || !AppCatalog.IsValidPackageName(app.PackageFullName) || !AppCatalog.IsValidPackageName(app.PackageFamilyName))
            throw new ArgumentException("Nome de pacote inválido.", nameof(app));
        if (AppCatalog.IsProtected(app.Name)) throw new ArgumentException($"{app.Name} é protegido e não pode ser removido.", nameof(app));
        App = app;
        AlsoProvisioned = alsoProvisioned;
        StoreId = AppCatalog.IsValidStoreId(storeId) ? storeId : null;
    }

    public override string Kind => KindId;
    public override bool RequiresAdmin => AlsoProvisioned;
    public override string Describe() => $"Remover {App.Name}{(AlsoProvisioned ? " (também para contas novas)" : "")}";

    public override async Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var snap = NewSnapshot();
        snap.Data["name"] = App.Name;
        snap.Data["full"] = App.PackageFullName;
        snap.Data["family"] = App.PackageFamilyName;
        snap.Data["storeId"] = StoreId;
        var prov = await Appx.ProvisionedAsync(ctx.Commands, App.Name, ct);
        snap.Data["provisioned"] = (prov is not null).ToString();
        snap.Data["provisionedRemoved"] = (AlsoProvisioned && prov is not null).ToString();
        return snap;
    }

    public override async Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        var r = await ctx.Commands.RunAsync("powershell",
            $"-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{App.PackageFullName}' -ErrorAction Stop\"", ct);
        if (!r.Success) throw new InvalidOperationException($"O Windows não removeu {App.Name}. {r.StdErr}".Trim());
        if (AlsoProvisioned && snapshot.Get("provisioned") == "True" && await Appx.ProvisionedAsync(ctx.Commands, App.Name, ct) is { } prov)
        {
            var p = await ctx.Commands.RunAsync("powershell",
                $"-NoProfile -NonInteractive -Command \"Remove-AppxProvisionedPackage -Online -PackageName '{prov.PackageName}' -ErrorAction Stop\"", ct);
            if (!p.Success) snapshot.Data["provisionedRemoved"] = "False"; // removido só da sua conta
        }
    }

    public override async Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct)
    {
        try { return (await Appx.ListAsync(ctx.Commands, ct)).All(a => !a.Name.Equals(App.Name, StringComparison.OrdinalIgnoreCase)); }
        catch (InvalidOperationException) { return false; }
    }

    public static async Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var name = s.Get("name");
        var family = s.Get("family");
        if (!AppCatalog.IsValidPackageName(name) || !AppCatalog.IsValidPackageName(family))
            throw new InvalidOperationException("Registro do histórico com nome de pacote inválido.");
        // Já voltou (reinstalado pelo usuário)? Nada a fazer.
        try { if ((await Appx.ListAsync(ctx.Commands, ct)).Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return; }
        catch (InvalidOperationException) { }

        // 1) Ainda provisionado no sistema: registra de novo para esta conta.
        if (await Appx.ProvisionedAsync(ctx.Commands, name!, ct) is { } prov)
        {
            var r = await ctx.Commands.RunAsync("powershell",
                $"-NoProfile -NonInteractive -Command \"Add-AppxPackage -Register '{prov.Manifest}' -DisableDevelopmentMode -ErrorAction Stop\"", ct);
            if (r.Success) return;
        }
        // 2) Id da Store conhecido: winget.
        if (s.Get("storeId") is { } id && AppCatalog.IsValidStoreId(id))
        {
            var r = await ctx.Commands.RunAsync("winget",
                $"install --id {id} --source msstore --accept-package-agreements --accept-source-agreements --silent", ct);
            if (r.Success) return;
        }
        // 3) Abre a página na Store: a reinstalação fica com o usuário.
        await ctx.Commands.RunAsync("explorer.exe", $"ms-windows-store://pdp/?PFN={family}", ct);
        throw new InvalidOperationException("Abrimos a página do app na Microsoft Store: clique em Instalar por lá. Depois, tente Desfazer de novo para tirar do histórico.");
    }
}

/// <summary>OneDrive não é app da Store: desinstala/reinstala pelo winget.</summary>
public sealed class OneDriveRemoveAction : TweakAction
{
    public const string KindId = "onedrive-remove";
    private const string Id = "Microsoft.OneDrive";

    public override string Kind => KindId;
    public override string Describe() => "Desinstalar o OneDrive (winget)";

    public static async Task<bool> IsInstalledAsync(ICommandRunner c, CancellationToken ct) =>
        (await c.RunAsync("winget", $"list --id {Id} --exact --accept-source-agreements", ct)).Success;

    public override Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct) => Task.FromResult(NewSnapshot());

    public override async Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        var r = await ctx.Commands.RunAsync("winget", $"uninstall --id {Id} --exact --silent --accept-source-agreements", ct);
        if (!r.Success) throw new InvalidOperationException($"O winget não desinstalou o OneDrive (código {r.ExitCode}).");
    }

    public override async Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct) => !await IsInstalledAsync(ctx.Commands, ct);

    public static async Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        if (await IsInstalledAsync(ctx.Commands, ct)) return;
        var r = await ctx.Commands.RunAsync("winget", $"install --id {Id} --exact --silent --accept-package-agreements --accept-source-agreements", ct);
        if (!r.Success) throw new InvalidOperationException($"O winget não reinstalou o OneDrive (código {r.ExitCode}). Precisa de internet.");
    }
}

/// <summary>Monta os ajustes de remoção (um por app), com risco e consequências do catálogo.</summary>
public static class DebloatTweaks
{
    public const string IdPrefix = "debloat:";

    public static RiskLevel RiskOf(AppVerdict v, bool alsoProvisioned) => alsoProvisioned ? RiskLevel.High : v switch
    {
        AppVerdict.Safe => RiskLevel.Low,
        AppVerdict.Caution => RiskLevel.Medium,
        _ => RiskLevel.High,
    };

    public static Tweak Remove(InstalledApp app, bool alsoProvisioned)
    {
        var entry = AppCatalog.Find(app.Name);
        var verdict = entry?.Verdict ?? AppVerdict.Unknown;
        var consequences = (entry?.Consequences ?? AppCatalog.UnknownConsequences)
            .Concat(alsoProvisioned ? AppCatalog.ForNewAccountsConsequences : [])
            .Append("Desfazer reinstala o app (precisa de internet).").ToList();
        return new Tweak
        {
            Id = IdPrefix + app.Name.ToLowerInvariant(),
            Name = $"Remover app: {entry?.FriendlyName ?? app.Name}",
            Description = $"Remove o pacote {app.Name} da sua conta.",
            Benefit = "Menos apps em segundo plano e no menu Iniciar.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskOf(verdict, alsoProvisioned),
            Impact = ImpactLevel.Low,
            Consequences = consequences,
            Actions = [new AppxRemoveAction(app, alsoProvisioned, entry?.StoreId)],
        };
    }

    public static Tweak RemoveOneDrive() => new()
    {
        Id = IdPrefix + "onedrive",
        Name = "Remover app: OneDrive",
        Description = "Desinstala o OneDrive deste PC.",
        Benefit = "Um programa a menos sincronizando em segundo plano.",
        Category = TweakCategory.SystemPrivacy,
        Risk = RiskLevel.Medium,
        Impact = ImpactLevel.Low,
        Consequences =
        [
            "Para de sincronizar seus arquivos.",
            "Arquivos que estão só na nuvem (\"sob demanda\") deixam de aparecer no PC; eles continuam no site do OneDrive.",
            "Pastas como Documentos e Área de Trabalho podem estar dentro do OneDrive: confira antes.",
            "Desfazer reinstala o OneDrive (precisa de internet).",
        ],
        Actions = [new OneDriveRemoveAction()],
    };
}
