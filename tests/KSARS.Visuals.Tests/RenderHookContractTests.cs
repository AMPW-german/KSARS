using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class RenderHookContractTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");

        private static AssemblyDefinition LoadModAssembly() => AssemblyDefinition.ReadAssembly(ModAssemblyPath);

        private static IEnumerable<MethodDefinition> AllMethods(ModuleDefinition module) =>
            module.GetTypes().SelectMany(t => t.Methods);

        private static int LocalIndexLoadedBefore(Mono.Collections.Generic.Collection<Instruction> instructions, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                int? local = LocalIndex(instructions[i]);
                if (local.HasValue && IsLoadLocal(instructions[i]))
                {
                    return local.Value;
                }
            }
            return -1;
        }

        private static int NextNonNop(Mono.Collections.Generic.Collection<Instruction> instructions, int index)
        {
            while (index < instructions.Count && instructions[index].OpCode == OpCodes.Nop)
            {
                index++;
            }
            return index;
        }

        private static bool IsStoreLocal(Instruction instruction) => instruction.OpCode.Code is
            Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3;

        private static bool IsLoadLocal(Instruction instruction) => instruction.OpCode.Code is
            Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3;

        private static int? LocalIndex(Instruction instruction)
        {
            var code = instruction.OpCode.Code;
            if (instruction.Operand is VariableReference variable)
            {
                return variable.Index;
            }
            return code switch
            {
                Code.Stloc_0 or Code.Ldloc_0 => 0,
                Code.Stloc_1 or Code.Ldloc_1 => 1,
                Code.Stloc_2 or Code.Ldloc_2 => 2,
                Code.Stloc_3 or Code.Ldloc_3 => 3,
                _ => null,
            };
        }

        [Fact]
        public void Init_UsesCreateClassProcessorAndPatch()
        {
            using var assembly = LoadModAssembly();
            var ksars = assembly.MainModule.GetType("KSARS.KSARS");
            Assert.NotNull(ksars);

            var init = ksars.Methods.Single(m => m.Name == "Init");
            var instructions = init.Body.Instructions;

            var calls = instructions
                .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference)
                .Select(i => (MethodReference)i.Operand)
                .ToList();

            Assert.Contains(calls, m => m.Name == "CreateClassProcessor" && m.DeclaringType.Name == "Harmony");
            Assert.Contains(calls, m => m.Name == "Patch" && m.DeclaringType.Name == "PatchClassProcessor");

            Assert.Contains(instructions,
                i => i.OpCode == OpCodes.Ldtoken && i.Operand is TypeReference t && t.FullName == "KSARS.KSARS");
        }

        [Fact]
        public void BeforeMain_HasBeforeMainAttributeAndCallsCoordinator()
        {
            using var assembly = LoadModAssembly();
            var ksars = assembly.MainModule.GetType("KSARS.KSARS");
            var beforeMain = ksars.Methods.Single(m => m.Name == "BeforeMain");

            Assert.Contains(beforeMain.CustomAttributes,
                a => a.AttributeType.FullName == "StarMap.API.StarMapBeforeMainAttribute");

            var instructions = beforeMain.Body.Instructions;
            var calls = instructions
                .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference)
                .Select(i => (MethodReference)i.Operand)
                .ToList();

            Assert.Contains(calls,
                m => m.DeclaringType.FullName == "KSARS.Visuals.RelativisticVisuals" && m.Name == "InspectBaseline");
            Assert.Contains(instructions,
                i => i.OpCode == OpCodes.Ldtoken && i.Operand is TypeReference t && t.FullName == "KSA.Program");
        }

        [Fact]
        public void HarmonyPatching_OnlyInInitAndRenderHooksInstaller()
        {
            using var assembly = LoadModAssembly();
            var patchEntryPoints = new[] { "Patch", "PatchAll", "PatchCategory", "CreateClassProcessor", "CreateAndPatchAll" };

            var installingMethods = AllMethods(assembly.MainModule)
                .Where(m => m.HasBody)
                .Where(m => m.Body.Instructions
                    .Where(i => i.Operand is MethodReference)
                    .Select(i => (MethodReference)i.Operand)
                    .Any(mr => mr.DeclaringType.Namespace == "HarmonyLib" && patchEntryPoints.Contains(mr.Name)))
                .Select(m => $"{m.DeclaringType.FullName}::{m.Name}")
                .ToList();

            Assert.Equal(
                new[]
                {
                    "KSARS.KSARS::Init",
                    "KSARS.Visuals.RelativisticSpaceRenderer::TryInstallLightingProbe",
                    "KSARS.Visuals.RelativisticSpaceRenderer::TryInstallMediaProbe",
                    "KSARS.Visuals.RelativisticSpaceRenderer::TryInstallMilkyWayProbe",
                    "KSARS.Visuals.RelativisticSpaceRenderer::TryInstallStarProbe",
                    "KSARS.Visuals.RelativisticSpaceRenderer::TryInstallVesselShadowProbe",
                    "KSARS.Visuals.RenderHooks::TryInstall",
                    "KSARS.Visuals.StarCatalogHooks::TryInstall",
                },
                installingMethods.OrderBy(n => n).ToArray());
        }

        [Fact]
        public void VisualsTypes_NoHarmonyAttributes_PatchingOnlyInInstaller()
        {
            using var assembly = LoadModAssembly();
            var visualsTypes = assembly.MainModule.GetTypes()
                .Where(t => t.Namespace == "KSARS.Visuals")
                .ToList();
            Assert.NotEmpty(visualsTypes);

            var harmonyCalls = visualsTypes
                .SelectMany(t => t.Methods)
                .Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions.Select(i => (Method: m, Operand: i.Operand as MethodReference)))
                .Where(x => x.Operand != null && x.Operand.DeclaringType.Namespace == "HarmonyLib"
                    && x.Operand.Name is "Patch" or "PatchAll" or "PatchCategory" or "CreateClassProcessor" or "CreateAndPatchAll")
                .ToList();
            Assert.All(harmonyCalls, x =>
                Assert.True(
                    ((x.Method.DeclaringType.FullName == "KSARS.Visuals.RenderHooks"
                        || x.Method.DeclaringType.FullName == "KSARS.Visuals.StarCatalogHooks")
                        && x.Method.Name == "TryInstall")
                    || (x.Method.DeclaringType.FullName == "KSARS.Visuals.RelativisticSpaceRenderer"
                        && (x.Method.Name == "TryInstallStarProbe" || x.Method.Name == "TryInstallMilkyWayProbe"
                            || x.Method.Name == "TryInstallLightingProbe"
                            || x.Method.Name == "TryInstallMediaProbe"
                            || x.Method.Name == "TryInstallVesselShadowProbe")),
                    $"{x.Method.DeclaringType.FullName}::{x.Method.Name} installs Harmony patches"));

            var harmonyAttributes = visualsTypes
                .SelectMany(t => t.CustomAttributes
                    .Concat(t.Methods.SelectMany(m => m.CustomAttributes))
                    .Concat(t.Fields.SelectMany(f => f.CustomAttributes)))
                .Where(a => a.AttributeType.Namespace == "HarmonyLib")
                .ToList();
            Assert.Empty(harmonyAttributes);
        }

        [Fact]
        public void RenderHooks_InstallsWithVisualsOwner()
        {
            using var assembly = LoadModAssembly();
            var hooks = assembly.MainModule.GetType("KSARS.Visuals.RenderHooks");
            Assert.NotNull(hooks);
            var tryInstall = hooks.Methods.Single(m => m.Name == "TryInstall");
            var instructions = tryInstall.Body.Instructions;

            int harmonyCtorIndex = instructions.ToList().FindIndex(
                i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "HarmonyLib.Harmony");
            Assert.True(harmonyCtorIndex >= 0, "Harmony instance not constructed in TryInstall");

            var ownerStrings = instructions.Take(harmonyCtorIndex + 1)
                .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s == "KSARS.Visuals")
                .ToList();
            Assert.NotEmpty(ownerStrings);

            int patchCalls = instructions.Count(
                i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "HarmonyLib.Harmony" && mr.Name == "Patch");
            Assert.Equal(7, patchCalls);
        }

        [Fact]
        public void RenderHooks_UnpatchAll_AlwaysPassesExplicitOwner()
        {
            using var assembly = LoadModAssembly();
            var hooks = assembly.MainModule.GetType("KSARS.Visuals.RenderHooks");
            Assert.NotNull(hooks);

            var unpatchCalls = hooks.Methods
                .Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions.Select((inst, index) => (Method: m, Index: index, Inst: inst)))
                .Where(x => x.Inst.Operand is MethodReference mr
                    && mr.Name == "UnpatchAll" && mr.DeclaringType.FullName == "HarmonyLib.Harmony")
                .ToList();

            Assert.Equal(2, unpatchCalls.Count);
            foreach (var call in unpatchCalls)
            {
                var previous = call.Method.Body.Instructions[call.Index - 1];
                Assert.True(
                    previous.OpCode == OpCodes.Ldstr && (string)previous.Operand == RenderHooks.OwnerId,
                    $"UnpatchAll in {call.Method.Name} is not passed the explicit visuals owner");
            }
        }

        [Fact]
        public void Assembly_NeverCallsPatchAll()
        {
            using var assembly = LoadModAssembly();
            var patchAllCalls = AllMethods(assembly.MainModule)
                .Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions)
                .Where(i => i.Operand is MethodReference mr && mr.Name == "PatchAll")
                .ToList();

            Assert.Empty(patchAllCalls);
        }

        public static IEnumerable<object[]> HarmonyPatchMethods => new[]
        {
            new object[] { "VehicleUpdateTask_DetectStructuralFailure_Prefix", "PhysicsBubble", "DetectStructuralFailure", "HarmonyPrefix", true },
            new object[] { "FlightComputer_SolveGLoadThrottleCap_Postfix", "FlightComputer", "SolveGLoadThrottleCap", "HarmonyPostfix", true },
            new object[] { "PhysicsStates_IntegrateVelocityVerlet_Prefix", "PhysicsStates", "IntegrateVelocityVerlet", "HarmonyPrefix", false },
            new object[] { "PhysicsStates_IntegrateVelocityVerlet_Postfix", "PhysicsStates", "IntegrateVelocityVerlet", "HarmonyPostfix", false },
        };

        [Theory]
        [MemberData(nameof(HarmonyPatchMethods))]
        public void HarmonyPatchMethod_RetainsExactMetadata(string methodName, string targetTypeName, string targetMethodName, string processorAttributeName, bool isPublic)
        {
            using var assembly = LoadModAssembly();
            var ksars = assembly.MainModule.GetType("KSARS.KSARS");
            var method = ksars.Methods.Single(m => m.Name == methodName);

            Assert.True(method.IsStatic);
            Assert.Equal(isPublic, method.IsPublic);

            var patchAttribute = method.CustomAttributes.Single(a => a.AttributeType.Name == "HarmonyPatch");
            Assert.Equal(2, patchAttribute.ConstructorArguments.Count);

            var targetType = patchAttribute.ConstructorArguments[0].Value as TypeReference;
            Assert.NotNull(targetType);
            Assert.Equal(targetTypeName, targetType.Name);

            Assert.Equal(targetMethodName, patchAttribute.ConstructorArguments[1].Value as string);

            Assert.Contains(method.CustomAttributes, a => a.AttributeType.Name == processorAttributeName);
        }

