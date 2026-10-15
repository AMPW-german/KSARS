using KSARS.Visuals;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class RebuildBoundaryTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");

        private static readonly (string DeclaringType, string Name, string ReturnType)[] ValidCalls =
        {
            ("KSA.Program", "AcquireFrame", "RenderCore.FrameAcquireResult"),
            ("Brutal.VulkanApi.VkDeviceExtensions", "WaitIdle", "System.Void"),
            ("KSA.Program", "BuildRenderTargets", "System.Void"),
            ("KSA.Program", "UnrelatedWork", "System.Void"),
            ("KSA.Program", "SetCameraUbo", "System.Void"),
            ("Core.Renderer", "AcknowledgeSwapchainRebuild", "System.Void"),
            ("Core.KSADeviceContextEx", "get_Device", "Brutal.VulkanApi.Abstractions.DeviceEx"),
            ("Brutal.VulkanApi.VkDeviceExtensions", "WaitIdle", "System.Void"),
        };

        private static readonly (string OpCode, string? Member)[] ValidTail =
        {
            ("ldsfld", "KSA.Program::_renderer"),
            ("callvirt", "Core.KSADeviceContextEx::get_Device"),
            ("call", "Brutal.VulkanApi.VkDeviceExtensions::WaitIdle"),
            ("ret", null),
        };

        private static MethodDefinition CoordinatorMethod(ModuleDefinition module, string name) =>
            module.GetType("KSARS.Visuals.RelativisticSpaceRenderer")!.Methods.Single(m => m.Name == name);

        private static List<MethodReference> MethodRefsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => i.Operand is MethodReference)
                .Select(i => (MethodReference)i.Operand)
                .ToList();

        private static List<FieldReference> FieldRefsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => i.Operand is FieldReference)
                .Select(i => (FieldReference)i.Operand)
                .ToList();

        private static bool IsLdargParam(Instruction instruction, int index) =>
            (instruction.OpCode == OpCodes.Ldarg || instruction.OpCode == OpCodes.Ldarg_S)
            && instruction.Operand is ParameterReference p && p.Index == index;

        private static bool IsStargParam(Instruction instruction, int index) =>
            (instruction.OpCode == OpCodes.Starg || instruction.OpCode == OpCodes.Starg_S)
            && instruction.Operand is ParameterReference p && p.Index == index;

        [Fact]
        public void ValidSequence_WithSingleReturnAndIdleTail_Accepted()
        {
            Assert.True(RenderCompatibility.TryValidateRebuildBoundary(ValidCalls, 1, ValidTail, out string reason));
            Assert.Contains("verified", reason);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void MultipleReturns_Rejected(int returnCount)
        {
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(ValidCalls, returnCount, ValidTail, out string reason));
            Assert.Contains("return", reason);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public void WaitIdleCount_NotExactlyTwo_Rejected(int extraIdle)
        {
            var calls = extraIdle == 1
                ? ValidCalls.Where(c => c.Name != "WaitIdle").Append(ValidCalls.Last()).ToList()
                : ValidCalls.Append(("Brutal.VulkanApi.VkDeviceExtensions", "WaitIdle", "System.Void")).ToList();
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(calls, 1, ValidTail, out string reason));
            Assert.Contains("WaitIdle", reason);
        }

        [Theory]
        [InlineData("KSA.Program", "BuildRenderTargets")]
        [InlineData("KSA.Program", "SetCameraUbo")]
        [InlineData("Core.Renderer", "AcknowledgeSwapchainRebuild")]
        public void MissingRequiredCall_Rejected(string declaringType, string name)
        {
            var calls = ValidCalls.Where(c => !(c.DeclaringType == declaringType && c.Name == name)).ToList();
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(calls, 1, ValidTail, out string reason));
            Assert.Contains("missing", reason);
            Assert.Contains(name, reason);
        }

        [Theory]
        [InlineData("KSA.Program", "BuildRenderTargets")]
        [InlineData("KSA.Program", "SetCameraUbo")]
        [InlineData("Core.Renderer", "AcknowledgeSwapchainRebuild")]
        public void DuplicateRequiredCall_Rejected(string declaringType, string name)
        {
            var calls = ValidCalls.ToList();
            calls.Add((declaringType, name, "System.Void"));
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(calls, 1, ValidTail, out string reason));
            Assert.Contains("duplicate", reason);
            Assert.Contains(name, reason);
        }

        [Fact]
        public void ReorderedRequiredCalls_Rejected()
        {
            var calls = ValidCalls.ToList();
            int setCamera = calls.FindIndex(c => c.Name == "SetCameraUbo");
            int acknowledge = calls.FindIndex(c => c.Name == "AcknowledgeSwapchainRebuild");
            (calls[setCamera], calls[acknowledge]) = (calls[acknowledge], calls[setCamera]);
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(calls, 1, ValidTail, out string reason));
            Assert.Contains("out of order", reason);
        }

        [Fact]
        public void FirstIdleAfterBuildRenderTargets_Rejected()
        {
            var calls = ValidCalls.ToList();
            var firstIdle = calls[1];
            calls.RemoveAt(1);
            calls.Insert(4, firstIdle);
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(calls, 1, ValidTail, out string reason));
            Assert.Contains("out of order", reason);
        }

        [Fact]
        public void FinalIdleNotLastCall_Rejected()
        {
            var calls = ValidCalls.Append(("KSA.Program", "AfterIdle", "System.Void")).ToList();
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(calls, 1, ValidTail, out string reason));
            Assert.Contains("last call", reason);
        }

        [Fact]
        public void UnsafeTail_MissingDeviceReceiver_Rejected()
        {
            var tail = new (string OpCode, string? Member)[]
            {
                ("ldsfld", "KSA.Program::_renderer"),
                ("call", "Brutal.VulkanApi.VkDeviceExtensions::WaitIdle"),
                ("ret", null),
            };
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(ValidCalls, 1, tail, out string reason));
            Assert.Contains("tail", reason);
        }

        [Fact]
        public void UnsafeTail_IdleNotImmediatelyBeforeReturn_Rejected()
        {
            var tail = ValidTail.ToList();
            tail.Insert(tail.Count - 1, ("call", "KSA.Program::AfterIdle"));
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(ValidCalls, 1, tail, out string reason));
            Assert.Contains("tail", reason);
        }

        [Fact]
        public void NonVoidWaitIdle_Rejected()
        {
            var calls = ValidCalls.Select(c => c.Name == "WaitIdle"
                ? (c.DeclaringType, c.Name, "Brutal.VulkanApi.VkResult") : c).ToList();
            Assert.False(RenderCompatibility.TryValidateRebuildBoundary(calls, 1, ValidTail, out string reason));
            Assert.Contains("System.Void", reason);
        }

        [Fact]
        public void PrepareProbe_ResolvesRebuildMethodAndValidatesOriginalInstructions()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = CoordinatorMethod(assembly.MainModule, "PrepareProbe");
            var calls = MethodRefsOf(prepare);
            var fields = FieldRefsOf(prepare);

            var strings = prepare.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldstr)
                .Select(i => (string)i.Operand)
                .ToList();
            Assert.Contains("KSA.Program::RebuildRenderer", strings);
            Assert.Contains(calls,
                m => m.DeclaringType.FullName == "HarmonyLib.PatchProcessor" && m.Name == "GetOriginalInstructions");
            Assert.Contains(calls,
                m => m.DeclaringType.FullName == "KSARS.Visuals.RenderCompatibility" && m.Name == "TryValidateRebuildBoundary");
            Assert.Contains(fields, f => f.Name == "s_rebuildMethod");
            Assert.Contains(fields, f => f.Name == "s_rebuildBoundaryVerified");
        }

        [Fact]
        public void OnRendererRebuilt_RetiresOnlyPerFrameResourcesWithoutIdle()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var method = CoordinatorMethod(assembly.MainModule, "OnRendererRebuilt");

            Assert.True(method.IsStatic);
            Assert.Equal("System.Void", method.ReturnType.FullName);
            Assert.Empty(method.Parameters);

            var calls = MethodRefsOf(method);
            Assert.Equal(0, calls.Count(m => m.Name.Contains("WaitIdle")));
            Assert.Equal(1, calls.Count(m =>
                m.DeclaringType.FullName == "KSARS.Visuals.FrameResourceRetirement" && m.Name == "TryRetireAfterIdle"));
            foreach (string forbidden in new[]
            {
                "ObjectCaptureContext", "NativeCelestialCaptureAdapter", "NativeSolarCaptureAdapter",
            })
            {
                Assert.Equal(0, calls.Count(m => m.DeclaringType.Name == forbidden && m.Name == "Dispose"));
            }
            Assert.Equal(0, calls.Count(m => m.Name == "RemoveTexture"));

            var fields = FieldRefsOf(method).Select(f => f.Name).ToList();
            foreach (string expected in new[]
            {
                "s_rebuildBoundaryVerified", "s_rebuildMethod", "s_composites", "s_mainFrames", "s_mainRing",
                "s_previewComposite", "s_preparedMainFrame", "s_lastSnapshot", "s_axisDiagnostic",
                "s_mainOwnership", "s_rendererEpoch", "s_firstMainRecordLogged",
                "s_mainRetryAfterRebuild", "s_compositeRetryAfterRebuild",
                "s_mainProbeEnabled", "s_recordingDisabled", "s_mainFailed", "s_compositeFailed",
            })
            {
                Assert.Contains(expected, fields);
            }

            foreach (string forbidden in new[]
            {
                "s_captures", "s_captureArchive", "s_solarCapture", "s_solarArchive",
            })
            {
                Assert.DoesNotContain(forbidden, fields);
            }
        }

        private static bool IsStsfldNull(Instruction instruction, string fieldName) =>
            instruction.OpCode == OpCodes.Stsfld
            && instruction.Operand is FieldReference fr && fr.Name == fieldName;

        private static int IndexOfStoreWithPrevious(List<Instruction> instructions, string fieldName, OpCode valueOp)
        {
            for (int i = 1; i < instructions.Count; i++)
            {
                if (IsStsfldNull(instructions[i], fieldName) && instructions[i - 1].OpCode == valueOp)
                {
                    return i;
                }
            }
            return -1;
        }

        [Fact]
        public void ResetProbeState_ClearsRebuildLifecycleState()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var reset = CoordinatorMethod(assembly.MainModule, "ResetProbeState");
            var instructions = reset.Body.Instructions.ToList();

            Assert.True(IndexOfStoreWithPrevious(instructions, "s_rebuildMethod", OpCodes.Ldnull) >= 0,
                "ResetProbeState must null s_rebuildMethod");
            foreach (string field in new[]
            {
                "s_rebuildBoundaryVerified", "s_rendererEpoch",
                "s_mainRetryAfterRebuild", "s_compositeRetryAfterRebuild",
            })
            {
                Assert.True(IndexOfStoreWithPrevious(instructions, field, OpCodes.Ldc_I4_0) >= 0,
                    $"ResetProbeState must reset {field}");
            }
        }

        [Fact]
        public void PrepareProbe_InvalidatesVerifiedStateAtEntryAndPromotesOnlyAfterAllValidation()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = CoordinatorMethod(assembly.MainModule, "PrepareProbe");
            var instructions = prepare.Body.Instructions.ToList();

            int entryMethodClear = IndexOfStoreWithPrevious(instructions, "s_rebuildMethod", OpCodes.Ldnull);
            int entryFlagClear = IndexOfStoreWithPrevious(instructions, "s_rebuildBoundaryVerified", OpCodes.Ldc_I4_0);
            int identityCall = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "VerifyCoreRendererIdentity");
            Assert.True(identityCall >= 0, "PrepareProbe must verify core renderer identity");
            Assert.True(entryMethodClear >= 0 && entryMethodClear < identityCall,
                "s_rebuildMethod must be invalidated before validation begins");
            Assert.True(entryFlagClear >= 0 && entryFlagClear < identityCall,
                "s_rebuildBoundaryVerified must be invalidated before validation begins");

            int promote = IndexOfStoreWithPrevious(instructions, "s_rebuildBoundaryVerified", OpCodes.Ldc_I4_1);
            Assert.True(promote >= 0, "PrepareProbe must promote s_rebuildBoundaryVerified on success");
            int lastCall = instructions.FindLastIndex(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference);
            Assert.True(promote > lastCall,
                $"verified flag must be stored only after every validation call completes (store={promote} lastCall={lastCall})");
            int methodStore = instructions.FindLastIndex(i => IsStsfldNull(i, "s_rebuildMethod"));
            Assert.True(methodStore > lastCall && instructions[methodStore - 1].OpCode != OpCodes.Ldnull,
                "s_rebuildMethod must be assigned the validated MethodInfo after all validation calls complete");
        }

        [Fact]
        public void OnRendererRebuilt_ClearsTransientReferencesBeforeRetirementAndFirstReturn()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var method = CoordinatorMethod(assembly.MainModule, "OnRendererRebuilt");
            var instructions = method.Body.Instructions.ToList();

            int helperCall = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.Name == "TryRetireAfterIdle");
            int firstReturn = instructions.FindIndex(i => i.OpCode == OpCodes.Ret);
            Assert.True(helperCall >= 0 && firstReturn >= 0);

            foreach (string field in new[] { "s_previewComposite", "s_preparedMainFrame", "s_lastSnapshot" })
            {
                int clear = IndexOfStoreWithPrevious(instructions, field, OpCodes.Ldnull);
                Assert.True(clear >= 0 && clear < helperCall && clear < firstReturn,
                    $"{field} must be cleared before retirement and before the first return (clear={clear} helper={helperCall} ret={firstReturn})");
            }

            int axisStore = instructions.FindIndex(i =>
                i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference fr && fr.Name == "s_axisDiagnostic");
            Assert.True(axisStore >= 0 && axisStore < helperCall && axisStore < firstReturn,
                "stale axis data must be cleared before retirement and before the first return");

            int ownershipReset = instructions.FindIndex(i =>
                i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "KSARS.Visuals.MainAtlasOwnership" && mr.Name == "Reset");
            Assert.True(ownershipReset >= 0 && ownershipReset < helperCall && ownershipReset < firstReturn);

            foreach (string field in new[] { "s_composites", "s_mainFrames" })
            {
                for (int i = 1; i < helperCall; i++)
                {
                    Assert.False(IsStsfldNull(instructions[i], field)
                            && (instructions[i - 1].OpCode == OpCodes.Ldnull),
                        $"{field} must not be nulled before the retirement helper (retained-roots failure path)");
                }
            }
        }

        [Fact]
        public void TryResolveMainSyncSlot_ReportsInvalidationOnlyForRingReplacement()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var method = CoordinatorMethod(assembly.MainModule, "TryResolveMainSyncSlot");

            Assert.Equal(5, method.Parameters.Count);
            var invalidated = method.Parameters[4];
            Assert.True(invalidated.IsOut);
            Assert.Equal("System.Boolean&", invalidated.ParameterType.FullName);

            var instructions = method.Body.Instructions.ToList();
            var trueStores = instructions.Select((inst, i) => (inst, i))
                .Where(x => x.inst.OpCode == OpCodes.Stind_I1
                    && x.i >= 2
                    && instructions[x.i - 1].OpCode == OpCodes.Ldc_I4_1
                    && IsLdargParam(instructions[x.i - 2], invalidated.Index))
                .ToList();
            Assert.Single(trueStores);

            string? precedingString = instructions.Take(trueStores[0].i)
                .LastOrDefault(i => i.OpCode == OpCodes.Ldstr)?.Operand as string;
            Assert.NotNull(precedingString);
            Assert.Contains("sync ring replaced", precedingString);

            var falseStores = instructions.Select((inst, i) => (inst, i))
                .Where(x => x.inst.OpCode == OpCodes.Stind_I1
                    && x.i >= 2
                    && instructions[x.i - 1].OpCode == OpCodes.Ldc_I4_0
                    && IsLdargParam(instructions[x.i - 2], invalidated.Index))
                .ToList();
            Assert.NotEmpty(falseStores);
            Assert.DoesNotContain(instructions, i => IsStargParam(i, invalidated.Index));
        }

        [Fact]
        public void TryRecordMainComposite_TargetChangedExceptionMarksRetryFlag()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = CoordinatorMethod(assembly.MainModule, "TryRecordMainComposite");

            Assert.Contains(record.Body.Instructions, i =>
                i.OpCode == OpCodes.Isinst && i.Operand is TypeReference tr
                    && tr.Name == "RendererTargetChangedException");
            Assert.Contains(FieldRefsOf(record), f => f.Name == "s_mainRetryAfterRebuild");
        }

        [Fact]
        public void RenderHooks_InstallsRebuildPostfixAndRollsBackBothTargets()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var hooks = assembly.MainModule.GetType("KSARS.Visuals.RenderHooks")!;

            var tryInstall = hooks.Methods.Single(m => m.Name == "TryInstall");
            int patchCalls = tryInstall.Body.Instructions.Count(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "HarmonyLib.Harmony" && mr.Name == "Patch");
            Assert.Equal(7, patchCalls);

            var instructions = tryInstall.Body.Instructions.ToList();
            var unpatchCalls = instructions.Select((inst, i) => (inst, i))
                .Where(x => (x.inst.OpCode == OpCodes.Call || x.inst.OpCode == OpCodes.Callvirt)
                    && x.inst.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "HarmonyLib.Harmony" && mr.Name == "Unpatch")
                .ToList();
            Assert.Equal(3, unpatchCalls.Count);
            foreach (var (_, index) in unpatchCalls)
            {
                int constIndex = instructions.Take(index).ToList()
                    .FindLastIndex(i => i.OpCode == OpCodes.Ldc_I4 || i.OpCode == OpCodes.Ldc_I4_S
                        || (i.OpCode.Name ?? "").StartsWith("ldc.i4."));
                Assert.True(constIndex >= 0, $"Unpatch call at {index} lacks a HarmonyPatchType constant");

                int ldstrIndex = instructions.Take(index).ToList()
                    .FindLastIndex(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s == RenderHooks.OwnerId);
                Assert.True(ldstrIndex > constIndex,
                    $"Unpatch call at {index} is not passed the explicit visuals owner after the patch type");
            }

            var resolve = hooks.Methods.Single(m => m.Name == "ResolveProbeCoordinator");
            Assert.Equal(6, resolve.Parameters.Count);
            Assert.All(resolve.Parameters.Skip(1), p => Assert.True(p.IsOut));
            Assert.Equal("System.Reflection.MethodBase&", resolve.Parameters[1].ParameterType.FullName);
            Assert.Equal("System.Reflection.MethodBase&", resolve.Parameters[2].ParameterType.FullName);
            Assert.Equal("System.Reflection.MethodInfo&", resolve.Parameters[3].ParameterType.FullName);
            Assert.Equal("System.Reflection.MethodBase&", resolve.Parameters[4].ParameterType.FullName);
            Assert.Equal("System.Reflection.MethodInfo&", resolve.Parameters[5].ParameterType.FullName);
            Assert.Contains(resolve.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s == "OnRendererRebuilt");
            Assert.Contains(resolve.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s == "TranspileNativeDistanceData");
            Assert.Contains(resolve.Body.Instructions,
                i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s == "UpdateRenderData");
        }
    }
}
