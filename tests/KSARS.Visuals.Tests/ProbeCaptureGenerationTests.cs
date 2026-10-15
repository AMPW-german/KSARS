using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class ProbeCaptureGenerationTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");
        private const string CoordinatorType = "KSARS.Visuals.RelativisticSpaceRenderer";

        private static List<(string DeclaringType, string Name)> CallsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
                    && i.Operand is MethodReference)
                .Select(i =>
                {
                    var mr = (MethodReference)i.Operand;
                    return (mr.DeclaringType.FullName, mr.Name);
                })
                .ToList();

        private static TypeDefinition TypeOf(ModuleDefinition module, string name)
        {
            var type = module.GetType(name);
            Assert.NotNull(type);
            return type;
        }

        private static int CountCalls(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.Count(c => c.DeclaringType == declaringType && c.Name == name);

        [Fact]
        public void Generation_QueueCoalescesAndConsumesOnlyWithValidSnapshot()
        {
            var generation = new ProbeCaptureGeneration();
            Assert.Equal(0, generation.Generation);
            Assert.False(generation.BudgetExhausted);

            Assert.True(generation.TryQueue());
            Assert.True(generation.TryQueue());
            Assert.True(generation.Queued);

            Assert.False(generation.TryConsume(hasValidSnapshot: false));
            Assert.True(generation.Queued);
            Assert.Equal(0, generation.Generation);

            Assert.True(generation.TryConsume(hasValidSnapshot: true));
            Assert.False(generation.Queued);
            Assert.Equal(1, generation.Generation);
        }

        [Fact]
        public void Generation_BudgetStopsAtThreeGenerations()
        {
            var generation = new ProbeCaptureGeneration();
            Assert.True(generation.TryQueue());
            Assert.True(generation.TryConsume(hasValidSnapshot: true));
            Assert.Equal(1, generation.Generation);

            Assert.True(generation.TryQueue());
            Assert.True(generation.TryConsume(hasValidSnapshot: true));
            Assert.Equal(2, generation.Generation);

            Assert.True(generation.BudgetExhausted);
            Assert.False(generation.TryQueue());
            Assert.False(generation.Queued);
            Assert.False(generation.TryConsume(hasValidSnapshot: true));
            Assert.Equal(2, generation.Generation);
            Assert.Equal(3, ProbeCaptureGeneration.MaxGenerations);
        }

        [Fact]
        public void Generation_ResetRestoresInitialState()
        {
            var generation = new ProbeCaptureGeneration();
            generation.TryQueue();
            generation.TryConsume(hasValidSnapshot: true);
            generation.Reset();
            Assert.Equal(0, generation.Generation);
            Assert.False(generation.Queued);
        }

        [Fact]
        public void CoordinatorArchive_MovesCurrentCapturesWithoutDisposing()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var archive = TypeOf(assembly.MainModule, CoordinatorType).Methods
                .Single(m => m.Name == "ArchiveFrozenSources");
            var calls = CallsOf(archive);

            Assert.True(calls.Count(c => c.Name == "AddRange") >= 1);
            Assert.True(calls.Count(c => c.Name == "Clear") >= 1);
            Assert.Equal(0, calls.Count(c => c.Name == "Dispose" || c.Name == "RemoveTexture"));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));
        }

        [Fact]
        public void CoordinatorGui_RecaptureButtonOnlyQueuesFlag()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var gui = TypeOf(assembly.MainModule, CoordinatorType).Methods
                .Single(m => m.Name == "DrawProbeGui");
            var calls = CallsOf(gui);

            Assert.Equal(1, CountCalls(calls, "Brutal.ImGuiApi.ImGui", "Button"));
            Assert.True(CountCalls(calls, "KSARS.Visuals.ProbeCaptureGeneration", "TryQueue") >= 1);
            Assert.Equal(0, calls.Count(c => (c.Name == "Dispose" || c.Name == "RemoveTexture"
                    || c.Name.Contains("WaitIdle") || c.Name == "UpdateDescriptorSets")
                && (c.DeclaringType.Contains("Capture") || c.DeclaringType.Contains("Composite")
                    || c.DeclaringType.Contains("Adapter") || c.DeclaringType.Contains("Context")
                    || c.DeclaringType.Contains("VkDevice") || c.DeclaringType.Contains("VkQueue"))));
        }

        [Fact]
        public void CoordinatorGui_RecaptureAndOverridePrecedeNoBodyEarlyReturn()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var gui = TypeOf(assembly.MainModule, CoordinatorType).Methods
                .Single(m => m.Name == "DrawProbeGui");
            var instructions = gui.Body.Instructions.ToList();

            int buttonIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.DeclaringType.FullName == "Brutal.ImGuiApi.ImGui"
                && mr.Name == "Button");
            int checkboxIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.DeclaringType.FullName == "Brutal.ImGuiApi.ImGui"
                && mr.Name == "Checkbox");
            int inputIntIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.DeclaringType.FullName == "Brutal.ImGuiApi.ImGui"
                && mr.Name == "InputInt");
            var captureCountReads = instructions
                .Select((instruction, index) => (instruction, index))
                .Where(pair => pair.instruction.Operand is MethodReference mr
                    && mr.Name == "get_Count"
                    && mr.DeclaringType.FullName.EndsWith("/ProbeCapture>"))
                .Select(pair => pair.index)
                .ToList();

            Assert.True(buttonIndex >= 0 && checkboxIndex >= 0 && inputIntIndex >= 0);
            Assert.True(captureCountReads.Count >= 4,
                "GUI must read the body-capture count for the frozen-budget label, the empty-body early return, and the preview gate");
            int earlyReturnCheck = captureCountReads[2];
            Assert.True(buttonIndex < captureCountReads[0],
                "recapture queue must run before the frozen-budget label reads captures");
            Assert.True(checkboxIndex < earlyReturnCheck,
                "beta-override controls must run before the empty-body early return");
            Assert.True(inputIntIndex > earlyReturnCheck,
                "composite mode/body preview controls stay behind the body-present early return");
        }

        [Fact]
        public void CoordinatorConsume_HappensAtSourceBoundaryNotGui()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var source = TypeOf(assembly.MainModule, CoordinatorType).Methods
                .Single(m => m.Name == "TryRecordSources");
            var calls = CallsOf(source);

            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.ProbeCaptureGeneration", "TryConsume"));
            int consumeIndex = calls.FindIndex(c => c.Name == "TryConsume");
            int captureIndex = calls.FindIndex(c => c.Name == "TrySelectAndCapture");
            Assert.True(consumeIndex >= 0 && captureIndex > consumeIndex,
                "queued recapture must archive before the current candidate capture at the same boundary");

            var gui = TypeOf(assembly.MainModule, CoordinatorType).Methods
                .Single(m => m.Name == "DrawProbeGui");
            Assert.Equal(0, CountCalls(CallsOf(gui), "KSARS.Visuals.ProbeCaptureGeneration", "TryConsume"));
        }

        [Fact]
        public void CoordinatorShutdown_DisposesCompositesAndAllGenerations()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var shutdown = TypeOf(assembly.MainModule, CoordinatorType).Methods
                .Single(m => m.Name == "Shutdown");
            var calls = CallsOf(shutdown);
            var instructions = shutdown.Body.Instructions;

            Assert.True(CountCalls(calls, "KSARS.Visuals.AtlasCompositeProbe", "Dispose") >= 1);
            Assert.True(calls.Count(c => (c.DeclaringType == "KSARS.Visuals.NativeCelestialCaptureAdapter"
                    || c.DeclaringType == "KSARS.Visuals.NativeSolarCaptureAdapter"
                    || c.DeclaringType == "RenderCore.RenderTechnique") && c.Name == "Dispose") >= 2);
            Assert.True(CountCalls(calls, "KSARS.Visuals.ObjectCaptureContext", "Dispose") >= 1);
            Assert.True(calls.Count(c => c.Name == "RemoveTexture") >= 1);
            Assert.True(calls.Count(c => c.Name.Contains("WaitIdle")) >= 1);
            Assert.Equal(0, instructions.Count(i => i.OpCode == OpCodes.Ldsfld
                && i.Operand is FieldReference fr && fr.Name == "s_previewComposite"));
        }
    }
}
