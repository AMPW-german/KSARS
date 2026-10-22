namespace KSARS.Visuals
{
    internal readonly struct SolarFrameToken
    {
        internal SolarFrameToken(object? snapshot, object? viewport, object? camera, object? sun,
            int resourceFrame, int rendererEpoch)
        {
            Snapshot = snapshot;
            Viewport = viewport;
            Camera = camera;
            Sun = sun;
            ResourceFrame = resourceFrame;
            RendererEpoch = rendererEpoch;
        }

        internal object? Snapshot { get; }
        internal object? Viewport { get; }
        internal object? Camera { get; }
        internal object? Sun { get; }
        internal int ResourceFrame { get; }
        internal int RendererEpoch { get; }

        internal bool Matches(in SolarFrameToken other) =>
            Snapshot is not null && Viewport is not null && Camera is not null && Sun is not null
            && ResourceFrame >= 0 && RendererEpoch >= 0
            && ReferenceEquals(Snapshot, other.Snapshot)
            && ReferenceEquals(Viewport, other.Viewport)
            && ReferenceEquals(Camera, other.Camera)
            && ReferenceEquals(Sun, other.Sun)
            && ResourceFrame == other.ResourceFrame
            && RendererEpoch == other.RendererEpoch;

        internal bool CanPresent(in SolarFrameToken current, bool prepared, bool hasPending) =>
            Matches(in current) && prepared && hasPending;

        internal bool Owns(in SolarFrameToken current, bool prepared, bool presented) =>
            Matches(in current) && prepared && presented;
    }

    internal static class MainAtlasSlotGuard
    {
        internal static bool TryFindAcquiredSlot(ReadOnlySpan<nint> fences, ReadOnlySpan<nint> commands,
            nint acquiredFence, nint recordingCommand, out int index, out string reason)
        {
            index = -1;
            reason = "validated";
            if (fences.Length < 1 || fences.Length != commands.Length)
            {
                reason = $"sync ring handle lists empty or mismatched ({fences.Length}/{commands.Length})";
                return false;
            }

            if (acquiredFence == 0 || recordingCommand == 0)
            {
                reason = "acquired frame exposes a zero fence or command buffer handle";
                return false;
            }

            for (int i = 0; i < fences.Length; i++)
            {
                if (fences[i] != acquiredFence || commands[i] != recordingCommand)
                {
                    continue;
                }

                if (index >= 0)
                {
                    reason = "acquired frame pair matches multiple sync ring slots; cannot prove unique slot";
                    return false;
                }

                index = i;
            }

            if (index < 0)
            {
                reason = "acquired fence/command buffer pair not present in the sync ring";
                return false;
            }

            return true;
        }
    }

    internal sealed class MainAtlasOwnership
    {
        private object? _token;
        private readonly HashSet<object> _bodies = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _culledBodies = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _omittedBodies = new(ReferenceEqualityComparer.Instance);

        internal object? CurrentToken => _token;

        internal IEnumerable<object> CulledBodies => _culledBodies;

        internal IEnumerable<object> OmittedBodies => _omittedBodies;

        internal void BeginFrame(object frameToken)
        {
            ArgumentNullException.ThrowIfNull(frameToken);
            _bodies.Clear();
            _culledBodies.Clear();
            _omittedBodies.Clear();
            _token = frameToken;
        }

        internal void MarkPresented(object frameToken, IEnumerable<object> bodies)
        {
            if (!ReferenceEquals(frameToken, _token))
            {
                return;
            }

            _bodies.Clear();
            foreach (object body in bodies)
            {
                _bodies.Add(body);
            }
        }

        internal void MarkCulled(object frameToken, IEnumerable<object> bodies)
        {
            if (frameToken is null || !ReferenceEquals(frameToken, _token))
            {
                return;
            }

            _culledBodies.Clear();
            foreach (object body in bodies)
            {
                _culledBodies.Add(body);
            }
        }

        internal void MarkOmitted(object frameToken, IEnumerable<object> bodies)
        {
            if (frameToken is null || !ReferenceEquals(frameToken, _token))
            {
                return;
            }

            _omittedBodies.Clear();
            foreach (object body in bodies)
            {
                _omittedBodies.Add(body);
            }
        }

        internal bool Owns(object? frameToken, object body) =>
            frameToken is not null && ReferenceEquals(frameToken, _token)
                && (_bodies.Contains(body) || _culledBodies.Contains(body) || _omittedBodies.Contains(body));

        internal bool OwnsCurrent(object body) =>
            _token is not null && (_bodies.Contains(body) || _culledBodies.Contains(body) || _omittedBodies.Contains(body));

        internal void Reset()
        {
            _token = null;
            _bodies.Clear();
            _culledBodies.Clear();
            _omittedBodies.Clear();
        }
    }
}
