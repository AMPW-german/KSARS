using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;
using KSARS.Visuals;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class NativeDistanceDataTests
    {
        static NativeDistanceDataTests()
        {
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                if (!BaselineContext.Enabled)
                {
                    return null;
                }

                string path = Path.Combine(
                    Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, name.Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }

        private static readonly Lazy<Assembly> Ksa = new(() => Assembly.LoadFrom(BaselineContext.KsaAssemblyPath));

        private static Type DistanceType => Ksa.Value.GetType("KSA.StaticCelestialDistanceRendering")!;

        private static MethodInfo TinyBeginMethod => typeof(RelativisticSpaceRenderer)
            .GetMethod("BeginNativeDistanceData", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static MethodInfo TinyFilterMethod => typeof(RelativisticSpaceRenderer)
            .GetMethod("FilterNativeDistantOrbiter", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static MethodInfo AppendStarsMethod => DistanceType
            .GetMethod("AppendStars", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static MethodInfo BlendMethod => DistanceType
            .GetMethod("SpriteSphereBlend", BindingFlags.Public | BindingFlags.Static)!;

        private static MethodInfo ScaleMethod => DistanceType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "GetApparentSizeScale" && m.GetParameters().Length == 1);

        private static MethodInfo DiameterMethod => Ksa.Value.GetType("KSA.Camera")!
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "GetObjectDiameterPixels");

        private static MethodInfo AddMethod => Ksa.Value.GetType("KSA.Rendering.DeviceVector")!
            .GetMethod("Add", BindingFlags.Public | BindingFlags.Instance)!;

        private static Type OrbiterType => Ksa.Value.GetType("KSA.IOrbiter")!;

        private static void WireTinyHelpers()
        {
            var type = typeof(RelativisticSpaceRenderer);
            type.GetField("s_tinyBegin", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, TinyBeginMethod);
            type.GetField("s_tinyFilter", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, TinyFilterMethod);
        }

        private static List<CodeInstruction> BaseFixture()
        {
            return new List<CodeInstruction>
            {
                new(OpCodes.Ldc_I4_1),
                new(OpCodes.Stloc_0),
                new(OpCodes.Call, AppendStarsMethod),
                new(OpCodes.Ldloc_0),
                new(OpCodes.Isinst, OrbiterType),
                new(OpCodes.Stloc_1),
                new(OpCodes.Callvirt, DiameterMethod),
                new(OpCodes.Call, BlendMethod),
                new(OpCodes.Call, ScaleMethod),
                new(OpCodes.Callvirt, AddMethod),
                new(OpCodes.Ret),
            };
        }

        private static List<CodeInstruction> Transpile(List<CodeInstruction> fixture)
        {
            WireTinyHelpers();
            return RelativisticSpaceRenderer.TranspileNativeDistanceData(fixture).ToList();
        }

        [RendererBaselineFact]
        public void Transpile_PrependsBeginAndReplacesUniqueCast()
        {
            var result = Transpile(BaseFixture());

            Assert.Equal(OpCodes.Ldarg_0, result[0].opcode);
            Assert.Equal(OpCodes.Ldarg_1, result[1].opcode);
            Assert.Equal(OpCodes.Call, result[2].opcode);
            Assert.Equal(TinyBeginMethod, result[2].operand as MethodInfo);

            Assert.DoesNotContain(result, i => i.opcode == OpCodes.Isinst && i.operand is Type t && t == OrbiterType);
            int filterIndex = result.FindIndex(i => i.opcode == OpCodes.Call && i.operand is MethodInfo m && m == TinyFilterMethod);
            Assert.True(filterIndex > 0, "filter call must be present");
            Assert.Equal(OpCodes.Ldarg_0, result[filterIndex - 2].opcode);
            Assert.Equal(OpCodes.Ldarg_1, result[filterIndex - 1].opcode);

            Assert.Equal(1, result.Count(i => i.operand is MethodInfo m && m == AppendStarsMethod));
            Assert.Equal(1, result.Count(i => i.operand is MethodInfo m && m == AddMethod));
        }

        [RendererBaselineFact]
        public void Transpile_TransfersEntryLabelsAndBlocks()
        {
            var fixture = BaseFixture();
            fixture[0].labels.Add(new Label());
            fixture[0].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));

            var result = Transpile(fixture);

            Assert.Single(result[0].labels);
            Assert.Single(result[0].blocks);
            Assert.Empty(result[3].labels);
            Assert.Empty(result[3].blocks);
        }

        [RendererBaselineFact]
        public void Transpile_TransfersCastLabelsAndBlocks()
        {
            var fixture = BaseFixture();
            int castIndex = fixture.FindIndex(i => i.opcode == OpCodes.Isinst);
            fixture[castIndex].labels.Add(new Label());
            fixture[castIndex].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginCatchBlock));

            var result = Transpile(fixture);

            int loadIndex = result.FindIndex(i =>
                i.opcode == OpCodes.Call && i.operand is MethodInfo m && m == TinyFilterMethod) - 2;
            Assert.True(loadIndex > 0);
            Assert.Single(result[loadIndex].labels);
            Assert.Single(result[loadIndex].blocks);
        }

        [RendererBaselineFact]
        public void Transpile_MissingCast_Throws()
        {
            var fixture = BaseFixture();
            fixture.RemoveAll(i => i.opcode == OpCodes.Isinst);
            Assert.Throws<InvalidOperationException>(() => Transpile(fixture));
        }

        [RendererBaselineFact]
        public void Transpile_DuplicateCast_Throws()
        {
            var fixture = BaseFixture();
            int castIndex = fixture.FindIndex(i => i.opcode == OpCodes.Isinst);
            fixture.Insert(castIndex + 1, new CodeInstruction(OpCodes.Isinst, OrbiterType));
            Assert.Throws<InvalidOperationException>(() => Transpile(fixture));
        }

        [RendererBaselineFact]
        public void Transpile_AlteredAnchorCounts_Throw()
        {
            var removals = new (string name, Predicate<CodeInstruction> match)[]
            {
                ("AppendStars", i => i.operand is MethodInfo m && m == AppendStarsMethod),
                ("GetObjectDiameterPixels", i => i.operand is MethodInfo m && m == DiameterMethod),
                ("SpriteSphereBlend", i => i.operand is MethodInfo m && m == BlendMethod),
                ("GetApparentSizeScale", i => i.operand is MethodInfo m && m == ScaleMethod),
                ("DeviceVector.Add", i => i.operand is MethodInfo m && m == AddMethod),
            };

            foreach (var (name, match) in removals)
            {
                var fixture = BaseFixture();
                Assert.True(fixture.RemoveAll(match) > 0, $"{name} not in fixture");
                Assert.Throws<InvalidOperationException>(() => Transpile(fixture));
            }

            var duplicated = BaseFixture();
            duplicated.Add(new CodeInstruction(OpCodes.Call, BlendMethod));
            Assert.Throws<InvalidOperationException>(() => Transpile(duplicated));
        }
    }
}
