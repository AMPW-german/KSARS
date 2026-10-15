namespace KSARS.Visuals
{
    internal sealed class ProbeCaptureGeneration
    {
        internal const int MaxGenerations = 3;

        internal int Generation { get; private set; }
        internal bool Queued { get; private set; }
        internal bool BudgetExhausted => Generation + 1 >= MaxGenerations;

        internal bool TryQueue()
        {
            if (BudgetExhausted)
            {
                return false;
            }

            Queued = true;
            return true;
        }

        internal bool TryConsume(bool hasValidSnapshot)
        {
            if (!Queued || !hasValidSnapshot)
            {
                return false;
            }

            Queued = false;
            if (BudgetExhausted)
            {
                return false;
            }

            Generation++;
            return true;
        }

        internal void Reset()
        {
            Generation = 0;
            Queued = false;
        }
    }
}
