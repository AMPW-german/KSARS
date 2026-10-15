namespace KSARS.Visuals
{
    internal static class FrameResourceRetirement
    {
        internal static bool TryRetireAfterIdle(IEnumerable<IDisposable> resources, out AggregateException? error)
        {
            List<Exception>? errors = null;
            foreach (IDisposable resource in resources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }
            }

            error = errors is null ? null : new AggregateException(errors);
            return error is null;
        }
    }
}
