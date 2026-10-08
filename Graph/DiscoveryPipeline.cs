using Ape.Core.Graph;

namespace Ape.Module.DiscoveryManager.Graph;

/// <summary>
/// Decision pipeline. Socket adapters stay outside and only enqueue <see cref="DiscoverySample"/>.
/// HSM: Idle and Stopping run nothing; Running runs normalize → filter → dedupe → emit.
/// </summary>
public static class DiscoveryPipeline
{
    public static FrozenPlan<DiscoveryScratch> Compile(Action<string>? debug = null)
    {
        return new ComponentGraph<DiscoveryScratch>("discovery")
            .Add(new NormalizeStage())
            .Add(new FilterStage(debug))
            .Add(new DedupeStage())
            .Add(new EmitStage())
            .Hsm(h => h
                .State("Idle")
                .State("Running", "normalize", "filter", "dedupe", "emit")
                .State("Stopping")
                .On("Idle", "start", "Running")
                .On("Running", "stop", "Stopping"))
            .Compile();
    }

    private sealed class NormalizeStage : IStage<DiscoveryScratch>
    {
        public string Id => "normalize";

        public void Execute(ref DiscoveryScratch scratch)
        {
            scratch.Work.Clear();
            scratch.Staged.Clear();

            foreach (var sample in scratch.Inbox)
            {
                if (sample.Kind == DiscoverySampleKind.Snapshot)
                {
                    foreach (var url in sample.Locations)
                    {
                        scratch.Work.Add(new DiscoveryWork
                        {
                            Kind = DiscoveryWorkKind.SnapshotMember,
                            Source = sample.Source,
                            Location = url,
                        });
                    }

                    scratch.Work.Add(new DiscoveryWork
                    {
                        Kind = DiscoveryWorkKind.SnapshotCommit,
                        Source = sample.Source,
                    });
                    continue;
                }

                var location = sample.Location;
                if (sample.Source == DiscoverySources.Ssdp && scratch.RewriteLocation != null)
                    location = scratch.RewriteLocation(location, sample.RemoteAddress);

                scratch.Work.Add(new DiscoveryWork
                {
                    Kind = sample.Kind == DiscoverySampleKind.ByeBye
                        ? DiscoveryWorkKind.ByeBye
                        : DiscoveryWorkKind.Alive,
                    Source = sample.Source,
                    Location = location,
                    Usn = sample.Usn,
                    RemoteAddress = sample.RemoteAddress,
                });
            }
        }
    }

    private sealed class FilterStage : IStage<DiscoveryScratch>
    {
        private readonly Action<string>? _debug;

        public FilterStage(Action<string>? debug) => _debug = debug;

        public string Id => "filter";

        public void Execute(ref DiscoveryScratch scratch)
        {
            foreach (var work in scratch.Work)
            {
                if (work.Kind == DiscoveryWorkKind.SnapshotCommit)
                    continue;

                if (!ShouldDrop(scratch, work))
                    continue;

                work.Drop = true;
                _debug?.Invoke($"Discovery filter: dropped {work.Source} {work.Location}");
            }
        }

        private static bool ShouldDrop(DiscoveryScratch scratch, DiscoveryWork work)
        {
            if (string.IsNullOrWhiteSpace(work.Location))
                return true;

            if (!string.IsNullOrEmpty(scratch.LocalUuid) &&
                work.Usn.Contains(scratch.LocalUuid, StringComparison.Ordinal))
                return true;

            if (!Uri.TryCreate(work.Location, UriKind.Absolute, out var uri))
                return false;

            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                uri.Host is "127.0.0.1" or "0.0.0.0" or "::1")
                return true;

            foreach (var ip in scratch.LocalAddresses)
            {
                if (uri.Host.Equals(ip, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    private sealed class DedupeStage : IStage<DiscoveryScratch>
    {
        [Flags]
        private enum SeenFlags
        {
            None = 0,
            Ssdp = 1,
            Mdns = 2,
        }

        private readonly Dictionary<string, SeenFlags> _seen = new(StringComparer.Ordinal);

        public string Id => "dedupe";

        public void Execute(ref DiscoveryScratch scratch)
        {
            var mdnsPresent = new HashSet<string>(StringComparer.Ordinal);
            foreach (var work in scratch.Work)
            {
                switch (work.Kind)
                {
                    case DiscoveryWorkKind.SnapshotMember:
                        if (!work.Drop)
                            mdnsPresent.Add(work.Location);
                        break;
                    case DiscoveryWorkKind.SnapshotCommit:
                        CommitSnapshot(work.Source, mdnsPresent, scratch);
                        mdnsPresent.Clear();
                        break;
                    case DiscoveryWorkKind.Alive when !work.Drop:
                        NoteAlive(work, scratch);
                        break;
                    case DiscoveryWorkKind.ByeBye when !work.Drop:
                        NoteBye(work, scratch);
                        break;
                }
            }
        }

        private void NoteAlive(DiscoveryWork work, DiscoveryScratch scratch)
        {
            _seen.TryGetValue(work.Location, out var flags);
            var previous = flags;
            flags |= Bit(work.Source);
            _seen[work.Location] = flags;
            if (previous != SeenFlags.None)
                return;

            scratch.Staged.Add(new DiscoveryEffect(work.Location, work.Source, work.Usn, Appeared: true));
        }

        private void NoteBye(DiscoveryWork work, DiscoveryScratch scratch)
        {
            if (!_seen.TryGetValue(work.Location, out var flags))
                return;

            flags &= ~Bit(work.Source);
            if (flags == SeenFlags.None)
            {
                _seen.Remove(work.Location);
                scratch.Staged.Add(new DiscoveryEffect(work.Location, work.Source, work.Usn, Appeared: false));
                return;
            }

            _seen[work.Location] = flags;
        }

        private void CommitSnapshot(string source, HashSet<string> present, DiscoveryScratch scratch)
        {
            var bit = Bit(source);
            foreach (var location in present)
            {
                _seen.TryGetValue(location, out var flags);
                var previous = flags;
                flags |= bit;
                _seen[location] = flags;
                if (previous != SeenFlags.None)
                    continue;

                scratch.Staged.Add(new DiscoveryEffect(location, source, "", Appeared: true));
            }

            List<string>? missing = null;
            foreach (var (location, flags) in _seen)
            {
                if ((flags & bit) == 0 || present.Contains(location))
                    continue;
                missing ??= [];
                missing.Add(location);
            }

            if (missing == null)
                return;

            foreach (var location in missing)
            {
                var flags = _seen[location] & ~bit;
                if (flags == SeenFlags.None)
                {
                    _seen.Remove(location);
                    scratch.Staged.Add(new DiscoveryEffect(location, source, "", Appeared: false));
                }
                else
                {
                    _seen[location] = flags;
                }
            }
        }

        private static SeenFlags Bit(string source) =>
            source == DiscoverySources.Mdns ? SeenFlags.Mdns : SeenFlags.Ssdp;
    }

    private sealed class EmitStage : IStage<DiscoveryScratch>
    {
        public string Id => "emit";

        public void Execute(ref DiscoveryScratch scratch)
        {
            scratch.Effects.AddRange(scratch.Staged);
            scratch.Staged.Clear();
        }
    }
}
