using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class FrameResourceRetirementTests
    {
        private sealed class FakeDisposable : IDisposable
        {
            internal int Disposes;
            internal Exception? Error;

            public void Dispose()
            {
                Disposes++;
                if (Error is not null)
                {
                    throw Error;
                }
            }
        }

        [Fact]
        public void NoResources_SucceedsWithoutError()
        {
            Assert.True(FrameResourceRetirement.TryRetireAfterIdle(
                Array.Empty<IDisposable>(), out AggregateException? error));
            Assert.Null(error);
        }

        [Fact]
        public void TwoResources_BothDisposedOnce()
        {
            var first = new FakeDisposable();
            var second = new FakeDisposable();
            Assert.True(FrameResourceRetirement.TryRetireAfterIdle(
                new IDisposable[] { first, second }, out AggregateException? error));
            Assert.Null(error);
            Assert.Equal(1, first.Disposes);
            Assert.Equal(1, second.Disposes);
        }

        [Fact]
        public void FirstThrows_SecondStillDisposed()
        {
            var first = new FakeDisposable { Error = new InvalidOperationException("first") };
            var second = new FakeDisposable();
            Assert.False(FrameResourceRetirement.TryRetireAfterIdle(
                new IDisposable[] { first, second }, out AggregateException? error));
            Assert.NotNull(error);
            Assert.Equal(1, first.Disposes);
            Assert.Equal(1, second.Disposes);
            Assert.Single(error!.InnerExceptions);
            Assert.IsType<InvalidOperationException>(error.InnerExceptions[0]);
        }

        [Fact]
        public void MultipleErrors_AllCollectedInAggregate()
        {
            var first = new FakeDisposable { Error = new InvalidOperationException("first") };
            var middle = new FakeDisposable();
            var last = new FakeDisposable { Error = new ApplicationException("last") };
            Assert.False(FrameResourceRetirement.TryRetireAfterIdle(
                new IDisposable[] { first, middle, last }, out AggregateException? error));
            Assert.NotNull(error);
            Assert.Equal(2, error!.InnerExceptions.Count);
            Assert.Equal(1, first.Disposes);
            Assert.Equal(1, middle.Disposes);
            Assert.Equal(1, last.Disposes);
        }
    }
}
