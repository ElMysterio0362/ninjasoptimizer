using System.Management;
using NinjasOptimizer.Core.Abstractions;

namespace NinjasOptimizer.Windows.Platform;

/// <summary>
/// Cria pontos de restauração via WMI (root\default:SystemRestore.CreateRestorePoint).
/// O Windows limita a 1 ponto a cada 24h por padrão; liberamos o limite temporariamente
/// e restauramos o valor original logo em seguida.
/// </summary>
public sealed class WmiRestorePointService(IRegistryAccess registry) : IRestorePointService
{
    private const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValue = "SystemRestorePointCreationFrequency";
    private const uint ModifySettings = 12, BeginSystemChange = 100;

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default) => Task.Run(() =>
    {
        var previous = registry.GetValue(RegHive.LocalMachine, Key, FrequencyValue);
        try
        {
            registry.SetValue(RegHive.LocalMachine, Key, FrequencyValue, RegValue.Dword(0));
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            var code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);
            return code == 0
                ? new RestorePointResult(true, "Ponto de restauração criado.")
                : new RestorePointResult(false, code == 1058
                    ? "A Proteção do Sistema está desativada na unidade do Windows. Ative-a em 'Criar um ponto de restauração'."
                    : $"O Windows retornou o código {code} ao criar o ponto de restauração.");
        }
        catch (Exception ex)
        {
            return new RestorePointResult(false, ex.Message);
        }
        finally
        {
            try
            {
                if (previous is null) registry.DeleteValue(RegHive.LocalMachine, Key, FrequencyValue);
                else registry.SetValue(RegHive.LocalMachine, Key, FrequencyValue, previous);
            }
            catch { /* melhor esforço */ }
        }
    }, ct);
}
