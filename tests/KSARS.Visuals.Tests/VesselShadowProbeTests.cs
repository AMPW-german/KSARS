using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    [Collection("VisualHookState")]
    public class VesselShadowProbeTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");

        private static AssemblyDefinition LoadModAssembly() => AssemblyDefinition.ReadAssembly(ModAssemblyPath);

        private static TypeDefinition ModType(AssemblyDefinition assembly, string name)
        {
            var type = assembly.MainModule.GetType(name);
            Assert.NotNull(type);
            return type;
        }

        private static MethodDefinition SingleMethod(TypeDefinition type, string name) =>
            type.Methods.Single(m => m.Name == name);

        private static List<(int Index, MethodReference Reference)> CallsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Select((instruction, index) => (instruction, index))
                .Where(x => (x.instruction.OpCode == OpCodes.Call || x.instruction.OpCode == OpCodes.Callvirt)
                    && x.instruction.Operand is MethodReference)
                .Select(x => (x.index, (MethodReference)x.instruction.Operand))
                .ToList();

        [Fact]
        public void Install_RequiresLightingProbe_AndPatchesWritersAndCasters()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition install = SingleMethod(type, "TryInstallVesselShadowProbe");
            var calls = CallsOf(install);
            // The shadow map only makes sense while the lighting path presents
            // the aberrated sun; a lighting-less install must refuse.
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).Contains("KSARS_LIGHTING_PROBE"));
            // Four vessel writers + two depth casters + the record postfix.
            Assert.Equal(4, calls.Count(c => c.Reference.Name == "RequiredVesselWriter"));
            Assert.Equal(2, calls.Count(c => c.Reference.Name == "RequiredShadowCaster"));
            Assert.Equal(2, calls.Count(c => c.Reference.Name == "RequiredShadowSelector"));
            Assert.Equal(2, calls.Count(c => c.Reference.Name == "RequiredProgramSampler"));
            Assert.Contains(calls, c => c.Reference.Name == "Patch");
            // Depth casters are resolved on the nested DepthData types so the
            // color-path DepthData methods and prepass stay untouched.
            Assert.True(install.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldtoken) >= 6);
            MethodDefinition caster = SingleMethod(type, "RequiredShadowCaster");
            Assert.Contains(caster.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "AddCommandsCSM");
            Assert.Contains(caster.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).Contains("signature changed"));
        }

        [Fact]
        public void Transpilers_RewriteExactlyOneGetCsmCall()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            foreach (string name in new[] { "TranspileWriterCsmBinding", "TranspileShadowCasterCsmBinding" })
            {
                MethodDefinition transpiler = SingleMethod(type, name);
                Assert.Contains(CallsOf(transpiler), c => c.Reference.Name == "RewriteCsmCall");
            }

            MethodDefinition rewrite = SingleMethod(type, "RewriteCsmCall");
            var fieldWrites = rewrite.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            // A drifted anchor count must flag the install to fail closed.
            Assert.Contains("s_shadowAnchorMissed", fieldWrites);
        }

        [Fact]
        public void Selectors_GateOnSeparateFlags_AndFallBackToNative()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");

            MethodDefinition writer = SingleMethod(type, "GetCsmForVesselWriter");
            Assert.Equal("KSA.Rendering.Lighting.CascadedShadowSystem", writer.ReturnType.FullName);
            var writerReads = writer.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("s_vesselShadowActive", writerReads);
            Assert.Contains("s_ownedCsm", writerReads);
            Assert.Contains(CallsOf(writer), c => c.Reference.Name == "GetCSMSystem");

            MethodDefinition depth = SingleMethod(type, "GetCsmForDepthPass");
            var depthReads = depth.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("s_recordingOwnedShadow", depthReads);
            Assert.Contains("s_ownedCsm", depthReads);
            Assert.Contains(CallsOf(depth), c => c.Reference.Name == "GetCSMSystem");
        }

        [Fact]
        public void Recorder_CapturesObserver_AberratesSun_AndDrivesOwnedCascades()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition record = SingleMethod(type, "TryRecordVesselShadowPass");
            var calls = CallsOf(record);
            var names = calls.Select(c => c.Reference.Name).ToList();

            Assert.Contains("TryCaptureEffectiveObserver", names);
            Assert.Contains("GetPositionEgo", names);
            Assert.Contains("TryAberrateEgoOffset", names);
            Assert.Contains("UpdateUniforms", names);
            Assert.Contains("BeginCascade", names);
            Assert.Contains("EndCascade", names);
            Assert.Contains("DispatchPostFilterPass", names);
            // Both native caster entry points feed the owned pass.
            Assert.Equal(2, calls.Count(c => c.Reference.Name == "AddCommandsCSM"
                && c.Reference.Parameters.Count == 4));
            // The native settings gate must be honored.
            Assert.Contains("ShowVesselInCascadeShadows", names);
            // A rest observer must skip the owned pass.
            Assert.Contains("get_Q", names);
            // The recording flag must be cleared on every path out.
            var writes = record.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("s_recordingOwnedShadow", writes);
            Assert.Contains("s_vesselShadowActive", writes);
        }

        [Fact]
        public void Postfix_AndSelectors_NeverThrow_AndLatchFailures()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition postfix = SingleMethod(type, "AfterRingMeshUpdate");
            Assert.DoesNotContain(postfix.Body.Instructions, i => i.OpCode == OpCodes.Rethrow);
            Assert.Contains(CallsOf(postfix), c => c.Reference.Name == "TryRecordVesselShadowPass");
            var writes = postfix.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("s_shadowFailed", writes);

            // The owned system must be constructed with the native settings
            // snapshot and the reflected native shadow sampler handles.
            MethodDefinition create = SingleMethod(type, "TryCreateOwnedCsm");
            var createCalls = CallsOf(create).Select(c => c.Reference.Name).ToList();
            Assert.Contains("get_Instance", createCalls);
            Assert.Contains(create.Body.Instructions,
                i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f
                    && f.Name == "TextureSystem");
            Assert.Contains("GetCSMSystem", createCalls);
            Assert.Contains("get_CurrentSettings", createCalls);
            Assert.Equal(2, create.Body.Instructions.Count(i => i.OpCode == OpCodes.Callvirt
                && i.Operand is MethodReference m && m.Name == "GetValue"));
            Assert.DoesNotContain(create.Body.Instructions, i => i.OpCode == OpCodes.Rethrow);
        }

        [Fact]
        public void RebuildAndShutdown_RetireOwnedShadowSystem()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition collect = SingleMethod(type, "CollectVesselShadowResources");
            Assert.Contains(CallsOf(collect), c => c.Reference.Name == "Add");
            var writes = collect.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();
            Assert.Contains("s_vesselShadowActive", writes);

            MethodDefinition reset = SingleMethod(type, "ResetVesselShadowProbeState");
            Assert.DoesNotContain(CallsOf(reset), c => c.Reference.Name == "Dispose");
            Assert.DoesNotContain(reset.Body.Instructions, i => i.OpCode == OpCodes.Callvirt
                && i.Operand is MethodReference m && m.Name == "WaitIdle");

            // The rebuild path must collect the owned system and mark the
            // probe failed on the unverified-idle branch.
            MethodDefinition rebuilt = SingleMethod(type, "OnRendererRebuilt");
            Assert.Contains(CallsOf(rebuilt), c => c.Reference.Name == "CollectVesselShadowResources");
            Assert.Contains(rebuilt.Body.Instructions,
                i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f
                    && f.Name == "s_shadowFailed");
        }

        [Fact]
        public void RenderHooks_GatesInstallOnShadowEnvVar_AndResetsChain()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition hooks = ModType(assembly, "KSARS.Visuals.RenderHooks");
            MethodDefinition install = SingleMethod(hooks, "TryInstall");
            Assert.Contains(install.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "KSARS_SHADOW_PROBE");
            Assert.Contains(CallsOf(install), c => c.Reference.Name == "TryInstallVesselShadowProbe");

            MethodDefinition guarded = SingleMethod(hooks, "ResetVesselShadowProbeStateIfLoaded");
            Assert.Contains(CallsOf(guarded), c => c.Reference.Name == "ResetVesselShadowProbeState");
        }

        [Fact]
        public void Status_AndGui_SurfaceTheShadowProbe()
        {
            using AssemblyDefinition assembly = LoadModAssembly();
            TypeDefinition type = ModType(assembly, "KSARS.Visuals.RelativisticSpaceRenderer");
            MethodDefinition status = type.Methods.Single(m => m.Name == "GetStatus");
            Assert.Contains(status.Body.Instructions,
                i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f
                    && f.Name == "s_shadowStatus");
            MethodDefinition gui = type.Methods.Single(m => m.Name == "DrawProbeGui");
            Assert.Contains(gui.Body.Instructions,
                i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f
                    && f.Name == "s_shadowStatus");
        }
    }
}
