using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    [Collection("VisualHookState")]
    public class StarCatalogTests
    {
        private static readonly OpticalVector XAxis = new(2.0, 0.0, 0.0);
        private static readonly OpticalVector YAxis = new(0.0, -4.0, 0.0);
        private static readonly OpticalVector ZAxis = new(0.0, 0.0, 0.5);

        public StarCatalogTests() => StarCatalog.Reset();

        private static void Commit(object source, object technique, params (OpticalVector Direction, byte Scale, uint Rgb)[] rows)
        {
            var scope = StarCatalog.Begin(source, technique);
            Assert.NotNull(scope);
            foreach (var row in rows)
            {
                scope!.Add(row.Direction, row.Scale, row.Rgb);
            }
            StarCatalog.Complete(scope, success: true);
        }

        [Fact]
        public void SameSourceReload_ReplacesPreviousBatchAtomically()
        {
            var technique = new object();
            var source = new object();
            Commit(source, technique, (XAxis, 10, 0x112233u), (YAxis, 11, 0x445566u));

            Commit(source, technique, (ZAxis, 3, 0xffffffu));

            var snapshot = StarCatalog.Snapshot(technique);
            var entry = Assert.Single(snapshot!);
            Assert.Equal(3, entry.Scale);
            Assert.Equal(0xffffffu, entry.PackedRgb);
            Assert.Equal(0.0, entry.Direction.X, 12);
            Assert.Equal(0.0, entry.Direction.Y, 12);
            Assert.Equal(1.0, entry.Direction.Z, 12);
        }

        [Fact]
        public void DistinctSources_AreRetainedAndConcatenateInCommitOrder()
        {
            var technique = new object();
            var first = new object();
            var second = new object();

            Commit(first, technique, (XAxis, 1, 0x010101u));
            Commit(second, technique, (YAxis, 2, 0x020202u), (ZAxis, 3, 0x030303u));

            var snapshot = StarCatalog.Snapshot(technique)!;
            Assert.Equal(3, snapshot.Length);
            Assert.Equal(new byte[] { 1, 2, 3 }, snapshot.Select(e => e.Scale).ToArray());
            Assert.Equal(new[] { 0x010101u, 0x020202u, 0x030303u }, snapshot.Select(e => e.PackedRgb).ToArray());
        }

        [Fact]
        public void CoincidentEntries_AreNotDeduplicated()
        {
            var technique = new object();
            var source = new object();
            Commit(source, technique, (XAxis, 7, 0xabcdefu), (XAxis, 7, 0xabcdefu), (XAxis, 8, 0xabcdefu));

            var snapshot = StarCatalog.Snapshot(technique)!;
            Assert.Equal(3, snapshot.Length);
            Assert.Equal(new byte[] { 7, 7, 8 }, snapshot.Select(e => e.Scale).ToArray());
        }

        [Fact]
        public void TechniqueChange_ResetsPriorSources()
        {
            var oldTechnique = new object();
            var newTechnique = new object();
            var firstSource = new object();
            var secondSource = new object();
            Commit(firstSource, oldTechnique, (XAxis, 1, 0x111111u));
            Commit(secondSource, newTechnique, (YAxis, 2, 0x222222u));

            Assert.Null(StarCatalog.Snapshot(oldTechnique));
            var snapshot = StarCatalog.Snapshot(newTechnique)!;
            var entry = Assert.Single(snapshot);
            Assert.Equal(2, entry.Scale);
        }

        [Fact]
        public void FailedBatch_IsNotPublished_AndOldSnapshotRetained()
        {
            var technique = new object();
            var source = new object();
            Commit(source, technique, (XAxis, 5, 0x777777u));

            var scope = StarCatalog.Begin(source, technique)!;
            scope.Add(YAxis, 9, 0x999999u);
            scope.Add(OpticalVector.Zero, 9, 0x999999u);
            StarCatalog.Complete(scope, success: true);

            var snapshot = StarCatalog.Snapshot(technique)!;
            var entry = Assert.Single(snapshot);
            Assert.Equal(5, entry.Scale);
            Assert.NotNull(StarCatalog.Failure);
        }

        [Fact]
        public void AbortedNativeLoader_DiscardsPartialBatch()
        {
            var technique = new object();
            var source = new object();
            Commit(source, technique, (XAxis, 5, 0x777777u));

            var scope = StarCatalog.Begin(source, technique)!;
            scope.Add(YAxis, 9, 0x999999u);
            StarCatalog.Complete(scope, success: false);

            var snapshot = StarCatalog.Snapshot(technique)!;
            var entry = Assert.Single(snapshot);
            Assert.Equal(5, entry.Scale);
            Assert.NotNull(StarCatalog.Failure);
        }

        [Fact]
        public void NestedScopes_RestorePreviousScope()
        {
            var technique = new object();
            var outerSource = new object();
            var innerSource = new object();

            var outer = StarCatalog.Begin(outerSource, technique)!;
            var inner = StarCatalog.Begin(innerSource, technique)!;
            Assert.Same(outer, inner.Previous);
            Assert.Same(inner, StarCatalog.Current);

            inner.Add(XAxis, 1, 0x101010u);
            StarCatalog.Complete(inner, success: true);
            Assert.Same(outer, StarCatalog.Current);

            outer.Add(YAxis, 2, 0x202020u);
            StarCatalog.Complete(outer, success: true);
            Assert.Null(StarCatalog.Current);

            var snapshot = StarCatalog.Snapshot(technique)!;
            Assert.Equal(new byte[] { 1, 2 }, snapshot.Select(e => e.Scale).ToArray());
        }

        [Fact]
        public void Reset_RejectsInFlightScope()
        {
            var technique = new object();
            var source = new object();
            var scope = StarCatalog.Begin(source, technique)!;

            StarCatalog.Reset();
            scope.Add(XAxis, 5, 0x505050u);
            StarCatalog.Complete(scope, success: true);

            Assert.Null(StarCatalog.Snapshot(technique));
            Assert.NotNull(StarCatalog.Failure);
        }

        [Fact]
        public void Snapshot_ReturnsDefensiveCopy()
        {
            var technique = new object();
            var source = new object();
            Commit(source, technique, (XAxis, 5, 0x777777u));

            var first = StarCatalog.Snapshot(technique)!;
            var second = StarCatalog.Snapshot(technique)!;
            Assert.NotSame(first, second);
            Assert.Equal(first, second);

            first[0] = new StarCatalog.Entry(OpticalVector.Zero, 0, 0u);
            var fresh = StarCatalog.Snapshot(technique)!;
            Assert.Equal(5, fresh[0].Scale);
            Assert.Equal(0x777777u, fresh[0].PackedRgb);
        }

        [Fact]
        public void InvalidDirection_FailsWholeBatch()
        {
            var technique = new object();
            var source = new object();

            var scope = StarCatalog.Begin(source, technique)!;
            scope.Add(XAxis, 1, 0x010101u);
            scope.Add(OpticalVector.Zero, 2, 0x020202u);
            Assert.True(scope.Failed);
            StarCatalog.Complete(scope, success: true);

            var nanScope = StarCatalog.Begin(source, technique)!;
            nanScope.Add(new OpticalVector(double.NaN, 0.0, 0.0), 3, 0x030303u);
            Assert.True(nanScope.Failed);
            StarCatalog.Complete(nanScope, success: true);

            Assert.Null(StarCatalog.Snapshot(technique));
            Assert.NotNull(StarCatalog.Failure);
        }

        [Fact]
        public void NullInputs_DisableTransactionWithoutTouchingStore()
        {
            var technique = new object();
            Assert.Null(StarCatalog.Begin(null, technique));
            Assert.Null(StarCatalog.Begin(new object(), null));
            StarCatalog.Complete(null, success: true);

            Assert.Null(StarCatalog.Snapshot(technique));
            Assert.Null(StarCatalog.Snapshot(null));
            Assert.Null(StarCatalog.Failure);
        }

        [Fact]
        public void Snapshot_MismatchedTechniqueReference_ReturnsNull()
        {
            var technique = new object();
            var source = new object();
            Commit(source, technique, (XAxis, 4, 0x444444u));

            Assert.Null(StarCatalog.Snapshot(new object()));
            Assert.Single(StarCatalog.Snapshot(technique)!);
        }

        [Fact]
        public void Complete_ReportsCommitOutcome()
        {
            var technique = new object();
            var source = new object();

            var scope = StarCatalog.Begin(source, technique)!;
            scope.Add(XAxis, 1, 0x101010u);
            Assert.True(StarCatalog.Complete(scope, success: true));

            var invalid = StarCatalog.Begin(source, technique)!;
            invalid.Add(OpticalVector.Zero, 2, 0x202020u);
            Assert.False(StarCatalog.Complete(invalid, success: true));

            var aborted = StarCatalog.Begin(source, technique)!;
            aborted.Add(YAxis, 3, 0x303030u);
            Assert.False(StarCatalog.Complete(aborted, success: false));
        }

        [Fact]
        public void DuplicateComplete_CannotReplaceNewerBatch()
        {
            var technique = new object();
            var source = new object();

            var first = StarCatalog.Begin(source, technique)!;
            first.Add(XAxis, 1, 0x111111u);
            Assert.True(StarCatalog.Complete(first, success: true));

            Commit(source, technique, (YAxis, 2, 0x222222u));

            Assert.False(StarCatalog.Complete(first, success: true));
            var snapshot = StarCatalog.Snapshot(technique)!;
            var entry = Assert.Single(snapshot);
            Assert.Equal(2, entry.Scale);
            Assert.Equal(0x222222u, entry.PackedRgb);
        }

        [Fact]
        public void OutOfOrderComplete_RejectsWithoutPublication()
        {
            var technique = new object();
            var outerSource = new object();
            var innerSource = new object();

            var outer = StarCatalog.Begin(outerSource, technique)!;
            var inner = StarCatalog.Begin(innerSource, technique)!;

            outer.Add(XAxis, 1, 0x111111u);
            Assert.False(StarCatalog.Complete(outer, success: true));
            Assert.False(outer.Completed);
            Assert.Same(inner, StarCatalog.Current);
            Assert.Null(StarCatalog.Snapshot(technique));

            inner.Add(YAxis, 2, 0x222222u);
            Assert.True(StarCatalog.Complete(inner, success: true));
            Assert.Same(outer, StarCatalog.Current);
            Assert.True(StarCatalog.Complete(outer, success: true));
            Assert.Null(StarCatalog.Current);

            var snapshot = StarCatalog.Snapshot(technique)!;
            Assert.Equal(new byte[] { 2, 1 }, snapshot.Select(e => e.Scale).ToArray());
        }
    }
}
