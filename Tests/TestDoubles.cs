using Ape.Core.Determinism;
using Ape.Core.Logging;
using Ape.Core.Scene.Commit;

namespace Ape.Module.DiscoveryManager.Tests;

internal sealed class ListLogger : ILogger
{
    public List<string> Info { get; } = [];

    public void LogDebug(string message)
    {
    }

    public void LogInfo(string message) => Info.Add(message);

    public void LogWarning(string message)
    {
    }

    public void LogError(string message, Exception? ex = null)
    {
    }
}

internal sealed class MapProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();

    public MapProvider Add<T>(T instance) where T : class
    {
        _services[typeof(T)] = instance;
        return this;
    }

    public object? GetService(Type serviceType) =>
        _services.TryGetValue(serviceType, out var value) ? value : null;
}

internal sealed class RecordingBatch : IFrameCommitBatch
{
    public List<ISceneCommitRequest> Operations { get; } = [];

    public string ParticipantId => "test";

    public void Enqueue(ISceneCommitRequest request) => Operations.Add(request);

    public void Seal()
    {
    }
}
