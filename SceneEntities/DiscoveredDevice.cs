using MessagePack;
using Ape.Core.Scene.Models;

namespace Ape.Module.DiscoveryManager.SceneEntities;

/// <summary>
/// One remote device observed by discovery. <see cref="Present"/> is false after bye-bye or a missing mDNS poll.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
public partial class DiscoveredDevice : Entity
{
    public DiscoveredDevice() : base(DiscoverySceneEntityTypeIds.Device)
    {
    }

    [Key(10)]
    public string Location { get; set; } = "";

    [Key(11)]
    public string Source { get; set; } = "";

    [Key(12)]
    public string Usn { get; set; } = "";

    [Key(13)]
    public bool Present { get; set; }

    [Key(14)]
    public DateTime LastSeenUtc { get; set; }
}
