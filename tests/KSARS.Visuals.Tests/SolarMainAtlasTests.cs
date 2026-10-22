using System.Numerics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace KSARS.Visuals.Tests
{
    public class SolarMainAtlasTests
    {
        private static readonly string ModAssemblyPath = Path.Combine(AppContext.BaseDirectory, "KSARS.dll");
        private const string PassType = "KSARS.Visuals.AtlasMainPass";
        private const string RendererType = "KSARS.Visuals.RelativisticSpaceRenderer";
        private const string FrameType = "KSARS.Visuals.RelativisticSpaceRenderer/MainAtlasFrame";
        private const string SolarSlotType = "KSARS.Visuals.RelativisticSpaceRenderer/MainAtlasFrame/SolarSlot";

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

        private static List<(string DeclaringType, string Name)> FieldRefsOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => i.Operand is FieldReference)
                .Select(i =>
                {
                    var fr = (FieldReference)i.Operand;
                    return (fr.DeclaringType.FullName, fr.Name);
                })
                .ToList();

        private static List<string> FieldStoresOf(MethodDefinition method) =>
            method.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld)
                    && i.Operand is FieldReference)
                .Select(i => ((FieldReference)i.Operand).Name)
                .ToList();

        private static TypeDefinition TypeOf(ModuleDefinition module, string name)
        {
            var type = module.GetType(name) ?? module.Types.SelectMany(AllNested).FirstOrDefault(t => t.FullName == name);
            Assert.NotNull(type);
            return type;
        }

        private static IEnumerable<TypeDefinition> AllNested(TypeDefinition type)
        {
            foreach (TypeDefinition nested in type.NestedTypes)
            {
                yield return nested;
                foreach (TypeDefinition deeper in AllNested(nested))
                {
                    yield return deeper;
                }
            }
        }

        private static MethodDefinition SingleMethod(ModuleDefinition module, string typeName, string methodName) =>
            TypeOf(module, typeName).Methods.Single(m => m.Name == methodName);

        private static int CountCalls(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.Count(c => c.DeclaringType == declaringType && c.Name == name);

        private static int IndexOfCall(List<(string DeclaringType, string Name)> calls, string declaringType, string name) =>
            calls.FindIndex(c => c.DeclaringType == declaringType && c.Name == name);

        private static int? IntConstant(Instruction instruction)
        {
            if (instruction.OpCode == OpCodes.Ldc_I4_M1) return -1;
            if (instruction.OpCode == OpCodes.Ldc_I4_0) return 0;
            if (instruction.OpCode == OpCodes.Ldc_I4_1) return 1;
            if (instruction.OpCode == OpCodes.Ldc_I4_2) return 2;
            if (instruction.OpCode == OpCodes.Ldc_I4_3) return 3;
            if (instruction.OpCode == OpCodes.Ldc_I4_4) return 4;
            if (instruction.OpCode == OpCodes.Ldc_I4_5) return 5;
            if (instruction.OpCode == OpCodes.Ldc_I4_6) return 6;
            if (instruction.OpCode == OpCodes.Ldc_I4_7) return 7;
            if (instruction.OpCode == OpCodes.Ldc_I4_8) return 8;
            if (instruction.OpCode == OpCodes.Ldc_I4_S) return (sbyte)instruction.Operand;
            if (instruction.OpCode == OpCodes.Ldc_I4) return (int)instruction.Operand;
            return null;
        }

        private static bool StoresConstant(MethodDefinition method, int value, string fieldName) =>
            StoresConstantIn(method.Body.Instructions, value, fieldName);

        private static bool StoresConstantIn(IList<Instruction> instructions, int value, string fieldName)
        {
            for (int i = 0; i + 1 < instructions.Count; i++)
            {
                if (IntConstant(instructions[i]) == value
                    && instructions[i + 1].OpCode == OpCodes.Stfld
                    && instructions[i + 1].Operand is FieldReference fr && fr.Name == fieldName)
                {
                    return true;
                }
            }

            return false;
        }

        private readonly struct ReplicaTile
        {
            internal ReplicaTile(Vector3 right, Vector3 up, Vector3 fwd, float tanHalf,
                float near, float far, bool isSolar, float distance, bool coronaEnabled,
                Vector4 color, float depth)
            {
                Right = right;
                Up = up;
                Fwd = fwd;
                TanHalf = tanHalf;
                Near = near;
                Far = far;
                IsSolar = isSolar;
                Distance = distance;
                CoronaEnabled = coronaEnabled;
                Color = color;
                Depth = depth;
            }

            internal Vector3 Right { get; }
            internal Vector3 Up { get; }
            internal Vector3 Fwd { get; }
            internal float TanHalf { get; }
            internal float Near { get; }
            internal float Far { get; }
            internal bool IsSolar { get; }
            internal float Distance { get; }
            internal bool CoronaEnabled { get; }
            internal Vector4 Color { get; }
            internal float Depth { get; }
        }

        private static ReplicaTile BodyTile(float near, float far, Vector4 color, float depth,
            Vector3? fwd = null, float tanHalf = 0.1f) =>
            new(Vector3.UnitX, Vector3.UnitY, fwd ?? Vector3.UnitZ, tanHalf, near, far,
                isSolar: false, distance: 0f, coronaEnabled: false, color, depth);

        private static ReplicaTile SolarTile(float distance, float near, float far,
            Vector4 color, float depth, float tanHalf = 0.1f, bool coronaEnabled = true) =>
            new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, tanHalf, near, far,
                isSolar: true, distance, coronaEnabled, color, depth);

        private static bool AnyNonFinite(Vector4 v) =>
            !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || !float.IsFinite(v.W);

        private static bool ReplicaComposite(IReadOnlyList<ReplicaTile> tiles, Vector3 nRest,
            float doppler, out Vector3 rgb, out float alpha)
        {
            rgb = Vector3.Zero;
            alpha = 0f;
            float bestRadial = 3.0e38f;
            Vector3 bestRgb = Vector3.Zero;
            float bestAlpha = 0f;
            float coronaRadial = 3.0e38f;
            Vector3 coronaRgb = Vector3.Zero;
            float coronaAlpha = 0f;
            foreach (ReplicaTile tile in tiles)
            {
                float z = Vector3.Dot(nRest, tile.Fwd);
                if (!(z > 0f))
                {
                    continue;
                }

                float inv = 1f / (z * tile.TanHalf);
                var uv = new Vector2(0.5f * (1f + Vector3.Dot(nRest, tile.Right) * inv),
                                     0.5f * (1f - Vector3.Dot(nRest, tile.Up) * inv));
                if (float.IsNaN(uv.X) || float.IsNaN(uv.Y)
                    || uv.X < 0f || uv.X > 1f || uv.Y < 0f || uv.Y > 1f)
                {
                    continue;
                }

                float depth = tile.Depth;
                Vector4 c = tile.Color;
                if (AnyNonFinite(c))
                {
                    continue;
                }

                bool isSolar = tile.IsSolar;
                if (depth > 0f && depth <= 1f)
                {
                    float ratio = tile.Near / tile.Far;
                    float a = ratio / (1f - ratio);
                    float b = tile.Near / (1f - ratio);
                    float sourceZ = b / (depth + a);
                    Vector2 rayXY = (2f * uv - Vector2.One) * tile.TanHalf;
                    float radial = sourceZ * MathF.Sqrt(1f + Vector2.Dot(rayXY, rayXY));
                    if (!(radial > 0f))
                    {
                        continue;
                    }

                    if (radial < bestRadial)
                    {
                        bestRadial = radial;
                        bestRgb = new Vector3(c.X, c.Y, c.Z);
                        bestAlpha = 1f;
                    }

                    continue;
                }

                if (isSolar && depth == 0f && tile.CoronaEnabled)
                {
                    float coverage = Math.Clamp(c.W, 0f, 1f);
                    Vector2 coronaXY = (2f * uv - Vector2.One) * tile.TanHalf;
                    float radial = tile.Distance * MathF.Sqrt(1f + Vector2.Dot(coronaXY, coronaXY));
                    if (coverage > 0f && radial > 0f && float.IsFinite(radial)
                        && radial < coronaRadial)
                    {
                        coronaRadial = radial;
                        coronaRgb = new Vector3(c.X, c.Y, c.Z);
                        coronaAlpha = coverage;
                    }
                }
            }

            if (coronaAlpha > 0f && coronaRadial < bestRadial)
            {
                rgb = coronaRgb + (1f - coronaAlpha) * bestRgb;
                alpha = coronaAlpha + (1f - coronaAlpha) * bestAlpha;
            }
            else
            {
                rgb = bestRgb;
                alpha = bestAlpha;
            }

            if (!(alpha > 0f))
            {
                return false;
            }

            float scale = doppler * doppler * doppler;
            rgb *= scale;
            if (float.IsNaN(rgb.X) || float.IsNaN(rgb.Y) || float.IsNaN(rgb.Z))
            {
                return false;
            }

            rgb = Vector3.Clamp(rgb, Vector3.Zero, new Vector3(65504f));
            return true;
        }

        private static (Vector3 RestDir, float Doppler) ReplicaInverse(Vector3 nObs, Vector3 u, float q)
        {
            Vector3 nRest = nObs;
            float doppler = 1.0f;
            if (q != 1.0f)
            {
                float qq = q * q;
                float a = 0.5f * Vector3.Dot(nObs - u, nObs - u);
                float b = 0.5f * Vector3.Dot(nObs + u, nObs + u);
                Vector3 nPerp = nObs - Vector3.Dot(nObs, u) * u;
                nRest = Vector3.Normalize(u * (qq * b - a) + nPerp * (2f * q));
                doppler = 2f * q / (a + qq * b);
            }

            return (nRest, doppler);
        }

        private static bool TryRadialFromDepth(float near, float far, float depth, out float radial)
        {
            float ratio = near / far;
            float a = ratio / (1f - ratio);
            float b = near / (1f - ratio);
            radial = b / (depth + a);
            return depth > 0f && depth <= 1f && radial > 0f && float.IsFinite(radial);
        }

        [Fact]
        public void Replica_Q1Identity_PreservesDirectionAndComposite()
        {
            (Vector3 nRest, float doppler) = ReplicaInverse(Vector3.UnitZ, new Vector3(0.9f, 0f, 0f), 1.0f);
            Assert.Equal(Vector3.UnitZ, nRest);
            Assert.Equal(1.0f, doppler);

            var body = BodyTile(100f, 300f, new Vector4(0.2f, 0.4f, 0.8f, 1f), depth: 0.5f);
            Assert.True(ReplicaComposite(new[] { body }, nRest, doppler, out Vector3 rgb, out float alpha));
            Assert.Equal(new Vector3(0.2f, 0.4f, 0.8f), rgb);
            Assert.Equal(1.0f, alpha);
        }

        [Fact]
        public void Replica_OpaqueSolarBeatsBehindBody_AndBodyBeatsBehindSolar()
        {
            var solarOpaque = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(1f, 0.8f, 0.4f, 1f), depth: 0.5f);
            Assert.True(TryRadialFromDepth(50f, 150f, 0.5f, out float solarRadial));

            var nearBody = BodyTile(20f, 80f, new Vector4(0.1f, 0.2f, 0.9f, 1f), depth: 0.5f);
            Assert.True(TryRadialFromDepth(20f, 80f, 0.5f, out float nearBodyRadial));
            Assert.True(nearBodyRadial < solarRadial, "test requires the body sample radially nearer than the solar disc");

            Assert.True(ReplicaComposite(new[] { nearBody, solarOpaque }, Vector3.UnitZ, 1f,
                out Vector3 rgbNear, out float alphaNear));
            Assert.Equal(new Vector3(0.1f, 0.2f, 0.9f), rgbNear);
            Assert.Equal(1.0f, alphaNear);

            var farBody = BodyTile(200f, 800f, new Vector4(0.1f, 0.2f, 0.9f, 1f), depth: 0.5f);
            Assert.True(TryRadialFromDepth(200f, 800f, 0.5f, out float farBodyRadial));
            Assert.True(solarRadial < farBodyRadial, "test requires the solar disc radially nearer than the body");

            Assert.True(ReplicaComposite(new[] { farBody, solarOpaque }, Vector3.UnitZ, 1f,
                out Vector3 rgbFar, out float alphaFar));
            Assert.Equal(new Vector3(1f, 0.8f, 0.4f), rgbFar);
            Assert.Equal(1.0f, alphaFar);
        }

        [Fact]
        public void Replica_OutsidePhysicalR_DepthZeroIsCoronaNotOpaque_EvenWithAlphaOne()
        {
            var solarCorona = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(1f, 0.9f, 0.6f, 1f), depth: 0f);
            var nearerBody = BodyTile(20f, 80f, new Vector4(0.1f, 0.2f, 0.9f, 1f), depth: 0.5f);
            Assert.True(TryRadialFromDepth(20f, 80f, 0.5f, out float bodyRadial));
            Assert.True(bodyRadial < 100f,
                "test requires the body radially nearer than the solar center-plane corona distance");

            Assert.True(ReplicaComposite(new[] { nearerBody, solarCorona }, Vector3.UnitZ, 1f,
                out Vector3 rgb, out float alpha));
            Assert.Equal(new Vector3(0.1f, 0.2f, 0.9f), rgb);
            Assert.Equal(1.0f, alpha);
        }

        [Fact]
        public void Replica_PremultipliedCoronaOverOpaque_RequiredValues()
        {
            var solarCorona = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(0.2f, 0.1f, 0f, 0.25f), depth: 0f);
            var behindBody = BodyTile(200f, 800f, new Vector4(0.4f, 0.4f, 0.4f, 1f), depth: 0.5f);
            Assert.True(TryRadialFromDepth(200f, 800f, 0.5f, out float bodyRadial));
            Assert.True(100f < bodyRadial,
                "test requires the solar corona center-plane radial nearer than the body");

            Assert.True(ReplicaComposite(new[] { behindBody, solarCorona }, Vector3.UnitZ, 1f,
                out Vector3 rgb, out float alpha));
            Assert.Equal(new Vector3(0.5f, 0.4f, 0.3f), rgb);
            Assert.Equal(1.0f, alpha);
        }

        [Fact]
        public void Replica_CoronaBehindOpaque_Omitted()
        {
            var solarCorona = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(1f, 0.9f, 0.6f, 0.8f), depth: 0f);
            var nearerBody = BodyTile(20f, 80f, new Vector4(0.1f, 0.2f, 0.9f, 1f), depth: 0.5f);

            Assert.True(ReplicaComposite(new[] { nearerBody, solarCorona }, Vector3.UnitZ, 1f,
                out Vector3 rgb, out float alpha));
            Assert.Equal(new Vector3(0.1f, 0.2f, 0.9f), rgb);
            Assert.Equal(1.0f, alpha);
        }

        [Fact]
        public void Replica_CoronaBeyondNativeMeshDistance_Omitted()
        {
            var solarCorona = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(1f, 0.9f, 0.6f, 0.8f), depth: 0f, coronaEnabled: false);
            var disc = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(1f, 0.95f, 0.9f, 1f), depth: 0.5f, coronaEnabled: false);

            Assert.False(ReplicaComposite(new[] { solarCorona }, Vector3.UnitZ, 1f,
                out _, out _), "corona must be discarded entirely when the native mesh would not draw");
            Assert.True(ReplicaComposite(new[] { solarCorona, disc }, Vector3.UnitZ, 1f,
                out Vector3 rgb, out float alpha));
            Assert.Equal(new Vector3(1f, 0.95f, 0.9f), rgb);
            Assert.Equal(1.0f, alpha);
        }

        [Fact]
        public void Replica_CoronaOnly_RetainsNativeAlpha()
        {
            var solarCorona = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(0.6f, 0.5f, 0.4f, 0.25f), depth: 0f);
            Assert.True(ReplicaComposite(new[] { solarCorona }, Vector3.UnitZ, 1f,
                out Vector3 rgb, out float alpha));
            Assert.Equal(new Vector3(0.6f, 0.5f, 0.4f), rgb);
            Assert.Equal(0.25f, alpha);
        }

        [Fact]
        public void Replica_PhysicalRCoverage_BlocksEvenWhenRawAlphaZero()
        {
            var solarOpaqueZeroAlpha = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(0.9f, 0.7f, 0.3f, 0f), depth: 0.5f);
            var behindBody = BodyTile(200f, 800f, new Vector4(0.4f, 0.4f, 0.4f, 1f), depth: 0.5f);

            Assert.True(ReplicaComposite(new[] { behindBody, solarOpaqueZeroAlpha }, Vector3.UnitZ, 1f,
                out Vector3 rgb, out float alpha));
            Assert.Equal(new Vector3(0.9f, 0.7f, 0.3f), rgb);
            Assert.Equal(1.0f, alpha);
        }

        [Fact]
        public void Replica_RejectsBackwardOutsideUvInvalidDepthAndNonFinite()
        {
            var body = BodyTile(100f, 300f, new Vector4(0.5f, 0.5f, 0.5f, 1f), depth: 0.5f);
            Assert.False(ReplicaComposite(new[] { body }, -Vector3.UnitZ, 1f, out _, out _),
                "backward direction must reject");

            var edgeBody = new ReplicaTile(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 0.1f,
                100f, 300f, false, 0f, false, new Vector4(0.5f, 0.5f, 0.5f, 1f), 0.5f);
            Vector3 outside = Vector3.Normalize(new Vector3(0.5f, 0f, 1f));
            Assert.False(ReplicaComposite(new[] { edgeBody }, outside, 1f, out _, out _),
                "direction outside the tile cap must reject");

            foreach (float badDepth in new[] { float.NaN, -0.1f, 1.0000001f })
            {
                var invalid = BodyTile(100f, 300f, new Vector4(0.5f, 0.5f, 0.5f, 1f), badDepth);
                Assert.False(ReplicaComposite(new[] { invalid }, Vector3.UnitZ, 1f, out _, out _),
                    $"body depth {badDepth} must reject");
            }

            var nanColor = BodyTile(100f, 300f, new Vector4(float.NaN, 0.5f, 0.5f, 1f), 0.5f);
            Assert.False(ReplicaComposite(new[] { nanColor }, Vector3.UnitZ, 1f, out _, out _),
                "nonfinite sampled color must reject");

            foreach (float inf in new[] { float.PositiveInfinity, float.NegativeInfinity })
            {
                var infColor = BodyTile(100f, 300f, new Vector4(inf, 0.5f, 0.5f, 1f), 0.5f);
                Assert.False(ReplicaComposite(new[] { infColor }, Vector3.UnitZ, 1f, out _, out _),
                    $"infinite sampled color {inf} must reject at the sample stage");
                var infAlpha = SolarTile(100f, 50f, 150f, new Vector4(0.6f, 0.5f, 0.4f, inf), 0f);
                Assert.False(ReplicaComposite(new[] { infAlpha }, Vector3.UnitZ, 1f, out _, out _),
                    $"infinite sampled alpha {inf} must reject at the sample stage");
            }

            foreach (float badSolarDepth in new[] { float.NaN, float.PositiveInfinity, -0.1f, 1.5f })
            {
                var solar = SolarTile(100f, 50f, 150f, new Vector4(1f, 0.9f, 0.6f, 0.8f), badSolarDepth);
                Assert.False(ReplicaComposite(new[] { solar }, Vector3.UnitZ, 1f, out _, out _),
                    $"solar depth {badSolarDepth} must not become corona either");
            }

            var nanCorona = SolarTile(100f, 50f, 150f, new Vector4(float.NaN, 0.5f, 0.4f, 0.8f), 0f);
            Assert.False(ReplicaComposite(new[] { nanCorona }, Vector3.UnitZ, 1f, out _, out _),
                "nonfinite corona rgb must reject");
        }

        [Fact]
        public void Replica_DopplerScalesOnlyColorNeverOpacity()
        {
            var solarCorona = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(0.6f, 0.5f, 0.4f, 0.25f), depth: 0f);
            var body = BodyTile(200f, 800f, new Vector4(0.4f, 0.4f, 0.4f, 1f), depth: 0.5f);
            var tiles = new[] { body, solarCorona };

            Assert.True(ReplicaComposite(tiles, Vector3.UnitZ, 1f, out Vector3 rgbQ1, out float alphaQ1));
            const float doppler = 1.2f;
            Assert.True(ReplicaComposite(tiles, Vector3.UnitZ, doppler, out Vector3 rgbBoost, out float alphaBoost));

            Assert.Equal(alphaQ1, alphaBoost);
            float scale = doppler * doppler * doppler;
            Assert.Equal(rgbQ1 * scale, rgbBoost);
            Assert.Equal(1.0f, alphaBoost);
        }

        [Fact]
        public void Replica_SolarOnlyComposition_AndTwoBodyPlusSolarCapacity()
        {
            var solar = SolarTile(distance: 100f, near: 50f, far: 150f,
                color: new Vector4(0.9f, 0.7f, 0.3f, 1f), depth: 0.5f);
            Assert.True(ReplicaComposite(new[] { solar }, Vector3.UnitZ, 1f, out Vector3 rgb, out float alpha));
            Assert.Equal(new Vector3(0.9f, 0.7f, 0.3f), rgb);
            Assert.Equal(1.0f, alpha);

            var bodyA = BodyTile(200f, 800f, new Vector4(0.2f, 0.2f, 0.2f, 1f), 0.5f);
            var bodyB = BodyTile(400f, 1600f, new Vector4(0.1f, 0.1f, 0.1f, 1f), 0.5f);
            Assert.True(ReplicaComposite(new[] { bodyA, bodyB, solar }, Vector3.UnitZ, 1f,
                out Vector3 rgb3, out float alpha3));
            Assert.Equal(new Vector3(0.9f, 0.7f, 0.3f), rgb3);
            Assert.Equal(1.0f, alpha3);
        }

        [Fact]
        public void PassConstants_MaxBodiesTwoMaxSourcesThree()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var pass = TypeOf(assembly.MainModule, PassType);

            Assert.Equal(2, (int)pass.Fields.Single(f => f.Name == "MaxBodies").Constant!);
            Assert.Equal(3, (int)pass.Fields.Single(f => f.Name == "MaxSources").Constant!);
        }

        [Fact]
        public void FragmentSource_PinsSolarCoverageCoronaAndPremultipliedComposition()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var pass = TypeOf(assembly.MainModule, PassType);
            string source = (string)pass.Fields.Single(f => f.Name == "FragmentSource").Constant!;

            Assert.Contains("vec4 sourceInfo;", source);
            Assert.Contains("AtlasBody bodies[3]", source);
            Assert.Contains("sampler2D colorTiles[3]", source);
            Assert.Contains("sampler2D depthTiles[3]", source);
            Assert.Contains("atlas.bodies[i].sourceInfo.x > 0.5", source);
            Assert.Contains("atlas.bodies[i].sourceInfo.z > 0.5", source);
            Assert.Contains("depth == 0.0", source);
            Assert.Contains("any(isnan(c)) || any(isinf(c))", source);
            int sampleCheck = source.IndexOf("any(isnan(c)) || any(isinf(c))", StringComparison.Ordinal);
            int opaqueBranch = source.IndexOf("depth > 0.0 && depth <= 1.0", StringComparison.Ordinal);
            Assert.True(sampleCheck >= 0 && sampleCheck < opaqueBranch,
                "nonfinite sampled color including alpha must reject at the sample stage before HDR math");
            Assert.Contains("clamp(c.a, 0.0, 1.0)", source);
            Assert.Contains("atlas.bodies[i].sourceInfo.y * sqrt(1.0 + dot(coronaXY, coronaXY))", source);
            Assert.Contains("coronaRgb + (1.0 - coronaAlpha) * bestRgb", source);
            Assert.Contains("coronaAlpha + (1.0 - coronaAlpha) * bestAlpha", source);
            Assert.Contains("!(alpha > 0.0)", source);
            Assert.Contains("rgb *= scale;", source);
            Assert.Contains("vec4(rgb, alpha)", source);
            Assert.Contains("sourceZ = b / (depth + a)", source);
            Assert.Contains("discard;", source);
            Assert.Contains("min(max(rgb, vec3(0.0)), vec3(65504.0))", source);
            Assert.DoesNotContain("2.75", source);
            Assert.DoesNotContain("vec4(rgb, 1.0)", source);
            int scaleIndex = source.IndexOf("rgb *= scale;", StringComparison.Ordinal);
            Assert.DoesNotContain("alpha *", source.Substring(scaleIndex));
        }

        [Fact]
        public void MainTileSource_CarriesSolarFlagAndDistance()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var tile = TypeOf(assembly.MainModule, "KSARS.Visuals.MainTileSource");

            var solarProp = tile.Properties.SingleOrDefault(p => p.Name == "IsSolar");
            var distanceProp = tile.Properties.SingleOrDefault(p => p.Name == "Distance");
            var coronaProp = tile.Properties.SingleOrDefault(p => p.Name == "CoronaEnabled");
            Assert.NotNull(solarProp);
            Assert.NotNull(distanceProp);
            Assert.NotNull(coronaProp);
            Assert.Equal("System.Boolean", solarProp.PropertyType.FullName);
            Assert.Equal("System.Double", distanceProp.PropertyType.FullName);
            Assert.Equal("System.Boolean", coronaProp.PropertyType.FullName);
        }

        [Fact]
        public void SyncSources_ValidatesCapacitySolarUniquenessAndDistance()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var sync = SingleMethod(assembly.MainModule, PassType, "SyncSources");
            var instructions = sync.Body.Instructions;

            Assert.True(instructions.Count(i => i.OpCode == OpCodes.Newobj
                    && i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "System.ArgumentOutOfRangeException") >= 3,
                "SyncSources must throw on capacity, solar/body limits and invalid solar distance");
            Assert.Contains(instructions.Where(i => i.OpCode == OpCodes.Ldstr)
                    .Select(i => (string)i.Operand),
                s => s.Contains("solar"));
            Assert.Equal(1, CountCalls(CallsOf(sync), "KSARS.Visuals.RenderCompatibility",
                "IsSolarDistanceSupported"));
        }

        [Fact]
        public void BuildPipeline_UsesOwnedPremultipliedBlendState()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var build = SingleMethod(assembly.MainModule, PassType, "BuildPipeline");
            var refs = FieldRefsOf(build);

            Assert.Contains(refs, f => f.Name == "_premulBlend");
            Assert.DoesNotContain(refs, f => f.DeclaringType.EndsWith("BlendState"));
            Assert.Contains(refs, f => f.DeclaringType.EndsWith("ReverseZDepthStencil") && f.Name == "DepthTestNoWrite");
        }

        [Fact]
        public void PremultipliedBlendState_PinsOneOverOneMinusSrcAlpha()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var create = SingleMethod(assembly.MainModule, PassType, "CreatePremultipliedBlendState");

            Assert.True(StoresConstant(create, 1, "SrcColorBlendFactor"), "SrcColor must be One");
            Assert.True(StoresConstant(create, 7, "DstColorBlendFactor"), "DstColor must be OneMinusSrcAlpha");
            Assert.True(StoresConstant(create, 1, "SrcAlphaBlendFactor"), "SrcAlpha must be One");
            Assert.True(StoresConstant(create, 7, "DstAlphaBlendFactor"), "DstAlpha must be OneMinusSrcAlpha");
            Assert.Contains(create.Body.Instructions, i =>
                i.OpCode == OpCodes.Stfld && i.Operand is FieldReference fr && fr.Name == "AttachmentCount");
            Assert.True(StoresConstant(create, 15, "ColorWriteMask"), "RGBA write mask");
            Assert.Contains(create.Body.Instructions, i =>
                i.OpCode == OpCodes.Stfld && i.Operand is FieldReference fr && fr.Name == "BlendEnable");

            var calls = CallsOf(create);
            Assert.Equal(1, calls.Count(c => c.Name == ".ctor"
                && c.DeclaringType.StartsWith("Brutal.Memory.PtrOwner")
                && c.DeclaringType.Contains("VkPipelineColorBlendAttachmentState")));
            Assert.Equal(1, calls.Count(c => c.Name == ".ctor"
                && c.DeclaringType.StartsWith("Brutal.Memory.PtrOwner")
                && c.DeclaringType.Contains("VkPipelineColorBlendStateCreateInfo")));
            Assert.Equal(0, calls.Count(c => c.DeclaringType.StartsWith("KSA.")
                || c.DeclaringType.StartsWith("RenderCore")));
        }

        [Fact]
        public void MainAtlasFrame_OwnsSolarSlotPendingDrawAndEpoch()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var frame = TypeOf(assembly.MainModule, FrameType);
            var solar = TypeOf(assembly.MainModule, SolarSlotType);

            foreach (string field in new[] { "Context", "Adapter", "Coverage", "Sun", "SunId",
                     "SunRadius", "Projection", "Model", "Token", "Prepared", "Presented" })
            {
                Assert.NotNull(solar.Fields.SingleOrDefault(f => f.Name == field));
            }

            Assert.Equal("KSARS.Visuals.ObjectCaptureContext", solar.Fields.Single(f => f.Name == "Context").FieldType.FullName);
            Assert.Equal("KSARS.Visuals.NativeSolarCaptureAdapter", solar.Fields.Single(f => f.Name == "Adapter").FieldType.FullName);
            Assert.Equal("KSARS.Visuals.SolarCoverageProbe", solar.Fields.Single(f => f.Name == "Coverage").FieldType.FullName);
            Assert.Equal("KSA.StellarBody", solar.Fields.Single(f => f.Name == "Sun").FieldType.FullName);
            Assert.Equal("KSARS.Visuals.SolarFrameToken",
                solar.Fields.Single(f => f.Name == "Token").FieldType.FullName);

            Assert.NotNull(frame.Fields.SingleOrDefault(f => f.Name == "Solar"));
            Assert.NotNull(frame.Fields.SingleOrDefault(f => f.Name == "SourceCount"));
            Assert.NotNull(frame.Fields.SingleOrDefault(f => f.Name == "RendererEpoch"));
            Assert.NotNull(frame.Fields.SingleOrDefault(
                f => f.Name == "PendingSunRenderer" && f.FieldType.FullName == "KSA.Rendering.Sun.SunRenderer"));
            Assert.NotNull(frame.Fields.SingleOrDefault(
                f => f.Name == "PendingSunView" && f.FieldType.FullName == "RenderCore.Systems.ViewHandle"));

            var reset = frame.Methods.Single(m => m.Name == "Reset");
            var stores = FieldStoresOf(reset);
            Assert.Contains("PendingSunRenderer", stores);
            Assert.Contains(reset.Body.Instructions, i => i.OpCode == OpCodes.Initobj
                && i.Operand is TypeReference tr && tr.FullName == "RenderCore.Systems.ViewHandle");
            Assert.Contains(reset.Body.Instructions, i => i.OpCode == OpCodes.Initobj
                && i.Operand is TypeReference tr && tr.FullName == "KSARS.Visuals.SolarFrameToken");
            Assert.Contains("Prepared", stores);
            Assert.Contains("Presented", stores);
            Assert.Contains("SourceCount", stores);
            Assert.Contains("RendererEpoch", stores);
        }

        [Fact]
        public void MainAtlasFrameDispose_AttemptsEverySolarRootIndependently()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var frame = TypeOf(assembly.MainModule, FrameType);
            var dispose = frame.Methods.Single(m => m.Name == "Dispose");
            var calls = CallsOf(dispose);

            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.AtlasMainPass", "Dispose"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.SolarCoverageProbe", "Dispose"));
            Assert.Equal(2, CountCalls(calls, "RenderCore.RenderTechnique", "Dispose"));
            Assert.Equal(2, CountCalls(calls, "KSARS.Visuals.ObjectCaptureContext", "Dispose"));
            Assert.True(dispose.Body.ExceptionHandlers.Count >= 6,
                "pass plus three solar roots and both body-slot resources need independent handlers");
            Assert.Equal(1, CountCalls(calls, "System.AggregateException", ".ctor"));
            Assert.Equal(0, calls.Count(c => c.Name.Contains("WaitIdle")));
        }

        [Fact]
        public void PrepareMainSolarSource_UsesCurrentSnapshotAndPerSlotResourcesNeverFrozen()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var module = assembly.MainModule;
            var prep = SingleMethod(module, RendererType, "TryPrepareMainSolarSource");
            var calls = CallsOf(prep);
            var refs = FieldRefsOf(prep);

            Assert.Equal(1, CountCalls(calls, "KSA.Universe", "get_WorldSun"));
            // The media probe patches Camera.GetPositionEgo to apparent
            // positions; solar candidates must keep rest geometry via the
            // direct ECL difference.
            Assert.Equal(0, CountCalls(calls, "KSA.Camera", "GetPositionEgo"));
            Assert.Equal(1, calls.Count(c => c.Name == "GetPositionEcl"));
            Assert.Equal(1, calls.Count(c => c.Name == "get_PositionEcl"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.AtlasProjection", "TryCreate"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.SolarSourceGeometry", "TryCreate"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.SolarCoverageProbe", "GeometryUploadSupported"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.ObjectCaptureContext", ".ctor"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.ObjectCaptureContext", "WriteSnapshot"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.NativeSolarCaptureAdapter", ".ctor"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.SolarCoverageProbe", ".ctor"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.NativeSolarCaptureAdapter", "Record"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.SolarCoverageProbe", "Record"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.RelativisticOptics", "TryTransformSphericalCap"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.ObservedSphericalCap", "IntersectsViewCone"));
            Assert.Equal(0, CountCalls(calls, "KSARS.Visuals.SolarCoverageProbe", "RegisterPreviewTexture"));
            Assert.Equal(0, calls.Count(c => c.DeclaringType.StartsWith("Brutal.ImGuiApi")));
            Assert.DoesNotContain(refs, f => f.Name == "s_solarCapture");
            Assert.DoesNotContain(refs, f => f.Name == "s_solarArchive");
            Assert.Contains(refs, f => f.Name == "s_mainSolarStatus");
            Assert.Contains(refs, f => f.Name == "s_mainSolarPrepared");
            Assert.Contains(FieldStoresOf(prep), name => name == "Token");

            int adapterRecord = IndexOfCall(calls, "KSARS.Visuals.NativeSolarCaptureAdapter", "Record");
            int coverageRecord = IndexOfCall(calls, "KSARS.Visuals.SolarCoverageProbe", "Record");
            int contextCtor = IndexOfCall(calls, "KSARS.Visuals.ObjectCaptureContext", ".ctor");
            Assert.True(contextCtor < adapterRecord && adapterRecord < coverageRecord,
                "context must be rooted before adapter record, coverage record after color capture");
            int barrier = IndexOfCall(calls, "KSA.CommandBufferEx", "PipelineBarrier2");
            Assert.True(barrier > coverageRecord,
                "the explicit depth-sampled-read barrier must follow the coverage record outside attachments");

            var unbalanced = SingleMethod(module, RendererType, "MainRecordingUnbalanced");
            var unbalancedCalls = CallsOf(unbalanced);
            Assert.True(unbalancedCalls.Count(c => c.Name == "get_RecordingRecovered") >= 3,
                "the unbalanced guard must cover solar adapter, solar coverage and the body adapter slots");
            Assert.Contains(FieldRefsOf(unbalanced), f => f.DeclaringType == SolarSlotType && f.Name == "Adapter");
            Assert.Contains(FieldRefsOf(unbalanced), f => f.DeclaringType == SolarSlotType && f.Name == "Coverage");
        }

        [Fact]
        public void PrepareMainFrameSources_SolarPrepBeforeSourceCountGate()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var prepare = SingleMethod(assembly.MainModule, RendererType, "PrepareMainFrameSources");
            var calls = CallsOf(prepare);

            Assert.Equal(1, CountCalls(calls, RendererType, "TryPrepareMainSolarSource"));
            int bodyRecord = calls.FindLastIndex(c => c.DeclaringType == "KSARS.Visuals.NativeCelestialCaptureAdapter"
                && c.Name == "Record");
            int solarPrep = IndexOfCall(calls, RendererType, "TryPrepareMainSolarSource");
            int syncIndex = IndexOfCall(calls, "KSARS.Visuals.AtlasMainPass", "SyncSources");
            Assert.True(bodyRecord >= 0 && solarPrep > bodyRecord,
                "solar preparation must follow body captures on the same snapshot");
            Assert.True(syncIndex > solarPrep,
                "SyncSources must run after solar preparation so the solar tile joins the source list");
        }

        [Fact]
        public void TryRecordMainComposite_TokenGateBeforeRecordPresentedOnlyAfterSuccess()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var record = SingleMethod(assembly.MainModule, RendererType, "TryRecordMainComposite");
            var instructions = record.Body.Instructions.ToList();
            var calls = CallsOf(record);

            int recordCall = IndexOfCall(calls, PassType, "Record");
            int tokenCall = IndexOfCall(calls, RendererType, "CurrentSolarFrameToken");
            int gateCall = IndexOfCall(calls, "KSARS.Visuals.SolarFrameToken", "CanPresent");
            int pendingIndex = instructions.FindIndex(i =>
                i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference fr && fr.Name == "PendingSunRenderer");
            int recordIndex = instructions.FindIndex(i =>
                i.Operand is MethodReference mr && mr.DeclaringType.FullName == PassType && mr.Name == "Record");
            int presentedIndex = instructions.FindIndex(i =>
                i.OpCode == OpCodes.Stfld && i.Operand is FieldReference fr
                    && fr.Name == "Presented" && fr.DeclaringType.FullName == SolarSlotType);

            Assert.True(tokenCall >= 0 && gateCall > tokenCall && recordCall > gateCall,
                "the current ownership token and CanPresent gate must be evaluated before Pass.Record");
            Assert.True(pendingIndex >= 0 && pendingIndex < recordIndex,
                "the deferred native Sun seam must be inspected before Pass.Record");
            Assert.True(presentedIndex > recordIndex,
                "solar presented must be marked only after a successful Record");

            Assert.Contains(FieldRefsOf(record), f => f.Name == "SourceCount");
            Assert.Contains(FieldStoresOf(record), name => name == "s_mainSolarPresented");
            Assert.DoesNotContain(FieldStoresOf(record), name => name == "PendingSunRenderer");
        }

        [Fact]
        public void RunWrapper_RecordsNativeDistanceThenCompositeThenFlushesDeferredSun()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var wrapper = SingleMethod(assembly.MainModule, RendererType, "RunNativeDistanceAndComposite");
            var calls = CallsOf(wrapper);

            int run = IndexOfCall(calls, "KSA.StaticCelestialDistanceRendering", "Run");
            int composite = IndexOfCall(calls, RendererType, "TryRecordMainComposite");
            int flush = IndexOfCall(calls, RendererType, "FlushDeferredNativeSun");
            Assert.True(run >= 0 && composite > run && flush > composite,
                "native distance, then composite, then deferred native Sun flush");
            Assert.Equal(1, CountCalls(calls, "KSA.StaticCelestialDistanceRendering", "Run"));
            Assert.Equal(1, CountCalls(calls, RendererType, "TryRecordMainComposite"));
            Assert.Equal(1, CountCalls(calls, RendererType, "FlushDeferredNativeSun"));
        }

        [Fact]
        public void QueueNativeSunOrAtlas_DefersOnlyCurrentPreparedSolarFrame()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var wrapper = SingleMethod(assembly.MainModule, RendererType, "QueueNativeSunOrAtlas");

            Assert.True(wrapper.IsStatic);
            Assert.Equal(
                new[] { "KSA.Rendering.Sun.SunRenderer", "RenderCore.Systems.ViewHandle" },
                wrapper.Parameters.Select(p => p.ParameterType.FullName).ToArray());
            Assert.Equal("System.Void", wrapper.ReturnType.FullName);

            var calls = CallsOf(wrapper);
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.Sun.SunRenderer", "Draw"));
            Assert.Equal(1, CountCalls(calls, RendererType, "CurrentSolarFrameToken"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.SolarFrameToken", "Matches"));
            Assert.Equal(1, CountCalls(calls, "KSA.Program", "get_MainViewport"));
            Assert.Equal(1, CountCalls(calls, "KSA.Program", "get_RenderedViewport"));

            var refs = FieldRefsOf(wrapper);
            foreach (string field in new[] { "s_preparedMainFrame", "s_mainProbeEnabled",
                     "s_mainFailed", "s_recordingDisabled" })
            {
                Assert.Contains(refs, f => f.DeclaringType == RendererType && f.Name == field);
            }

            Assert.Contains(refs, f => f.DeclaringType == SolarSlotType && f.Name == "Token");
            Assert.Contains(refs, f => f.DeclaringType == SolarSlotType && f.Name == "Prepared");
            Assert.Contains(refs, f => f.DeclaringType == SolarSlotType && f.Name == "Presented");
            Assert.Contains(FieldStoresOf(wrapper), name => name == "PendingSunRenderer");
            Assert.Contains(FieldStoresOf(wrapper), name => name == "PendingSunView");
        }

        [Fact]
        public void FlushDeferredNativeSun_DrawsPendingUnlessSolarPresented()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var flush = SingleMethod(assembly.MainModule, RendererType, "FlushDeferredNativeSun");
            var calls = CallsOf(flush);

            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.Sun.SunRenderer", "Draw"));
            Assert.Equal(1, CountCalls(calls, RendererType, "CurrentSolarFrameToken"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.SolarFrameToken", "Owns"));
            Assert.Contains(FieldRefsOf(flush), f => f.DeclaringType == SolarSlotType && f.Name == "Token");
            Assert.Contains(FieldRefsOf(flush), f => f.DeclaringType == SolarSlotType && f.Name == "Prepared");
            Assert.Contains(FieldRefsOf(flush), f => f.DeclaringType == SolarSlotType && f.Name == "Presented");
            Assert.Contains(FieldStoresOf(flush), name => name == "PendingSunRenderer");
        }

        [Fact]
        public void SunbloomWrapper_BypassesOnlyCurrentSuccessfulSolarReplacement()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var wrapper = SingleMethod(assembly.MainModule, RendererType, "RenderSunbloomOrAtlas");

            Assert.True(wrapper.IsStatic);
            Assert.Equal(
                new[] { "KSA.SunbloomRenderer", "Brutal.VulkanApi.CommandBuffer", "KSA.IViewport", "System.Int32" },
                wrapper.Parameters.Select(p => p.ParameterType.FullName).ToArray());
            Assert.Equal("System.Void", wrapper.ReturnType.FullName);

            var calls = CallsOf(wrapper);
            Assert.Equal(1, CountCalls(calls, "KSA.SunbloomRenderer", "Render"));
            Assert.Equal(1, CountCalls(calls, RendererType, "SolarReplacementPresented"));
            int presented = IndexOfCall(calls, RendererType, "SolarReplacementPresented");
            int native = IndexOfCall(calls, "KSA.SunbloomRenderer", "Render");
            Assert.True(presented >= 0 && native > presented,
                "the ownership-token check must gate the native Sunbloom call");
        }

        [Fact]
        public void SolarReplacementPresented_RequiresCurrentTokenChain()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var gate = SingleMethod(assembly.MainModule, RendererType, "SolarReplacementPresented");
            var calls = CallsOf(gate);
            var refs = FieldRefsOf(gate);

            Assert.Equal(1, CountCalls(calls, "KSA.Program", "get_MainViewport"));
            Assert.Equal(1, CountCalls(calls, "KSA.Program", "get_RenderedViewport"));
            Assert.Equal(1, CountCalls(calls, RendererType, "CurrentSolarFrameToken"));
            Assert.Equal(1, CountCalls(calls, "KSARS.Visuals.SolarFrameToken", "Owns"));
            foreach (string field in new[] { "s_preparedMainFrame", "s_mainProbeEnabled",
                     "s_mainFailed", "s_recordingDisabled" })
            {
                Assert.Contains(refs, f => f.DeclaringType == RendererType && f.Name == field);
            }

            Assert.Contains(refs, f => f.DeclaringType == SolarSlotType && f.Name == "Token");
            Assert.Contains(refs, f => f.DeclaringType == SolarSlotType && f.Name == "Prepared");
            Assert.Contains(refs, f => f.DeclaringType == SolarSlotType && f.Name == "Presented");
            Assert.True(FieldStoresOf(gate).Count == 0,
                "the gate must be a pure read of the ownership token, never a mutation");
        }

        [Fact]
        public void CurrentSolarFrameToken_IdentitySourcePin()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var helper = SingleMethod(assembly.MainModule, RendererType, "CurrentSolarFrameToken");
            var calls = CallsOf(helper);
            var refs = FieldRefsOf(helper);

            Assert.True(CountCalls(calls, "KSA.Program", "get_Instance") == 1
                || refs.Any(f => f.DeclaringType == "KSA.Program" && f.Name == "Instance"),
                "the live Program instance must feed the resource frame read");
            Assert.True(CountCalls(calls, "KSA.Program", "get_ResourceFrameIndex") == 1
                || refs.Any(f => f.DeclaringType == "KSA.Program" && f.Name == "ResourceFrameIndex"),
                "the live resource frame index must feed the current token");
            Assert.Equal(1, CountCalls(calls, "KSA.Universe", "get_WorldSun"));
            Assert.True(CountCalls(calls, "KSA.Program", "get_MainViewport") >= 1);
            Assert.Equal(1, CountCalls(calls, "KSA.IViewport", "GetCamera"));
            Assert.Contains(refs, f => f.Name == "s_lastSnapshot");
            Assert.Contains(refs, f => f.Name == "s_rendererEpoch");
            Assert.True(FieldStoresOf(helper).Count == 0,
                "the current token builder must be a pure read");
        }

        [Fact]
        public void PassDispose_AttemptsEveryOwnedRootIndependently()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var dispose = SingleMethod(assembly.MainModule, PassType, "Dispose");
            var calls = CallsOf(dispose);

            Assert.Equal(1, CountCalls(calls, "System.AggregateException", ".ctor"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "DestroyPipeline"));
            Assert.Equal(1, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "DestroyPipelineLayout"));
            Assert.Equal(2, CountCalls(calls, "Brutal.VulkanApi.VkDeviceExtensions", "DestroyShaderModule"));
            Assert.True(dispose.Body.ExceptionHandlers.Count >= 9,
                "pipeline, layout, pool, set layout, body buffer, both shader modules and both blend owners need independent handlers");
            Assert.True(calls.Count(c => c.Name.Contains("Destroy") && c.DeclaringType.Contains("RenderTarget")) == 0,
                "the borrowed native target must never be destroyed");
        }

        [Fact]
        public void SolarFrameToken_MatchesRequiresEveryIdentity()
        {
            object snapshot = new(), viewport = new(), camera = new(), sun = new();
            var token = new SolarFrameToken(snapshot, viewport, camera, sun, 3, 7);
            var same = new SolarFrameToken(snapshot, viewport, camera, sun, 3, 7);
            Assert.True(token.Matches(in same));

            Assert.False(token.Matches(new SolarFrameToken(new object(), viewport, camera, sun, 3, 7)),
                "different snapshot reference must not match");
            Assert.False(token.Matches(new SolarFrameToken(snapshot, new object(), camera, sun, 3, 7)),
                "different viewport reference must not match");
            Assert.False(token.Matches(new SolarFrameToken(snapshot, viewport, new object(), sun, 3, 7)),
                "different camera reference must not match");
            Assert.False(token.Matches(new SolarFrameToken(snapshot, viewport, camera, new object(), 3, 7)),
                "different sun reference must not match");
            Assert.False(token.Matches(new SolarFrameToken(snapshot, viewport, camera, sun, 4, 7)),
                "different resource frame must not match");
            Assert.False(token.Matches(new SolarFrameToken(snapshot, viewport, camera, sun, 3, 8)),
                "different renderer epoch must not match");
            Assert.False(token.Matches(default), "default token must not match");
            Assert.False(default(SolarFrameToken).Matches(in same), "a reset token must not match");
        }

        [Fact]
        public void SolarFrameToken_NullAndNegativeFieldsRejected()
        {
            object snapshot = new(), viewport = new(), camera = new(), sun = new();
            Assert.False(new SolarFrameToken(null, viewport, camera, sun, 3, 7)
                .Matches(new SolarFrameToken(null, viewport, camera, sun, 3, 7)));
            Assert.False(new SolarFrameToken(snapshot, null, camera, sun, 3, 7)
                .Matches(new SolarFrameToken(snapshot, null, camera, sun, 3, 7)));
            Assert.False(new SolarFrameToken(snapshot, viewport, null, sun, 3, 7)
                .Matches(new SolarFrameToken(snapshot, viewport, null, sun, 3, 7)));
            Assert.False(new SolarFrameToken(snapshot, viewport, camera, null, 3, 7)
                .Matches(new SolarFrameToken(snapshot, viewport, camera, null, 3, 7)));
            Assert.False(new SolarFrameToken(snapshot, viewport, camera, sun, -1, 7)
                .Matches(new SolarFrameToken(snapshot, viewport, camera, sun, -1, 7)));
            Assert.False(new SolarFrameToken(snapshot, viewport, camera, sun, 3, -1)
                .Matches(new SolarFrameToken(snapshot, viewport, camera, sun, 3, -1)));
        }

        [Fact]
        public void SolarFrameToken_CanPresentAndOwnsRequireFlags()
        {
            object snapshot = new(), viewport = new(), camera = new(), sun = new();
            var token = new SolarFrameToken(snapshot, viewport, camera, sun, 3, 7);
            var current = new SolarFrameToken(snapshot, viewport, camera, sun, 3, 7);

            Assert.True(token.CanPresent(in current, prepared: true, hasPending: true));
            Assert.False(token.CanPresent(in current, prepared: false, hasPending: true));
            Assert.False(token.CanPresent(in current, prepared: true, hasPending: false));
            Assert.False(token.CanPresent(default, prepared: true, hasPending: true));

            Assert.True(token.Owns(in current, prepared: true, presented: true));
            Assert.False(token.Owns(in current, prepared: false, presented: true));
            Assert.False(token.Owns(in current, prepared: true, presented: false));
            Assert.False(token.Owns(default, prepared: true, presented: true));
        }

        [Fact]
        public void SolarDistanceSupport_RejectsUnderflowOverflowAndNonFinite()
        {
            Assert.True(RenderCompatibility.IsSolarDistanceSupported(1.0e10));
            Assert.False(RenderCompatibility.IsSolarDistanceSupported(1.0e-300),
                "a positive double that underflows to zero float must be rejected");
            Assert.False(RenderCompatibility.IsSolarDistanceSupported(0.0));
            Assert.False(RenderCompatibility.IsSolarDistanceSupported(-5.0));
            Assert.False(RenderCompatibility.IsSolarDistanceSupported(double.NaN));
            Assert.False(RenderCompatibility.IsSolarDistanceSupported(double.PositiveInfinity));
            Assert.False(RenderCompatibility.IsSolarDistanceSupported(1.0e300),
                "a double overflowing float must be rejected");
        }

        [Fact]
        public void TranspilerAndPrepare_ResolveAndReWireBothSolarAnchors()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var transpile = SingleMethod(assembly.MainModule, RendererType, "TranspileMainRenderGame");
            var refs = FieldRefsOf(transpile);

            foreach (string field in new[] { "s_nativeSunDraw", "s_sunDrawWrapper",
                     "s_nativeSunbloom", "s_sunbloomWrapper" })
            {
                Assert.Contains(refs, f => f.DeclaringType == RendererType && f.Name == field);
            }

            var prepare = SingleMethod(assembly.MainModule, RendererType, "PrepareProbe");
            var prepareCalls = CallsOf(prepare);
            Assert.Equal(1, CountCalls(prepareCalls,
                "KSARS.Visuals.RenderCompatibility", "TryValidateSolarQueueBoundary"));
            Assert.True(CountCalls(prepareCalls,
                    "HarmonyLib.PatchProcessor", "GetOriginalInstructions") >= 2,
                "the native Sun draw and mesh queue bodies must be read before wrappers are installed");
            var strings = prepare.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string)
                .Select(i => (string)i.Operand)
                .ToList();
            Assert.Contains("QueueNativeSunOrAtlas", strings);
            Assert.Contains("RenderSunbloomOrAtlas", strings);
        }

        [Fact]
        public void SolarSeams_NeverMutateNativeSunOrBloomState()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var module = assembly.MainModule;
            foreach (string name in new[] { "QueueNativeSunOrAtlas", "FlushDeferredNativeSun",
                     "RenderSunbloomOrAtlas", "SolarReplacementPresented" })
            {
                var method = SingleMethod(module, RendererType, name);
                var stores = method.Body.Instructions
                    .Where(i => (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld)
                        && i.Operand is FieldReference fr)
                    .Select(i => ((FieldReference)i.Operand).DeclaringType.FullName)
                    .ToList();
                Assert.DoesNotContain(stores, d => d.StartsWith("KSA.") || d.StartsWith("RenderCore.")
                    || d.StartsWith("Core.") || d.StartsWith("Brutal."));
            }

            var prep = SingleMethod(module, RendererType, "TryPrepareMainSolarSource");
            var prepStores = prep.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld)
                    && i.Operand is FieldReference fr)
                .Select(i => ((FieldReference)i.Operand).DeclaringType.FullName)
                .ToList();
            Assert.DoesNotContain(prepStores, d => d.StartsWith("KSA.")
                && d != "KSA.InstanceData" && d != "KSA.MeshRenderTechnique/Ubo");
            Assert.DoesNotContain(prepStores, d => d.StartsWith("RenderCore.")
                || d.StartsWith("Core.") || d.StartsWith("Brutal."));
        }

        [Fact]
        public void StatusStrings_PinSolarDiagnosticsAndFallbackDisclosure()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(ModAssemblyPath);
            var type = TypeOf(assembly.MainModule, RendererType);
            var strings = type.Methods.Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions)
                .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string)
                .Select(i => (string)i.Operand)
                .ToList();

            Assert.Contains(strings, s => s.Contains("native Sun path retained"));
            Assert.Contains(strings, s => s.Contains("simplified center-plane radial"));
            Assert.Contains(strings, s => s.Contains("Sunbloom flare bypassed this frame only")
                || s.Contains("Sunbloom flare bypassed"));
            Assert.Contains(strings, s => s.Contains("physical R"));
            Assert.Contains(strings, s => s.Contains("2.75"));
            Assert.Contains(strings, s => s.Contains("calibration"));
            Assert.DoesNotContain(strings, s => s.Contains("GPU opacity verified")
                || s.Contains("solar opacity calibrated"));

            var getStatus = type.Methods.Single(m => m.Name == "GetStatus");
            Assert.Contains(FieldRefsOf(getStatus), f => f.Name == "s_mainSolarStatus");
        }

        [RendererBaselineFact]
        public void NativeSunRendererDraw_QueuesBucketDrawBehindPositionAndThreshold()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var sun = assembly.MainModule.GetType("KSA.Rendering.Sun.SunRenderer");
            Assert.NotNull(sun);
            var draw = sun.Methods.Single(m => m.Name == "Draw"
                && m.Parameters.Select(p => p.ParameterType.FullName)
                    .SequenceEqual(new[] { "RenderCore.Systems.ViewHandle" }));
            Assert.Equal("System.Void", draw.ReturnType.FullName);

            var calls = CallsOf(draw);
            Assert.Equal(1, CountCalls(calls, "KSA.Universe", "get_WorldSun"));
            Assert.Equal(1, CountCalls(calls, "KSA.Camera", "GetPositionEgo"));
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.Sun.SunRenderer", "StartShowingDistOf"));
            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.Sun.SunMeshRenderable", "Draw"));
            int position = IndexOfCall(calls, "KSA.Camera", "GetPositionEgo");
            int threshold = IndexOfCall(calls, "KSA.Rendering.Sun.SunRenderer", "StartShowingDistOf");
            int queue = IndexOfCall(calls, "KSA.Rendering.Sun.SunMeshRenderable", "Draw");
            Assert.True(position >= 0 && threshold > position && queue > threshold,
                "the native sun must queue its mesh only inside the distance threshold");
            Assert.True(calls.Count(c => c.Name == "BeginRendering" || c.Name == "EndRendering"
                    || c.Name.Contains("Barrier") || c.Name == "DrawIndexed") == 0,
                "the native sun draw must only queue bucket work, never submit GPU commands");
        }

        [RendererBaselineFact]
        public void NativeSunMeshRenderableDraw_OnlyQueuesMeshInstanceAllPasses()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var mesh = assembly.MainModule.GetType("KSA.Rendering.Sun.SunMeshRenderable");
            Assert.NotNull(mesh);
            var draw = mesh.Methods.Single(m => m.Name == "Draw");
            var calls = CallsOf(draw);

            Assert.True(calls.Count(c => c.Name == "DrawMeshInstanceAllPasses") >= 1,
                "the queued sun work must flow through the shared bucket system");
            Assert.Equal(0, calls.Count(c => c.Name == "BeginRendering" || c.Name == "EndRendering"
                || c.Name == "PipelineBarrier2" || c.Name == "DrawIndexed" || c.Name == "Draw"));
        }

        [RendererBaselineFact]
        public void NativeSolarQueueBoundary_ValidatorAcceptsInstalledCalls()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var sun = assembly.MainModule.GetType("KSA.Rendering.Sun.SunRenderer");
            var mesh = assembly.MainModule.GetType("KSA.Rendering.Sun.SunMeshRenderable");
            Assert.NotNull(sun);
            Assert.NotNull(mesh);
            var draw = sun.Methods.Single(m => m.Name == "Draw"
                && m.Parameters.Select(p => p.ParameterType.FullName)
                    .SequenceEqual(new[] { "RenderCore.Systems.ViewHandle" }));
            var queue = mesh.Methods.Single(m => m.Name == "Draw");

            Assert.True(RenderCompatibility.TryValidateSolarQueueBoundary(
                CallsOf(draw), CallsOf(queue), out string reason), reason);
        }

        [RendererBaselineFact]
        public void NativeAppendStars_StillSkipsWorldSun()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var distance = assembly.MainModule.GetType("KSA.StaticCelestialDistanceRendering");
            Assert.NotNull(distance);
            var append = distance.Methods.Single(m => m.Name == "AppendStars");
            var instructions = append.Body.Instructions;
            var calls = CallsOf(append);

            Assert.Equal(1, CountCalls(calls, "KSA.Universe", "get_WorldSun"));

            int worldSunIndex = instructions.ToList().FindIndex(i =>
                i.Operand is MethodReference mr && mr.DeclaringType.FullName == "KSA.Universe"
                    && mr.Name == "get_WorldSun");
            Assert.True(worldSunIndex >= 0);
            int branchIndex = -1;
            for (int i = worldSunIndex + 1; i < Math.Min(instructions.Count, worldSunIndex + 9); i++)
            {
                if (instructions[i].OpCode.FlowControl == FlowControl.Cond_Branch
                    && instructions[i].Operand is Instruction)
                {
                    branchIndex = i;
                    break;
                }
            }

            Assert.True(branchIndex > worldSunIndex,
                "a reference comparison against WorldSun must gate the per-star append");
            var branch = instructions[branchIndex];
            int targetIndex = instructions.IndexOf((Instruction)branch.Operand);
            bool beqFamily = branch.OpCode == OpCodes.Beq || branch.OpCode == OpCodes.Beq_S
                || branch.OpCode == OpCodes.Brtrue || branch.OpCode == OpCodes.Brtrue_S;
            if (beqFamily)
            {
                Assert.True(targetIndex > branchIndex,
                    "the WorldSun-equal branch must skip forward over the per-star append");
                Assert.True(Enumerable.Range(branchIndex + 1, targetIndex - branchIndex - 1)
                        .Any(i => instructions[i].OpCode == OpCodes.Call
                            || instructions[i].OpCode == OpCodes.Callvirt
                            || instructions[i].OpCode == OpCodes.Stfld
                            || instructions[i].OpCode == OpCodes.Stelem_Ref),
                    "the skipped range must contain the per-star append work");
            }
            else
            {
                Assert.True(targetIndex > branchIndex,
                    "the not-equal branch must jump forward into the loop body");
                Assert.True(Enumerable.Range(branchIndex + 1, targetIndex - branchIndex - 1)
                        .Any(i => instructions[i].OpCode == OpCodes.Br
                            || instructions[i].OpCode == OpCodes.Br_S),
                    "the skipped range must contain the unconditional continue jump");
            }
        }

        [RendererBaselineFact]
        public void NativeBlendPresets_PinAlphaBlendFactorSemantics()
        {
            string abstractionsPath = Path.Combine(
                Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, "Brutal.Vulkan.Abstractions.dll");
            using var assembly = AssemblyDefinition.ReadAssembly(abstractionsPath);
            var presets = assembly.MainModule.Types.SelectMany(AllNested)
                .Concat(assembly.MainModule.Types)
                .Single(t => t.FullName == "Brutal.VulkanApi.Abstractions.Presets/BlendAttachment");
            var cctor = presets.Methods.Single(m => m.Name == ".cctor");
            var instructions = cctor.Body.Instructions;
            int storeIndex = -1;
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode == OpCodes.Stsfld
                    && instructions[i].Operand is FieldReference fr && fr.Name == "BlendColorAlpha")
                {
                    Assert.True(storeIndex < 0, "BlendColorAlpha must be stored exactly once");
                    storeIndex = i;
                }
            }

            Assert.True(storeIndex >= 0, "BlendColorAlpha static store not found in the preset cctor");
            int sliceStart = 0;
            for (int i = storeIndex - 1; i >= 0; i--)
            {
                if (instructions[i].OpCode == OpCodes.Stsfld)
                {
                    sliceStart = i + 1;
                    break;
                }
            }

            var slice = instructions.Skip(sliceStart).Take(storeIndex - sliceStart).ToList();
            Assert.True(StoresConstantIn(slice, 6, "SrcColorBlendFactor"),
                "native BlendColorAlpha must source color from SrcAlpha");
            Assert.True(StoresConstantIn(slice, 7, "DstColorBlendFactor"),
                "native BlendColorAlpha must scale destination by OneMinusSrcAlpha");
            Assert.True(StoresConstantIn(slice, 1, "SrcAlphaBlendFactor"),
                "native BlendColorAlpha alpha source must be One");
            Assert.True(StoresConstantIn(slice, 0, "DstAlphaBlendFactor"),
                "native BlendColorAlpha alpha destination must be Zero");

            string vulkanPath = Path.Combine(
                Path.GetDirectoryName(BaselineContext.KsaAssemblyPath)!, "Brutal.Vulkan.dll");
            using var vulkan = AssemblyDefinition.ReadAssembly(vulkanPath);
            var blendFactor = vulkan.MainModule.GetType("Brutal.VulkanApi.VkBlendFactor");
            Assert.NotNull(blendFactor);
            Assert.Equal(1, blendFactor.Fields.Single(f => f.Name == "One").Constant);
            Assert.Equal(6, blendFactor.Fields.Single(f => f.Name == "SrcAlpha").Constant);
            Assert.Equal(7, blendFactor.Fields.Single(f => f.Name == "OneMinusSrcAlpha").Constant);
        }

        [RendererBaselineFact]
        public void NativeRenderGame_SunQueueAndBloomAnchorOrder()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(BaselineContext.KsaAssemblyPath);
            var renderGame = assembly.MainModule.GetType("KSA.Program")!.Methods.Single(m =>
                m.Name == "RenderGame"
                && m.Parameters.Select(p => p.ParameterType.FullName)
                    .SequenceEqual(new[] { "RenderCore.AcquiredFrame", "System.Double" }));
            var calls = CallsOf(renderGame);

            int begin = IndexOfCall(calls, "KSA.Rendering.RenderTarget", "BeginRendering");
            int sunDraw = IndexOfCall(calls, "KSA.Rendering.Sun.SunRenderer", "Draw");
            int run = IndexOfCall(calls, "KSA.StaticCelestialDistanceRendering", "Run");
            int sphere = IndexOfCall(calls, "KSA.StaticCelestial", "RenderSphere");
            int sunbloom = IndexOfCall(calls, "KSA.SunbloomRenderer", "Render");
            int overall = IndexOfCall(calls, "KSA.OverallBloomRenderer", "Render");

            Assert.Equal(1, CountCalls(calls, "KSA.Rendering.Sun.SunRenderer", "Draw"));
            Assert.Equal(1, CountCalls(calls, "KSA.SunbloomRenderer", "Render"));
            Assert.Equal(1, CountCalls(calls, "KSA.OverallBloomRenderer", "Render"));
            Assert.True(begin >= 0 && sunDraw > begin && run > sunDraw && sphere > run
                    && sunbloom > sphere && overall > sunbloom,
                "Begin < SunDraw < DistanceRun < NativeSphere < Sunbloom < OverallBloom must hold");
        }
    }
}
