using Brutal.Numerics;
using KSA;

namespace KSARS.Visuals
{
    internal sealed class ObserverSnapshot
    {
        public Vehicle FollowedVehicle { get; }
        public IViewport MainViewport { get; }
        public Camera MainCamera { get; }
        public double3 PositionEcl { get; }
        public double3 VelocityEcl { get; }
        public PresentationVelocity Observer { get; }
        public int ResourceFrameIndex { get; }
        public int GlobalFrameIndex { get; }
        public UboCameraData CameraData { get; }
        public UboLightingData LightingData { get; }
        public UboCelestialData CelestialData { get; }
        public UboVesselData VesselData { get; }
        public UboGlobalConstants Constants { get; }

        private ObserverSnapshot(Vehicle followedVehicle, IViewport mainViewport, Camera mainCamera,
            double3 positionEcl, double3 velocityEcl, PresentationVelocity observer,
            int resourceFrameIndex, int globalFrameIndex,
            UboCameraData cameraData, UboLightingData lightingData, UboCelestialData celestialData,
            UboVesselData vesselData, UboGlobalConstants constants)
        {
            FollowedVehicle = followedVehicle;
            MainViewport = mainViewport;
            MainCamera = mainCamera;
            PositionEcl = positionEcl;
            VelocityEcl = velocityEcl;
            Observer = observer;
            ResourceFrameIndex = resourceFrameIndex;
            GlobalFrameIndex = globalFrameIndex;
            CameraData = cameraData;
            LightingData = lightingData;
            CelestialData = celestialData;
            VesselData = vesselData;
            Constants = constants;
        }

        internal static bool TryCapture(Func<int> getGlobalFrameIndex, out ObserverSnapshot? snapshot, out string reason)
        {
            snapshot = null;
            reason = "unsupported state";

            if (getGlobalFrameIndex is null)
            {
                reason = "global frame getter missing";
                return false;
            }

            Program? program = Program.Instance;
            if (program is null)
            {
                reason = "program instance unavailable";
                return false;
            }

            IViewport? main = Program.MainViewport;
            if (main is null)
            {
                reason = "main viewport unavailable";
                return false;
            }

            Core.Renderer? renderer = Program.GetRenderer();
            if (renderer is null)
            {
                reason = "renderer unavailable";
                return false;
            }

            if (Universe.CurrentSystem is null)
            {
                reason = "no current system";
                return false;
            }

            if (!main.Visible)
            {
                reason = "main viewport not visible";
                return false;
            }

            if (Program.Editor is not null)
            {
                reason = "editor open";
                return false;
            }

            if (main.Mode == CameraMode.Map)
            {
                reason = "map mode";
                return false;
            }

            Camera? camera = main.GetCamera();
            if (camera is null)
            {
                reason = "main camera unavailable";
                return false;
            }

            if (camera.Orthographic)
            {
                reason = "orthographic camera";
                return false;
            }

            if (camera.Following is not Vehicle vehicle || vehicle.IsDisposed)
            {
                reason = "not following a live vehicle";
                return false;
            }

            if (main.Width <= 0 || main.Height <= 0)
            {
                reason = "invalid main viewport dimensions";
                return false;
            }

            if (camera.FramebufferSize.X <= 0 || camera.FramebufferSize.Y <= 0)
            {
                reason = "invalid camera framebuffer size";
                return false;
            }

            if (renderer.Extent.Width <= 0 || renderer.Extent.Height <= 0)
            {
                reason = "invalid renderer extent";
                return false;
            }

            int framesInFlight = renderer.MaxFramesInFlight;
            int resourceFrameIndex = program.ResourceFrameIndex;
            if (resourceFrameIndex < 0 || resourceFrameIndex >= framesInFlight)
            {
                reason = "resource frame index out of range";
                return false;
            }

            int globalFrameIndex = getGlobalFrameIndex();
            if (globalFrameIndex < 0 || globalFrameIndex >= framesInFlight)
            {
                reason = "global shader frame out of range";
                return false;
            }

            double3 positionEcl = camera.PositionEcl;
            if (!IsFinite(positionEcl))
            {
                reason = "nonfinite observer origin";
                return false;
            }

            double3 velocityEcl = vehicle.GetVelocityEcl();
            if (!IsFinite(velocityEcl))
            {
                reason = "nonfinite observer velocity";
                return false;
            }

            if (!RelativisticOptics.TryCreateVelocity(
                    new OpticalVector(velocityEcl.X, velocityEcl.Y, velocityEcl.Z), out PresentationVelocity observer))
            {
                reason = "observer velocity unsupported";
                return false;
            }

            int shaderSlot = main.ShaderSlot;
            if (shaderSlot < 0 || shaderSlot > 7)
            {
                reason = "invalid main viewport shader slot";
                return false;
            }

            UboCameraData cameraData = GlobalShaderBindings.CameraData(shaderSlot);
            UboLightingData lightingData = GlobalShaderBindings.LightingData(shaderSlot);
            UboCelestialData celestialData = GlobalShaderBindings.CelestialData(shaderSlot);
            UboVesselData vesselData = GlobalShaderBindings.VesselData(shaderSlot);
            UboGlobalConstants constants = GlobalShaderBindings.Constants;

            if (!IsFinite(cameraData.View) || !IsFinite(cameraData.Projection)
                || !IsFinite(cameraData.InverseView) || !IsFinite(cameraData.InverseProjection)
                || !IsFinite(cameraData.ViewProjection))
            {
                reason = "nonfinite main matrices";
                return false;
            }

            if (!float.IsFinite(camera.NearPlane) || !float.IsFinite(camera.FarPlane)
                || !(camera.NearPlane > 0.0f) || !(camera.FarPlane > camera.NearPlane))
            {
                reason = "invalid camera near/far";
                return false;
            }

            if (!float.IsFinite(cameraData.NearPlane) || !float.IsFinite(cameraData.FarPlane)
                || !(cameraData.NearPlane > 0.0f) || !(cameraData.FarPlane > cameraData.NearPlane))
            {
                reason = "invalid global near/far";
                return false;
            }

            if (!float.IsFinite(cameraData.PbrExposure))
            {
                reason = "nonfinite global exposure";
                return false;
            }

            if (!float.IsFinite(cameraData.CameraPosition.X) || !float.IsFinite(cameraData.CameraPosition.Y)
                || !float.IsFinite(cameraData.CameraPosition.Z) || !float.IsFinite(cameraData.CameraPosition.W))
            {
                reason = "nonfinite global camera position";
                return false;
            }

            if (cameraData.ScreenWidth != camera.FramebufferSize.X
                || cameraData.ScreenHeight != camera.FramebufferSize.Y)
            {
                reason = "global screen size mismatch with camera framebuffer";
                return false;
            }

            snapshot = new ObserverSnapshot(vehicle, main, camera, positionEcl, velocityEcl, observer,
                resourceFrameIndex, globalFrameIndex,
                cameraData, lightingData, celestialData, vesselData, constants);
            reason = "captured";
            return true;
        }

        private static bool IsFinite(double3 v) =>
            double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

        private static bool IsFinite(in float4x4 m) =>
            float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
            && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
            && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
            && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
    }
}
