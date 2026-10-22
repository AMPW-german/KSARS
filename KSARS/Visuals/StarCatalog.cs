namespace KSARS.Visuals
{
    // Read-only bookkeeping for the authoritative star catalog stream observed at
    // KSA.Mod::LoadStarBinaries. No file I/O, no GPU work, no color transform:
    // raw direction/scale/RGB records are kept per source mod under the current
    // star technique until a rendering consumer is validated.
    internal static class StarCatalog
    {
        internal readonly record struct Entry(OpticalVector Direction, byte Scale, uint PackedRgb);

        internal sealed class CaptureScope
        {
            internal CaptureScope(object source, object technique, int epoch, CaptureScope? previous)
            {
                Source = source;
                Technique = technique;
                Epoch = epoch;
                Previous = previous;
            }

            internal object Source { get; }
            internal object Technique { get; }
            internal int Epoch { get; }
            internal CaptureScope? Previous { get; }
            internal bool Failed { get; set; }
            internal bool Completed { get; set; }
            internal List<Entry> Entries { get; } = new();

            // Coincident records are legitimate; nothing is deduplicated.
            internal void Add(OpticalVector direction, byte scale, uint packedRgb)
            {
                if (!RelativisticOptics.TryNormalize(direction, out OpticalVector unit))
                {
                    Failed = true;
                    return;
                }

                Entries.Add(new Entry(unit, scale, packedRgb));
            }
        }

        private static readonly object s_lock = new();
        private static List<KeyValuePair<object, Entry[]>> s_sources = new();
        private static object? s_technique;
        private static int s_epoch;
        private static int s_revision;
        private static string? s_failure;

        [ThreadStatic]
        private static CaptureScope? s_current;

        internal static CaptureScope? Current => s_current;

        // Monotonic publication counter; consumers rebuild GPU-side copies when it
        // changes. Bumped once per committed batch, including empty ones.
        internal static int Revision
        {
            get
            {
                lock (s_lock)
                {
                    return s_revision;
                }
            }
        }

        internal static string? Failure
        {
            get
            {
                lock (s_lock)
                {
                    return s_failure;
                }
            }
        }

        internal static CaptureScope? Begin(object? source, object? technique)
        {
            if (source is null || technique is null)
            {
                return null;
            }

            int epoch;
            lock (s_lock)
            {
                epoch = s_epoch;
            }

            var scope = new CaptureScope(source, technique, epoch, s_current);
            s_current = scope;
            return scope;
        }

        // Returns true only when the batch was published. Once Completed a scope
        // can never republish; completing anything but the live top-of-stack scope
        // is rejected without publication (the scope stays open for its proper turn).
        internal static bool Complete(CaptureScope? scope, bool success)
        {
            if (scope is null || scope.Completed)
            {
                return false;
            }

            if (s_current != scope)
            {
                lock (s_lock)
                {
                    if (scope.Epoch != s_epoch)
                    {
                        scope.Completed = true;
                        s_failure = "star catalog batch discarded: stale capture scope";
                    }
                    else
                    {
                        s_failure = "star catalog batch rejected: out-of-order capture scope";
                    }
                }
                return false;
            }

            s_current = scope.Previous;
            scope.Completed = true;

            if (!success || scope.Failed)
            {
                lock (s_lock)
                {
                    s_failure = scope.Failed
                        ? "star catalog batch discarded: invalid record"
                        : "star catalog batch discarded: native loader aborted";
                }
                return false;
            }

            lock (s_lock)
            {
                if (scope.Epoch != s_epoch)
                {
                    s_failure = "star catalog batch discarded: stale capture scope";
                    return false;
                }

                if (!ReferenceEquals(s_technique, scope.Technique))
                {
                    s_technique = scope.Technique;
                    s_sources = new List<KeyValuePair<object, Entry[]>>();
                }

                var batch = scope.Entries.ToArray();
                int index = s_sources.FindIndex(pair => ReferenceEquals(pair.Key, scope.Source));
                var replacement = new KeyValuePair<object, Entry[]>(scope.Source, batch);
                if (index >= 0)
                {
                    s_sources[index] = replacement;
                }
                else
                {
                    s_sources.Add(replacement);
                }

                s_revision++;
                return true;
            }
        }

        // Defensive copy in native call order; only for the live technique reference.
        internal static Entry[]? Snapshot(object? technique)
        {
            lock (s_lock)
            {
                if (technique is null || !ReferenceEquals(s_technique, technique))
                {
                    return null;
                }

                var copy = new List<Entry>();
                foreach (KeyValuePair<object, Entry[]> pair in s_sources)
                {
                    copy.AddRange(pair.Value);
                }

                return copy.ToArray();
            }
        }

        internal static void Reset()
        {
            lock (s_lock)
            {
                s_epoch++;
                // Bump the revision so GPU consumers holding a snapshot cannot
                // mistake a cleared catalog for an unchanged one.
                s_revision++;
                s_technique = null;
                s_sources = new List<KeyValuePair<object, Entry[]>>();
                s_failure = null;
            }

            s_current = null;
        }

        internal static string Describe()
        {
            lock (s_lock)
            {
                int count = 0;
                foreach (KeyValuePair<object, Entry[]> pair in s_sources)
                {
                    count += pair.Value.Length;
                }

                string state = s_failure is null ? "raw" : $"degraded: {s_failure}";
                return $"{count} raw records across {s_sources.Count} source(s); {state}";
            }
        }
    }
}
