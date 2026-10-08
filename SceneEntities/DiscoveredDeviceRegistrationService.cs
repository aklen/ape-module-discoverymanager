using Ape.Core.Logging;
using Ape.Core.Runtime.Service;
using Ape.Core.Scene;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Module.DiscoveryManager.SceneEntities;

/// <summary>
/// Registers <see cref="DiscoveredDevice"/> so a host frame can commit it by type id.
/// </summary>
public sealed class DiscoveredDeviceRegistrationService : IPluggableService
{
    public string ServiceId => "discovery-scene-entity";
    public string Name => "Discovery scene entity";

    public void Register(IServiceCollection serviceCollection)
    {
    }

    public void Initialize(IServiceProvider services)
    {
        var registry = services.GetRequiredService<ISceneEntityRegistry>();
        var logger = services.GetRequiredService<ILogger>();
        registry.Register(
            DiscoverySceneEntityTypeIds.Device,
            (_, id, _) => new DiscoveredDevice { Id = id });
        logger.LogDebug("Discovery module: registered DiscoveredDevice scene entity factory.");
    }

    public void Start(CancellationToken cancellationToken)
    {
    }

    public void Stop()
    {
    }
}
