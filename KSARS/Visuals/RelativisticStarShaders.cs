namespace KSARS.Visuals
{
    internal static class RelativisticStarShaders
    {
        // All catalog directions are transformed here; no native forward-culling list is used.
        internal const string Vertex = """
            #version 450
            struct Camera {
                mat4 viewProjection; mat4 view; mat4 projection;
                mat4 inverseView; mat4 inverseProjection; vec4 cameraPosition;
                float nearPlane; float farPlane; float aspectRatio; int screenWidth;
                int screenHeight; float pbrExposure; float time; float simSpeed;
                int frame; int sdfResolution; float deltaTime; int pad0; mat4 galacticPlane;
            };
            layout(set=0,binding=0) uniform GlobalUbo { Camera camera; } globalData;
            layout(std430,set=1,binding=0) readonly buffer ThermalTable {
                vec4 samples[1024]; vec4 hot[3];
            } thermal;
            struct Star { vec4 directionTemperature; vec4 colorFlux; vec4 restThermal; vec4 aux; };
            layout(std430,set=1,binding=1) readonly buffer Catalog { Star stars[]; } catalog;
            layout(push_constant) uniform Observer { vec4 uq; } observer;
            layout(location=0) flat out vec3 outRgbPerY;
            layout(location=1) flat out vec2 outCenter;
            layout(location=2) flat out float outLogFlux;
            // maxTheta, sprite half-side in px, and the native resolution factor
            // max(screenW,screenH)/1920 applied inside psf_glow.
            layout(location=3) flat out vec3 outProfile;
            // Per-pixel luminance bound: the star's shifted photosphere
            // (beaming-free resolved luminance) in restY units, or the
            // prior-flux ceiling for unfitted colors.
            layout(location=4) flat out float outCapLum;

            vec4 spectrum(float temperature) {
                if (temperature > 1e8) {
                    float r = 1e8 / temperature;
                    // hot[i] holds channel i's continuation coefficients
                    // (constant, linear, quadratic) packed by WriteGpuTable.
                    vec3 xyz = vec3(
                        thermal.hot[0].x + r * (thermal.hot[0].y + r * thermal.hot[0].z),
                        thermal.hot[1].x + r * (thermal.hot[1].y + r * thermal.hot[1].z),
                        thermal.hot[2].x + r * (thermal.hot[2].y + r * thermal.hot[2].z));
                    return vec4(xyz / (xyz.x + xyz.y + xyz.z), log(xyz.y) + log(temperature) - 18.420680744);
                }
                float p = clamp(log(temperature) * (1023.0 / 18.420680744), 0.0, 1023.0);
                int i = min(int(floor(p)), 1022);
                return mix(thermal.samples[i], thermal.samples[i+1], clamp(p-float(i),0.0,1.0));
            }
            vec3 displayRgb(vec3 xyz) {
                vec3 rgb = vec3(
                    dot(vec3(3.240969942,-1.537383178,-0.498610760),xyz),
                    dot(vec3(-0.969243636,1.875967502,0.041555057),xyz),
                    dot(vec3(0.055630080,-0.203976959,1.056971514),xyz));
                float t = 0.0;
                if (rgb.r < 0.0) t = max(t,-rgb.r/(xyz.y-rgb.r));
                if (rgb.g < 0.0) t = max(t,-rgb.g/(xyz.y-rgb.g));
                if (rgb.b < 0.0) t = max(t,-rgb.b/(xyz.y-rgb.b));
                return max(mix(rgb,vec3(xyz.y),t),vec3(0.0)) / xyz.y;
            }
            void main() {
                outRgbPerY = vec3(0.0); outCenter = vec2(0.0); outLogFlux = -1e30;
                outProfile = vec3(0.0);
                gl_Position = vec4(2.0,2.0,1.0,1.0);
                Star star = catalog.stars[gl_InstanceIndex];
                vec3 rest = normalize(star.directionTemperature.xyz);
                vec3 observed = rest;
                float doppler = 1.0;
                float q = observer.uq.w;
                if (q != 1.0) {
                    vec3 u = normalize(observer.uq.xyz);
                    float a = 0.5 * dot(rest+u,rest+u);
                    float b = 0.5 * dot(rest-u,rest-u);
                    float denominator = a + q*q*b;
                    vec3 perpendicular = rest-dot(rest,u)*u;
                    observed = normalize(u*(a-q*q*b)+perpendicular*(2.0*q));
                    doppler = denominator/(2.0*q);
                }
                vec3 raw = star.colorFlux.rgb;
                float restY = dot(raw,vec3(0.212639006,0.715168679,0.072192315));
                if (!(restY > 0.0) || !(doppler > 0.0)) return;
                vec3 viewDirection = mat3(globalData.camera.view) * observed;
                // Project a point at the native star distance (19.5 view units,
                // native AddInstance Position=Normalize(forward)*distance).
                vec4 clip = globalData.camera.projection * vec4(viewDirection*19.5,1.0);
                if (!(clip.w > 0.0)) return;
                vec2 size = vec2(globalData.camera.screenWidth,globalData.camera.screenHeight);
                if (any(lessThanEqual(size,vec2(0.0)))) return;
                float radius = star.aux.x;
                if (!(radius > 0.0)) return;
                // Native sprite: clip-space half extent 0.5*scale01 divided by
                // w=19.5 gives half-side scale01*W/(4*19.5) px.
                float restPixelSide = radius * size.x / 78.0;
                // The billboard is a small angular feature about the star
                // direction, so aberration scales its observed angular radius
                // by 1/D. The cap at 1.0 keeps rear stars (D<1) at their rest
                // glyph - unbounded growth would fill the rear sky with giant
                // dim ghosts - and the ~1.25px floor keeps extreme-D sprites
                // covering at least one raster sample (below ~1px a quad
                // hit-tests pixel centers and flickers on tiny camera moves).
                // Per-pixel emission is compensated by 1/sizeFactor^2 below so
                // each star's integrated observed flux is unchanged.
                float sizeFactor = min(max(1.0 / doppler, 1.25 / restPixelSide), 1.0);
                float pixelSide = restPixelSide * sizeFactor;
                // Native profile scale: MaxIrr = (0.01*512/0.123)^2/(2*pi).
                float maxTheta = 0.123 * sqrt(atan(radius / 275.7619) * 275.7619);
                vec2 center = (clip.xy/clip.w*0.5+0.5)*size;
                if (any(lessThan(center,vec2(-pixelSide-2.0))) || any(greaterThan(center,size+vec2(pixelSide+2.0)))) return;
                // Emission reuses the native color*B profile; logFlux carries
                // the observed/rest visible-luminance ratio only, so output is
                // pixel-identical to the stock sprite at D=1.
                float logFlux = log(restY);
                vec3 rgbPerY = raw/restY;
                // Per-pixel luminance bound (fragment applies it as a hard
                // clamp on the emitted luma): a point source cannot out-shine
                // its own shifted photosphere. Unfitted colors have no
                // shifted spectrum, so their ceiling is exactly the prior
                // flux spread over the shrunken sprite (never binds).
                float capLum = restY/(sizeFactor*sizeFactor);
                // Fitted temperatures are positive; <=0 marks an unfitted catalog
                // color, which keeps its raw color and flux prior at all D.
                if (star.directionTemperature.w > 0.0 && doppler != 1.0) {
                    float temperature = star.directionTemperature.w * doppler;
                    if (!(temperature >= 1.0) || isinf(temperature) || isnan(temperature)) return;
                    vec4 shifted = spectrum(temperature);
                    // Resolved-source luminance bound: the shifted thermal
                    // luminance ratio (beaming-free) times the rest peak.
                    capLum = restY*exp(min(shifted.w-star.restThermal.w,20.0));
                    vec3 originalXyz = vec3(
                        dot(raw,vec3(0.412390799,0.357584339,0.180480788)),
                        restY,
                        dot(raw,vec3(0.019330819,0.119194780,0.950532152)));
                    originalXyz /= originalXyz.x+originalXyz.y+originalXyz.z;
                    float logD = log(doppler);
                    float weight = exp(-logD*logD);
                    vec3 gain = mix(vec3(1.0),originalXyz/star.restThermal.xyz,weight);
                    vec3 xyz = shifted.xyz*gain;
                    xyz /= xyz.x+xyz.y+xyz.z;
                    if (!(xyz.y > 0.0)) return;
                    rgbPerY = displayRgb(xyz);
                    // Point-source beaming: bolometric flux scales as D^2
                    // (photon energy and arrival rate each scale by D).
                    logFlux += shifted.w-star.restThermal.w+2.0*logD;
                }
                // Crowding bound: once compressed stars overlap roughly one
                // per pixel, the packed field's surface brightness tends to
                // the mean photosphere, not the sum - dim each star's cap by
                // ~(Dref/D)^2 above the ~1-star/px onset. Dref~32 is the
                // order-of-magnitude onset for this catalog density and
                // sprite footprint; rear stars (D<32) are untouched.
                capLum *= min(1.0, (32.0/doppler)*(32.0/doppler));
                // Flux conservation for the resized sprite: emission per
                // pixel scales by the inverse area factor, so the star's
                // integrated observed flux is unchanged - a forward star
                // (sizeFactor=1/D) concentrates its D^2 flux into the
                // D^-2-area cap for a D^4 surface-brightness peak.
                logFlux -= 2.0*log(sizeFactor);
                if (any(isnan(rgbPerY)) || any(isinf(rgbPerY)) || isnan(logFlux) || isinf(logFlux)) return;
                vec2 corners[6] = vec2[6](vec2(-1,-1),vec2(1,-1),vec2(1,1),
                    vec2(-1,-1),vec2(1,1),vec2(-1,1));
                vec2 offset = corners[gl_VertexIndex]*pixelSide;
                // Native adds 1.0 clip-z so sprites bias slightly closer and
                // overlay background (Milky Way) depth while still failing
                // behind opaque geometry.
                gl_Position = vec4(clip.xy+offset*(2.0/size)*clip.w,clip.z+1.0,clip.w);
                outCenter = center;
                outRgbPerY = rgbPerY;
                outLogFlux = logFlux;
                outCapLum = capLum;
                outProfile = vec3(maxTheta, pixelSide, max(size.x,size.y)/1920.0);
            }
            """;

        // Faithful port of the stock star sprite: native UV-octagon radial
        // profile (sharp core + 1/theta^2 wing + edge taper with slight signed
        // fringe) emitting raw*B*S where S is the relativistic luminance ratio
        // (1 at rest), so output matches the native sprite pixel-for-pixel at
        // zero observer speed.
        internal const string Fragment = """
            #version 450
            layout(location=0) flat in vec3 inRgbPerY;
            layout(location=1) flat in vec2 inCenter;
            layout(location=2) flat in float inLogFlux;
            layout(location=3) flat in vec3 inProfile;
            layout(location=4) flat in float inCapLum;
            layout(location=0) out vec4 outColor;
            void main() {
                vec2 d=gl_FragCoord.xy-inCenter;
                float offset=length(d)/inProfile.y;
                // Octagon edge: limit(phi) >= 0.924, so fragments inside 0.9
                // skip the boundary test (also avoids atan(0,0) at the core).
                if (offset > 0.9) {
                    float phi=mod(atan(d.y,d.x),0.7853981634);
                    float limit=0.708/(0.708*cos(phi)+0.292*sin(phi));
                    if (!(offset <= limit)) discard;
                }
                // Native psf_glow: theta scales by max(W,H)/1920.
                float theta=offset*0.01*inProfile.z;
                float ratio=inProfile.x/max(theta,1e-5)-1.0;
                float wing=0.0016*ratio*ratio*(1.0-smoothstep(inProfile.x*0.7,inProfile.x,theta));
                float centerMix=1.0-smoothstep(0.0,0.005,theta);
                float profile=min(1.5,(wing+(1.5-wing)*centerMix)*(1.0-offset));
                vec3 rgb=vec3(0.0);
                for (int i=0;i<3;i++) {
                    if (inRgbPerY[i] > 0.0)
                        rgb[i]=exp(min(log(inRgbPerY[i])+inLogFlux,11.089866488))*profile;
                }
                // A point source cannot out-shine its own shifted
                // photosphere: bound per-pixel luminance at the resolved
                // (beaming-free) surface emission. inCapLum*1.5 is the cap in
                // emitted-rgb luminance (profile peaks at 1.5). Hard clamp -
                // a soft knee would dim the rest peak and break
                // pixel-parity at D=1. Preserves chromaticity.
                float lum=dot(rgb,vec3(0.212639006,0.715168679,0.072192315));
                float cap=inCapLum*1.5;
                if (lum > cap) rgb*=cap/lum;
                if (any(isnan(rgb)) || any(isinf(rgb))) discard;
                // Native writes alpha=1 into the additive target; match so
                // downstream alpha consumers see identical accumulation.
                outColor=vec4(clamp(rgb,vec3(-65504.0),vec3(65504.0)),1.0);
            }
            """;
    }
}
