using System.Text.RegularExpressions;
using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

/// <summary>
/// Muda um valor da entrada de boot atual (bcdedit /set {current}). O snapshot guarda o valor anterior;
/// sem valor antes, a reversão usa /deletevalue. A saída do bcdedit é traduzida, mas o nome do campo e
/// os valores (Auto/Off) não são.
/// </summary>
public sealed partial class BcdSettingAction : TweakAction
{
    public const string KindId = "bcd";

    [GeneratedRegex(@"^[A-Za-z]+$")]
    private static partial Regex Word();

    public string Element { get; }
    public string Value { get; }
    private readonly string _label;

    public BcdSettingAction(string element, string value, string label)
    {
        if (!Word().IsMatch(element) || !Word().IsMatch(value)) throw new ArgumentException("Elemento ou valor do BCD inválido.");
        Element = element;
        Value = value;
        _label = label;
    }

    public override string Kind => KindId;
    public override bool RequiresAdmin => true;
    public override string Describe() => $"Boot do Windows: {Element} = {Value} ({_label})";

    /// <summary>Valor atual do elemento na saída de "bcdedit /enum {current}" (null = não definido).</summary>
    internal static string? Parse(string output, string element)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith(element, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = t[element.Length..];
            if (rest.Length == 0 || !char.IsWhiteSpace(rest[0])) continue; // outro campo com o mesmo começo
            var v = rest.Trim();
            return v.Length == 0 ? null : v;
        }
        return null;
    }

    private static async Task<string?> ReadAsync(SystemContext ctx, string element, CancellationToken ct)
    {
        var r = await ctx.Commands.RunAsync("bcdedit", "/enum {current}", ct);
        if (!r.Success) throw new InvalidOperationException("Não foi possível ler a configuração de boot (bcdedit). Precisa de administrador.");
        return Parse(r.StdOut, element);
    }

    public override async Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var snap = NewSnapshot();
        snap.Data["element"] = Element;
        snap.Data["prev"] = await ReadAsync(ctx, Element, ct);
        return snap;
    }

    public override async Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        var r = await ctx.Commands.RunAsync("bcdedit", $"/set {{current}} {Element} {Value}", ct);
        if (!r.Success) throw new InvalidOperationException($"O bcdedit recusou a mudança: {r.StdOut} {r.StdErr}".Trim());
    }

    public override async Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct)
    {
        try { return string.Equals(await ReadAsync(ctx, Element, ct), Value, StringComparison.OrdinalIgnoreCase); }
        catch (InvalidOperationException) { return false; }
    }

    public static async Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        var element = s.Get("element");
        var prev = s.Get("prev");
        // O journal fica em disco: confere de novo antes de passar ao bcdedit.
        if (element is null || !Word().IsMatch(element) || (prev is not null && !Word().IsMatch(prev)))
            throw new InvalidOperationException("Registro do histórico com valor de boot inválido.");
        var args = prev is null ? $"/deletevalue {{current}} {element}" : $"/set {{current}} {element} {prev}";
        var r = await ctx.Commands.RunAsync("bcdedit", args, ct);
        if (!r.Success) throw new InvalidOperationException($"O bcdedit não restaurou {element}: {r.StdOut} {r.StdErr}".Trim());
    }
}
