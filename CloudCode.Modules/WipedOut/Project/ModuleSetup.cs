using Unity.Services.CloudCode.Apis.Extensions;
using Unity.Services.CloudCode.Core;

namespace WipedOut;

public class ModuleSetup : ICloudCodeSetup
{
    public void Setup(ICloudCodeConfig config)
    {
        // Da acceso a Cloud Save desde el módulo. IPushClient (mensajes push) ya viene
        // registrado por defecto con estas versiones de Core y Apis.
        config.AddGameApiClient();
    }
}
