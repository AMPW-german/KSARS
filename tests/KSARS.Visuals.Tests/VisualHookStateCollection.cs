using Xunit;

namespace KSARS.Visuals.Tests
{
    // Serializes tests that share process-global hook/capture state (Harmony
    // patches, RenderHooks status fields, StarCatalog store) with everything else.
    [CollectionDefinition("VisualHookState", DisableParallelization = true)]
    public class VisualHookStateCollection { }
}
