using NinjaOptimizer.Core.Abstractions;

namespace NinjaOptimizer.Core.Optimization.Actions;

/// <summary>Muda o tipo de inicialização de um serviço (e opcionalmente o para agora).</summary>
public sealed class ServiceStartupAction(string serviceName, ServiceStartMode mode, bool stopNow = true) : TweakAction
{
    public const string KindId = "service";

    public string ServiceName { get; } = serviceName;
    public ServiceStartMode Mode { get; } = mode;
    public bool StopNow { get; } = stopNow;

    public override string Kind => KindId;

    public override string Describe() => $"Serviço {ServiceName} → inicialização {Mode}{(StopNow ? " e parar agora" : "")}";

    public override Task<ActionSnapshot> CaptureAsync(SystemContext ctx, CancellationToken ct)
    {
        var snap = NewSnapshot();
        snap.Data["service"] = ServiceName;
        var exists = ctx.Services.Exists(ServiceName);
        snap.Data["exists"] = exists.ToString();
        if (exists)
        {
            snap.Data["mode"] = ctx.Services.GetStartMode(ServiceName).ToString();
            snap.Data["running"] = ctx.Services.IsRunning(ServiceName).ToString();
        }
        return Task.FromResult(snap);
    }

    public override Task ApplyAsync(SystemContext ctx, ActionSnapshot snapshot, CancellationToken ct)
    {
        // Serviço ausente nesta edição do Windows: nada a fazer (e nada a reverter).
        if (!ctx.Services.Exists(ServiceName)) return Task.CompletedTask;
        ctx.Services.SetStartMode(ServiceName, Mode);
        if (StopNow && Mode == ServiceStartMode.Disabled && ctx.Services.IsRunning(ServiceName))
            ctx.Services.Stop(ServiceName);
        return Task.CompletedTask;
    }

    public override Task<bool> IsAppliedAsync(SystemContext ctx, CancellationToken ct) =>
        Task.FromResult(!ctx.Services.Exists(ServiceName) || ctx.Services.GetStartMode(ServiceName) == Mode);

    public static Task RevertAsync(SystemContext ctx, ActionSnapshot s, CancellationToken ct)
    {
        if (!bool.Parse(s.Get("exists") ?? "false")) return Task.CompletedTask;
        var name = s.Get("service")!;
        if (!ctx.Services.Exists(name)) return Task.CompletedTask;
        ctx.Services.SetStartMode(name, Enum.Parse<ServiceStartMode>(s.Get("mode")!));
        if (bool.Parse(s.Get("running") ?? "false") && !ctx.Services.IsRunning(name))
            ctx.Services.Start(name);
        return Task.CompletedTask;
    }
}