#if DEBUG
        [Fact]
        public void AfterGui_CopyDiagnosticsButton_GuardsClipboardWrite()
        {
            using var assembly = LoadModAssembly();
            var ksars = assembly.MainModule.GetType("KSARS.KSARS");
            Assert.NotNull(ksars);
            var afterGui = ksars.Methods.Single(m => m.Name == "AfterGui");
            var instructions = afterGui.Body.Instructions;

            int buttonIndex = instructions.ToList().FindIndex(
                i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr
                    && mr.DeclaringType.Name == "ImGui" && mr.Name == "Button");
            Assert.True(buttonIndex >= 0, "AfterGui does not call ImGui.Button");

            var labelInstruction = instructions.Take(buttonIndex).LastOrDefault(i => i.OpCode == OpCodes.Ldstr);
            Assert.True(labelInstruction?.Operand is string label && label == "Copy diagnostics",
                "button label must be the exact literal 'Copy diagnostics'");

            int clipboardIndex = instructions.ToList().FindIndex(
                i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr
                    && mr.DeclaringType.Name == "ImGui" && mr.Name == "SetClipboardText");
            Assert.True(clipboardIndex > buttonIndex, "SetClipboardText must follow the Button call");

            int gateIndex = NextNonNop(instructions, buttonIndex + 1);
            Assert.True(gateIndex < instructions.Count, "no instruction follows the Button call");
            var gate = instructions[gateIndex];
            if (IsStoreLocal(gate))
            {
                int storedLocal = LocalIndex(gate)!.Value;
                int reloadIndex = NextNonNop(instructions, gateIndex + 1);
                Assert.True(reloadIndex < instructions.Count
                    && IsLoadLocal(instructions[reloadIndex])
                    && LocalIndex(instructions[reloadIndex]) == storedLocal,
                    "the Button result must be reloaded from its stored local before the gate branch");
                gateIndex = NextNonNop(instructions, reloadIndex + 1);
                Assert.True(gateIndex < instructions.Count, "no branch follows the Button result reload");
                gate = instructions[gateIndex];
            }

            Assert.True(gate.OpCode == OpCodes.Brfalse || gate.OpCode == OpCodes.Brfalse_S,
                "the Button result must gate the clipboard write with a conditional branch");
            var skipTarget = Assert.IsType<Instruction>(gate.Operand);
            Assert.True(instructions.IndexOf(skipTarget) > clipboardIndex,
                "the Button-false path must skip SetClipboardText");

            int clipboardLocal = LocalIndexLoadedBefore(instructions, clipboardIndex);
            Assert.True(clipboardLocal >= 0, "SetClipboardText is not fed by a stored local");

            int matchingTextLocal = instructions
                .Select((inst, index) => (inst, index))
                .Where(x => x.inst.OpCode == OpCodes.Call && x.inst.Operand is MethodReference mr
                    && mr.DeclaringType.Name == "ImGui" && mr.Name == "Text")
                .Select(x => LocalIndexLoadedBefore(instructions, x.index))
                .FirstOrDefault(local => local == clipboardLocal, -1);
            Assert.Equal(clipboardLocal, matchingTextLocal);
        }
#endif

        [Fact]
        public void PhysicsConstants_SpeedOfLightIsExact()
        {
            using var assembly = LoadModAssembly();

            var constants = assembly.MainModule.GetType("KSARS.PhysicsConstants");
            Assert.NotNull(constants);
            Assert.False(constants.IsPublic);

            var sharedField = constants.Fields.Single(f => f.Name == "SpeedOfLight");
            Assert.True(sharedField.IsLiteral);
            Assert.Equal(299792458.0, Assert.IsType<double>(sharedField.Constant));

            var ksars = assembly.MainModule.GetType("KSARS.KSARS");
            var aliasField = ksars.Fields.Single(f => f.Name == "SpeedOfLight");
            Assert.True(aliasField.IsLiteral);
            Assert.True(aliasField.IsPrivate);
            Assert.Equal(299792458.0, Assert.IsType<double>(aliasField.Constant));
        }
    }
}
