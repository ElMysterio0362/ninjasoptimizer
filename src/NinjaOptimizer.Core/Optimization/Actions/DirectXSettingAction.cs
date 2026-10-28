using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

/// <summary>
/// Define uma opção em "DirectXUserGlobalSettings" (HKCU), uma string no formato "Chave=Valor;Chave=Valor;"
/// que guarda também HDR automático e VRR. Só a opção pedida muda; as demais são preservadas.
/// O snapshot tem o mesmo formato da ação de registro, então a reversão restaura a string inteira.
/// </summary>
public sealed class DirectXSettingAction(string option, string value) : TweakAction
{
    public const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string ValueName = "DirectXUserGlobalSettings";

    public string Option { get; } = option;
    public string Value { get; } = value;

    public override string Kind => RegistryValueAction.KindId;
    public override bool RequiresAdmin => false;
    public override IEnumerable<(RegHive Hive, string Key)> RegistryKeys => [(RegHive.CurrentUser, Key)];
    public override string Describe() => $@"HKCU\{Key} → {ValueName}: {Option}={Value}";

    internal static Dictionary<string, string> Parse(string? raw) =>
        (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Length > 0)
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);

    internal static string Merge(string? raw, string option, string value)
    {
        var tokens = Parse(raw);
        tokens[option] = value;
        return string.Concat(tokens.Select(t => $"{t.Key}={t.Value};"));
    }

    private static string? Read(SystemContext ctx) =>
        ctx.Registry.GetValue(RegHive.CurrentUser, Key, ValueName)?.Data as string;

    public override Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var snap = NewSnapshot();
        snap.Data["hive"] = RegHive.CurrentUser.ToString();
        snap.Data["key"] = Key;
        snap.Data["name"] = ValueName;
        var previous = ctx.Registry.GetValue(RegHive.CurrentUser, Key, ValueName);
        snap.Data["existed"] = (previous is not null).ToString();
        if (previous is not null)
        {
            snap.Data["kind"] = previous.Kind.ToString();
            snap.Data["value"] = previous.Serialize();
        }
        return Task.FromResult(snap);
    }

    public override Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        ctx.Registry.SetValue(RegHive.CurrentUser, Key, ValueName, RegValue.Str(Merge(Read(ctx), Option, Value)));
        return Task.CompletedTask;
    }

    public override Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct) =>
        Task.FromResult(Parse(Read(ctx)).TryGetValue(Option, out var v) && v == Value);
}
