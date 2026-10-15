namespace KSARS.Visuals
{
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
