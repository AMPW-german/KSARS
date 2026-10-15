namespace KSARS.Visuals
{
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
}
