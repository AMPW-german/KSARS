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
}
