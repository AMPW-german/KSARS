using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class RenderHooksTests
    {
        private static StartupObservation ValidBaseline() => new()
        {
            StageOrder = new[] { 1, 2, 3 },
            DescriptorsViewsLength = 0,
            DescriptorsNextViewportId = 1,
            DescriptorsAllocationSealed = false,
            DescriptorsPoolCapacity = 8,
            SealViewsLength = 8,
            SealPoolCapacity = 8,
            SealDisposalBufferLength = 8,
            SealShaderSlots = new[] { 0, 1, 2, 3, 4, 5, 6, 7 },
            CameraDataLength = 8,
            LightingDataLength = 8,
            CelestialDataLength = 8,
            VesselDataLength = 8,
            DescriptorSetsLength = 24,
            FrameCount = 3,
        };

        [Fact]
        public void ValidBaselineObservation_PassesWithCapturePendingStatus()
        {
            var validation = RenderHooks.ValidateStartup(ValidBaseline());
            Assert.True(validation.Passed, validation.Status);
            Assert.Contains("8 baseline slots", validation.Status);
            Assert.Contains("object-atlas/foreground gate not attempted", validation.Status);
            Assert.DoesNotContain("ready", validation.Status);
        }

        [Theory]
        [InlineData("descriptors-views", "failed")]
        [InlineData("descriptors-nextid", "failed")]
        [InlineData("descriptors-sealed", "failed")]
        [InlineData("descriptors-pool", "failed")]
        [InlineData("seal-views", "failed")]
        [InlineData("seal-pool", "failed")]
        [InlineData("seal-disposal", "failed")]
        [InlineData("seal-slots-duplicate", "failed")]
        [InlineData("seal-slots-outofrange", "failed")]
        [InlineData("seal-slots-short", "failed")]
        [InlineData("ctor-framecount", "failed")]
        [InlineData("ctor-framecount-max", "failed")]
        [InlineData("ctor-descriptorsets", "failed")]
        [InlineData("ubo-camera", "failed")]
        [InlineData("ubo-lighting", "failed")]
        [InlineData("ubo-celestial", "failed")]
        [InlineData("ubo-vessel", "failed")]
        [InlineData("order-skipped", "failed")]
        [InlineData("order-reversed", "failed")]
        [InlineData("order-duplicate", "failed")]
        [InlineData("order-empty", "incomplete")]
        [InlineData("order-partial", "incomplete")]
        public void InvalidObservation_FailsWithDisabledStatus(string mutation, string marker)
        {
            var observation = ValidBaseline();
            switch (mutation)
            {
                case "descriptors-views": observation.DescriptorsViewsLength = 4; break;
                case "descriptors-nextid": observation.DescriptorsNextViewportId = 0; break;
                case "descriptors-sealed": observation.DescriptorsAllocationSealed = true; break;
                case "descriptors-pool": observation.DescriptorsPoolCapacity = 4; break;
                case "seal-views": observation.SealViewsLength = 7; break;
                case "seal-pool": observation.SealPoolCapacity = 4; break;
                case "seal-disposal": observation.SealDisposalBufferLength = 7; break;
                case "seal-slots-duplicate": observation.SealShaderSlots = new[] { 0, 1, 2, 3, 4, 5, 6, 6 }; break;
                case "seal-slots-outofrange": observation.SealShaderSlots = new[] { 0, 1, 2, 3, 4, 5, 6, 8 }; break;
                case "seal-slots-short": observation.SealShaderSlots = new[] { 0, 1, 2, 3, 4, 5, 6 }; break;
                case "ctor-framecount": observation.FrameCount = 0; break;
                case "ctor-framecount-max": observation.FrameCount = int.MaxValue; break;
                case "ctor-descriptorsets": observation.DescriptorSetsLength = 16; break;
                case "ubo-camera": observation.CameraDataLength = 7; break;
                case "ubo-lighting": observation.LightingDataLength = 7; break;
                case "ubo-celestial": observation.CelestialDataLength = 7; break;
                case "ubo-vessel": observation.VesselDataLength = 7; break;
                case "order-skipped": observation.StageOrder = new[] { 1, 3 }; break;
                case "order-reversed": observation.StageOrder = new[] { 3, 2, 1 }; break;
                case "order-duplicate": observation.StageOrder = new[] { 1, 2, 3, 1 }; break;
                case "order-empty": observation.StageOrder = Array.Empty<int>(); break;
                case "order-partial": observation.StageOrder = new[] { 1, 2 }; break;
            }

            var validation = RenderHooks.ValidateStartup(observation);
            Assert.False(validation.Passed, validation.Status);
            Assert.Contains(marker, validation.Status);
            Assert.Contains("disabled", validation.Status);
            Assert.DoesNotContain("ready", validation.Status);
        }

        [Fact]
        public void TryInstall_UnsupportedBaseline_ReturnsFalseWithoutOwnerPatches()
        {
            Assert.False(RenderHooks.TryInstall(RenderCompatibility.InspectionResult.Failed("test mismatch")));
            AssertNoVisualsOwnerPatches();
        }

        [Fact]
        public void TryInstall_EmptyMapping_ReturnsFalseWithoutPartialPatch()
        {
            var matched = RenderCompatibility.InspectionResult.Matched(
                new ReadOnlyDictionary<string, MemberInfo>(new Dictionary<string, MemberInfo>()));
            Assert.False(RenderHooks.TryInstall(matched));
            AssertNoVisualsOwnerPatches();
        }

        private static void AssertNoVisualsOwnerPatches()
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Assert.False(
                    Harmony.GetPatchInfo(method)?.Owners.Contains(RenderHooks.OwnerId) == true,
                    $"unexpected KSARS.Visuals patch owner on {method.DeclaringType?.FullName}::{method.Name}");
            }
        }

        private sealed class FixturePool
        {
            public int Capacity => 8;
        }

        private sealed class FixtureViewport
        {
            public FixtureViewport(int shaderSlot) => ShaderSlot = shaderSlot;
            public int ShaderSlot { get; }
        }

        private static class FixtureState
        {
            internal static FixtureViewport?[] Views = Array.Empty<FixtureViewport?>();
            internal static int NextViewportId = 1;
            internal static bool AllocationSealed;
            internal static FixturePool ShaderIndexPool = new();
            internal static FixtureViewport?[] DisposalBuffer = new FixtureViewport?[8];
            internal static object?[] DescriptorSets = new object?[24];
            internal static int FrameCount = 3;
            internal static object?[] CameraData = new object?[8];
            internal static object?[] LightingData = new object?[8];
            internal static object?[] CelestialData = new object?[8];
            internal static object?[] VesselData = new object?[8];
        }

        private static class FixtureShaderBindings
        {
            [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
            internal static void CreateDescriptors() { }
        }

        private static class FixtureViewports
        {
            [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
            internal static void SealAllocation() => FixtureState.AllocationSealed = true;
        }

        private sealed class FixtureProgram
        {
            [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
            public FixtureProgram()
            {
                FixtureShaderBindings.CreateDescriptors();
                FixtureState.Views = new FixtureViewport?[8];
                for (int i = 0; i < 8; i++)
                {
                    FixtureState.Views[i] = new FixtureViewport(i);
                }
                FixtureViewports.SealAllocation();
            }

            [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
            public void Dispose() { }
        }

        private static class FixturePhysics
        {
            [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
            internal static void PatchedMethod() { }
        }

        private abstract class FixtureAbstract
        {
            public abstract void AbstractDispose();
        }

        private static int s_physicsPrefixCalls;
        private static int s_unrelatedPrefixCalls;

        private static void PhysicsPrefix() => s_physicsPrefixCalls++;
        private static void UnrelatedPrefix() => s_unrelatedPrefixCalls++;

        private static void ResetFixtureState()
        {
            FixtureState.Views = Array.Empty<FixtureViewport?>();
            FixtureState.NextViewportId = 1;
            FixtureState.AllocationSealed = false;
            FixtureState.ShaderIndexPool = new FixturePool();
            s_physicsPrefixCalls = 0;
            s_unrelatedPrefixCalls = 0;
        }

        private static MethodBase FixturePhysicsMethod =>
            typeof(FixturePhysics).GetMethod(nameof(FixturePhysics.PatchedMethod), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static HarmonyMethod SelfHook(string name) =>
            new(typeof(RenderHooksTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!);

        private static Dictionary<string, MemberInfo> FixtureMembers()
        {
            const BindingFlags f = BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags i = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var state = typeof(FixtureState);
            return new Dictionary<string, MemberInfo>
            {
                ["KSA.GlobalShaderBindings::CreateDescriptors"] = typeof(FixtureShaderBindings).GetMethod(nameof(FixtureShaderBindings.CreateDescriptors), f)!,
                ["KSA.ViewportRegistry::SealAllocation"] = typeof(FixtureViewports).GetMethod(nameof(FixtureViewports.SealAllocation), f)!,
                ["KSA.Program::.ctor"] = typeof(FixtureProgram).GetConstructor(Type.EmptyTypes)!,
                ["KSA.Program::Dispose"] = typeof(FixtureProgram).GetMethod(nameof(FixtureProgram.Dispose), i)!,
                ["KSA.ViewportRegistry::_views"] = state.GetField(nameof(FixtureState.Views), f)!,
                ["KSA.ViewportRegistry::_nextViewportId"] = state.GetField(nameof(FixtureState.NextViewportId), f)!,
                ["KSA.ViewportRegistry::_allocationSealed"] = state.GetField(nameof(FixtureState.AllocationSealed), f)!,
                ["KSA.ViewportRegistry::ShaderIndexPool"] = state.GetField(nameof(FixtureState.ShaderIndexPool), f)!,
                ["KSA.ViewportRegistry::DisposalBuffer"] = state.GetField(nameof(FixtureState.DisposalBuffer), f)!,
                ["KSA.GlobalShaderBindings::_descriptorSets"] = state.GetField(nameof(FixtureState.DescriptorSets), f)!,
                ["KSA.GlobalShaderBindings::_frameCount"] = state.GetField(nameof(FixtureState.FrameCount), f)!,
                ["KSA.Program::_cameraData"] = state.GetField(nameof(FixtureState.CameraData), f)!,
                ["KSA.Program::_lightingData"] = state.GetField(nameof(FixtureState.LightingData), f)!,
                ["KSA.Program::_celestialData"] = state.GetField(nameof(FixtureState.CelestialData), f)!,
                ["KSA.Program::_vesselData"] = state.GetField(nameof(FixtureState.VesselData), f)!,
                ["KSA.IViewport::get_ShaderSlot"] = typeof(FixtureViewport).GetProperty(nameof(FixtureViewport.ShaderSlot))!.GetMethod!,
            };
        }

        private static RenderCompatibility.InspectionResult FixtureInspection() =>
            RenderCompatibility.InspectionResult.Matched(
                new ReadOnlyDictionary<string, MemberInfo>(FixtureMembers()));

        private static IEnumerable<MethodBase> FixtureTargets() => new MethodBase[]
        {
            typeof(FixtureShaderBindings).GetMethod(nameof(FixtureShaderBindings.CreateDescriptors), BindingFlags.NonPublic | BindingFlags.Static)!,
            typeof(FixtureViewports).GetMethod(nameof(FixtureViewports.SealAllocation), BindingFlags.NonPublic | BindingFlags.Static)!,
            typeof(FixtureProgram).GetConstructor(Type.EmptyTypes)!,
            typeof(FixtureProgram).GetMethod(nameof(FixtureProgram.Dispose), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!,
        };

        private static void CleanupFixtureOwners()
        {
            RenderHooks.Uninstall("test cleanup");
            new Harmony("KSARS.Visuals").UnpatchAll("KSARS.Visuals");
            new Harmony("KSARS").UnpatchAll("KSARS");
            new Harmony("KSARS.Tests.Unrelated").UnpatchAll("KSARS.Tests.Unrelated");
        }

        [Fact]
        public void TryInstall_FixtureBaseline_ObservesStartupAndUninstallPreservesOtherOwners()
        {
            var physicsHarmony = new Harmony("KSARS");
            var unrelatedHarmony = new Harmony("KSARS.Tests.Unrelated");
            try
            {
                physicsHarmony.Patch(FixturePhysicsMethod, prefix: SelfHook(nameof(PhysicsPrefix)));
                unrelatedHarmony.Patch(FixturePhysicsMethod, prefix: SelfHook(nameof(UnrelatedPrefix)));

                ResetFixtureState();
                Assert.True(RenderHooks.TryInstall(FixtureInspection()));

                foreach (MethodBase target in FixtureTargets())
                {
                    Assert.Contains(RenderHooks.OwnerId, Harmony.GetPatchInfo(target)!.Owners);
                }

                var program = new FixtureProgram();
                Assert.Contains("8 baseline slots", RenderHooks.Status);
                Assert.Contains("object-atlas/foreground gate not attempted", RenderHooks.Status);

                RenderHooks.Uninstall("test");
                foreach (MethodBase target in FixtureTargets())
                {
                    Assert.DoesNotContain(RenderHooks.OwnerId, Harmony.GetPatchInfo(target)?.Owners ?? Enumerable.Empty<string>());
                }

                Patches physicsInfo = Harmony.GetPatchInfo(FixturePhysicsMethod)!;
                Assert.Contains("KSARS", physicsInfo.Owners);
                Assert.Contains("KSARS.Tests.Unrelated", physicsInfo.Owners);

                FixturePhysics.PatchedMethod();
                Assert.Equal(1, s_physicsPrefixCalls);
                Assert.Equal(1, s_unrelatedPrefixCalls);
            }
            finally
            {
                CleanupFixtureOwners();
            }
        }

        [Fact]
        public void TryInstall_AlreadyInstalled_IsIdempotent()
        {
            try
            {
                ResetFixtureState();
                Assert.True(RenderHooks.TryInstall(FixtureInspection()));
                Assert.True(RenderHooks.TryInstall(FixtureInspection()));

                foreach (MethodBase target in FixtureTargets())
                {
                    Patches info = Harmony.GetPatchInfo(target)!;
                    int visualPatches =
                        info.Prefixes.Count(p => p.owner == RenderHooks.OwnerId) +
                        info.Postfixes.Count(p => p.owner == RenderHooks.OwnerId) +
                        info.Transpilers.Count(p => p.owner == RenderHooks.OwnerId) +
                        info.Finalizers.Count(p => p.owner == RenderHooks.OwnerId);
                    Assert.Equal(1, visualPatches);
                }
            }
            finally
            {
                CleanupFixtureOwners();
            }
        }

        [Fact]
        public void ReinstallAfterUninstall_RecordsFreshStages()
        {
            try
            {
                ResetFixtureState();
                Assert.True(RenderHooks.TryInstall(FixtureInspection()));
                var first = new FixtureProgram();
                RenderHooks.Uninstall("test-first");
                Assert.Contains("uninstalled", RenderHooks.Status);
                Assert.Contains("disabled", RenderHooks.Status);

                ResetFixtureState();
                Assert.True(RenderHooks.TryInstall(FixtureInspection()));
                var second = new FixtureProgram();
                Assert.Contains("8 baseline slots", RenderHooks.Status);
                Assert.Contains("object-atlas/foreground gate not attempted", RenderHooks.Status);
            }
            finally
            {
                CleanupFixtureOwners();
            }
        }

        [Fact]
        public void TryInstall_WhenInstalled_FailedInspection_UninstallsProbe()
        {
            var physicsHarmony = new Harmony("KSARS");
            try
            {
                physicsHarmony.Patch(FixturePhysicsMethod, prefix: SelfHook(nameof(PhysicsPrefix)));
                ResetFixtureState();
                Assert.True(RenderHooks.TryInstall(FixtureInspection()));

                Assert.False(RenderHooks.TryInstall(RenderCompatibility.InspectionResult.Failed("changed baseline")));
                AssertNoVisualsOwnerPatches();
                Assert.Contains("KSARS", Harmony.GetPatchInfo(FixturePhysicsMethod)!.Owners);
            }
            finally
            {
                CleanupFixtureOwners();
            }
        }

        [Fact]
        public void TryInstall_WhenInstalled_EmptyMapping_UninstallsProbe()
        {
            var unrelatedHarmony = new Harmony("KSARS.Tests.Unrelated");
            try
            {
                unrelatedHarmony.Patch(FixturePhysicsMethod, prefix: SelfHook(nameof(UnrelatedPrefix)));
                ResetFixtureState();
                Assert.True(RenderHooks.TryInstall(FixtureInspection()));

                var emptyMatched = RenderCompatibility.InspectionResult.Matched(
                    new ReadOnlyDictionary<string, MemberInfo>(new Dictionary<string, MemberInfo>()));
                Assert.False(RenderHooks.TryInstall(emptyMatched));
                AssertNoVisualsOwnerPatches();
                Assert.Contains("KSARS.Tests.Unrelated", Harmony.GetPatchInfo(FixturePhysicsMethod)!.Owners);
            }
            finally
            {
                CleanupFixtureOwners();
            }
        }

        [Fact]
        public void TryInstall_PatchFailure_RollsBackOnlyVisualOwner()
        {
            var physicsHarmony = new Harmony("KSARS");
            var unrelatedHarmony = new Harmony("KSARS.Tests.Unrelated");
            try
            {
                physicsHarmony.Patch(FixturePhysicsMethod, prefix: SelfHook(nameof(PhysicsPrefix)));
                unrelatedHarmony.Patch(FixturePhysicsMethod, prefix: SelfHook(nameof(UnrelatedPrefix)));

                var members = FixtureMembers();
                members["KSA.Program::Dispose"] = typeof(FixtureAbstract)
                    .GetMethod(nameof(FixtureAbstract.AbstractDispose))!;

                bool installed = RenderHooks.TryInstall(
                    RenderCompatibility.InspectionResult.Matched(new ReadOnlyDictionary<string, MemberInfo>(members)));
                Assert.False(installed, "fixture abstract target was patchable; rollback path was not exercised");

                AssertNoVisualsOwnerPatches();
                Patches physicsInfo = Harmony.GetPatchInfo(FixturePhysicsMethod)!;
                Assert.Contains("KSARS", physicsInfo.Owners);
                Assert.Contains("KSARS.Tests.Unrelated", physicsInfo.Owners);
            }
            finally
            {
                CleanupFixtureOwners();
            }
        }

        private static class FixtureRebuildTarget
        {
            internal static bool ThrowOriginal;
            internal static int NormalReturns;

            [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
            internal static void RebuildRenderer(bool full)
            {
                if (ThrowOriginal)
                {
                    throw new InvalidOperationException("fixture rebuild failure");
                }

                NormalReturns++;
            }
        }

        private static int s_rebuildPostfixCalls;

        private static void RebuildPostfix() => s_rebuildPostfixCalls++;

        private static IEnumerable<CodeInstruction> FixtureTranspiler(IEnumerable<CodeInstruction> instructions) =>
            instructions;

        private static MethodBase FixtureRebuildMethod =>
            typeof(FixtureRebuildTarget).GetMethod(nameof(FixtureRebuildTarget.RebuildRenderer),
                BindingFlags.NonPublic | BindingFlags.Static)!;

        [Fact]
        public void ManagedPostfix_RunsOnlyAfterNormalReturnOfOriginal()
        {
            var harmony = new Harmony("KSARS.Tests.Rebuild");
            try
            {
                FixtureRebuildTarget.ThrowOriginal = true;
                FixtureRebuildTarget.NormalReturns = 0;
                s_rebuildPostfixCalls = 0;
                harmony.Patch(FixtureRebuildMethod, postfix: SelfHook(nameof(RebuildPostfix)));

                Assert.Throws<InvalidOperationException>(() => FixtureRebuildTarget.RebuildRenderer(true));
                Assert.Equal(0, s_rebuildPostfixCalls);
                Assert.Equal(0, FixtureRebuildTarget.NormalReturns);

                FixtureRebuildTarget.ThrowOriginal = false;
                FixtureRebuildTarget.RebuildRenderer(true);
                Assert.Equal(1, FixtureRebuildTarget.NormalReturns);
                Assert.Equal(1, s_rebuildPostfixCalls);
            }
            finally
            {
                FixtureRebuildTarget.ThrowOriginal = false;
                harmony.UnpatchAll("KSARS.Tests.Rebuild");
            }
        }

        [Fact]
        public void SecondPatchFailure_RollbackRemovesBothVisualHooksOnly()
        {
            var physicsHarmony = new Harmony("KSARS");
            var unrelatedHarmony = new Harmony("KSARS.Tests.Unrelated");
            var visuals = new Harmony(RenderHooks.OwnerId);
            var abstractTarget = typeof(FixtureAbstract)
                .GetMethod(nameof(FixtureAbstract.AbstractDispose))!;
            try
            {
                physicsHarmony.Patch(FixturePhysicsMethod, prefix: SelfHook(nameof(PhysicsPrefix)));
                unrelatedHarmony.Patch(FixturePhysicsMethod, prefix: SelfHook(nameof(UnrelatedPrefix)));

                visuals.Patch(FixtureRebuildMethod, transpiler: SelfHook(nameof(FixtureTranspiler)));
                Assert.Contains(RenderHooks.OwnerId, Harmony.GetPatchInfo(FixtureRebuildMethod)!.Owners);
                Assert.Single(Harmony.GetPatchInfo(FixtureRebuildMethod)!.Transpilers,
                    p => p.owner == RenderHooks.OwnerId);

                bool secondFailed = false;
                try
                {
                    visuals.Patch(abstractTarget, postfix: SelfHook(nameof(RebuildPostfix)));
                }
                catch (Exception)
                {
                    secondFailed = true;
                }
                Assert.True(secondFailed, "fixture abstract target was patchable; rollback path was not exercised");

                visuals.Unpatch(FixtureRebuildMethod, HarmonyPatchType.Transpiler, RenderHooks.OwnerId);
                try
                {
                    visuals.Unpatch(abstractTarget, HarmonyPatchType.Postfix, RenderHooks.OwnerId);
                }
                catch (ArgumentException)
                {
                }
                Assert.DoesNotContain(RenderHooks.OwnerId,
                    Harmony.GetPatchInfo(FixtureRebuildMethod)?.Owners ?? Enumerable.Empty<string>());

                Patches physicsInfo = Harmony.GetPatchInfo(FixturePhysicsMethod)!;
                Assert.Contains("KSARS", physicsInfo.Owners);
                Assert.Contains("KSARS.Tests.Unrelated", physicsInfo.Owners);
            }
            finally
            {
                CleanupFixtureOwners();
            }
        }
    }
}
