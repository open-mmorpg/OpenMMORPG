// Stylised water for the demo island's sea.
//
// The sea is one surface fifteen hundred metres across and the player stands at the
// waterline on a beach, so the same material has to hold up at arm's length and at
// the horizon. That constraint is what most of the decisions below answer to.
//
// The waves are a sum of sine trains rather than a scrolling normal map, for two
// reasons. A texture would be more demo art to license and ship, and the budget for
// that is spent. The real reason is that a wave field which is an analytic function
// of world position has an analytic slope as well, so the surface normal can be
// evaluated per pixel without caring how finely the mesh is divided. Geometry then
// only has to be dense enough where the vertical displacement is actually visible —
// near the shore — and the shading carries the water everywhere else.
//
// The octaves within a train are deliberately not harmonics. Frequency steps by
// 2.13 rather than 2, headings fan out around the wind instead of sharing it, and
// each octave travels at the speed the deep-water dispersion relation gives it, so
// short waves lag behind long ones. Sines at exact multiples of one frequency, all
// running the same way at the same speed, resolve into a corduroy ripple that
// visibly repeats and reads as cloth. Detuning them is most of what makes this look
// like water.
//
// Shore foam and the shallow-to-deep colour are read from the depth buffer, and the
// refraction from the opaque colour copy, so this needs Depth Texture and Opaque
// Texture enabled on the pipeline asset. PC_RPAsset has both; Mobile_RPAsset has
// neither, and the two toggles below exist to turn each effect off for a pipeline
// that cannot feed it.
Shader "Demo/StylizedWater"
{
    Properties
    {
        [Header(Colour)]
        _ShallowColor ("Shallow", Color) = (0.42, 0.78, 0.74, 1)
        _DeepColor ("Deep", Color) = (0.03, 0.20, 0.33, 1)
        _HorizonColor ("Horizon", Color) = (0.62, 0.80, 0.86, 1)
        _DepthDistance ("Depth To Deep Colour", Float) = 5.5
        _ShallowOpacity ("Shallow Opacity", Range(0, 1)) = 0.18
        _DeepOpacity ("Deep Opacity", Range(0, 1)) = 0.94
        _FresnelPower ("Horizon Falloff", Range(1, 12)) = 5

        [Header(Swell)]
        _WaveHeight ("Height", Float) = 0.18
        _WaveLength ("Length", Float) = 13
        _WaveSpeed ("Speed", Float) = 0.6
        _WaveHeading ("Heading", Range(0, 360)) = 35
        _WaveSpread ("Spread", Range(0, 60)) = 22
        // Vertical displacement is a lie past the point where you can see a wave move,
        // and a mesh this size is coarse out there. Fading it out is cheaper than
        // tessellating water nobody is looking at.
        _DisplaceFade ("Displacement Fade (near, far)", Vector) = (110, 280, 0, 0)
        _SwellNormalFade ("Swell Normal Fade (near, far)", Vector) = (320, 900, 0, 0)

        [Header(Ripples)]
        _RippleStrength ("Strength", Range(0, 2)) = 1
        _RippleLength ("Length", Float) = 2.4
        _RippleSpeed ("Speed", Float) = 0.4
        // Ripples are around a metre across. Past a couple of hundred metres they are
        // smaller than a pixel, and what should be a shimmer becomes crawling noise.
        _RippleFade ("Ripple Fade (near, far)", Vector) = (45, 200, 0, 0)
        _NormalStrength ("Normal Strength", Range(0, 3)) = 1

        [Header(Light)]
        _GlintColor ("Sun Glint", Color) = (1, 0.97, 0.88, 1)
        _Glossiness ("Glint Tightness", Range(0, 1)) = 0.85
        _GlintSharpen ("Glint Hardness", Range(0, 1)) = 0.35
        _SparkleStrength ("Sparkle", Range(0, 4)) = 1.1
        _SunDiffuse ("Sun On Water Colour", Range(0, 1)) = 0.25

        [Header(Shore)]
        [Toggle(_SHORE_ON)] _Shore ("Depth Fade And Foam", Float) = 1
        _FoamColor ("Foam", Color) = (0.95, 0.98, 1, 1)
        _FoamDepth ("Foam Depth", Float) = 1.4
        _FoamCoverage ("Foam Coverage", Range(0, 1)) = 0.35
        _FoamBreakup ("Foam Breakup", Range(0, 1)) = 0.55
        _FoamSoftness ("Foam Softness", Range(0.01, 1)) = 0.22
        // How far the swell drags the foam line up and down the beach. This is what
        // stops the waterline being a fixed contour traced around the island.
        _FoamSwell ("Foam Follows Swell", Range(0, 4)) = 1.6

        [Header(Refraction)]
        [Toggle(_REFRACTION_ON)] _Refraction ("Refraction", Float) = 1
        _RefractionStrength ("Strength", Range(0, 1)) = 0.22
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            // The camera dips below sea level looking up at the hills from the beach.
            // Culling the back face would make the sea vanish for those few frames.
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #pragma shader_feature_local _ _SHORE_ON
            #pragma shader_feature_local _ _REFRACTION_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            // Every property lives in one block so the SRP batcher will take this
            // shader; a property declared outside it silently drops the batch.
            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _HorizonColor;
                float _DepthDistance;
                half _ShallowOpacity;
                half _DeepOpacity;
                half _FresnelPower;

                float _WaveHeight;
                float _WaveLength;
                float _WaveSpeed;
                float _WaveHeading;
                float _WaveSpread;
                float4 _DisplaceFade;
                float4 _SwellNormalFade;

                half _RippleStrength;
                float _RippleLength;
                float _RippleSpeed;
                float4 _RippleFade;
                half _NormalStrength;

                half4 _GlintColor;
                half _Glossiness;
                half _GlintSharpen;
                half _SparkleStrength;
                half _SunDiffuse;

                half4 _FoamColor;
                float _FoamDepth;
                half _FoamCoverage;
                half _FoamBreakup;
                half _FoamSoftness;
                half _FoamSwell;

                half _RefractionStrength;
            CBUFFER_END

            // ---- wave field ---------------------------------------------------
            //
            // A single plane wave running along `dir`, of wavelength 2*pi/k. It
            // contributes its height and, so the surface can be shaded without a
            // normal map, the two horizontal derivatives of that height.
            void AddOctave(float2 pos, float2 dir, float k, float amp, float omega, float t,
                           inout float height, inout float2 gradient)
            {
                float s, c;
                sincos(dot(dir, pos) * k + t * omega, s, c);
                height += amp * s;
                gradient += dir * (amp * k * c);
            }

            // A train of `octaves` waves fanned around one heading. See the note at the
            // top of the file for why the octaves are detuned rather than harmonic.
            //
            // `amplitude` is the height of the finished train, not of its first octave:
            // the octaves are summed and then normalised, so changing the octave count
            // does not change how tall the water is.
            void WaveTrain(float2 pos, float t, float heading, float wavelength,
                           float amplitude, float speed, float spread, int octaves,
                           out float height, out float2 gradient)
            {
                height = 0;
                gradient = 0;

                float k0 = TWO_PI / max(wavelength, 0.01);
                float k = k0;
                float amp = 1;
                float total = 0;

                [unroll(4)]
                for (int i = 0; i < octaves; i++)
                {
                    // Golden-ratio offsets: an even-looking spread around the heading
                    // that never lands two octaves on the same bearing.
                    float angle = heading + spread * (frac(i * 0.6180339) * 2 - 1);
                    float2 dir = float2(cos(angle), sin(angle));
                    // Deep water: phase speed goes as 1/sqrt(k), and this form leaves
                    // the longest octave travelling at exactly `speed`.
                    AddOctave(pos, dir, k, amp, speed * sqrt(k * k0), t, height, gradient);

                    total += amp;
                    k *= 2.13;
                    amp *= 0.52;
                }

                float norm = amplitude / max(total, 1e-4);
                height *= norm;
                gradient *= norm;
            }

            // Slope of a height field y = h(x, z) as a world normal.
            float3 GradientToNormal(float2 gradient, float strength)
            {
                return normalize(float3(-gradient.x * strength, 1, -gradient.y * strength));
            }

            float FadeOut(float toCamera, float2 range)
            {
                return 1 - smoothstep(range.x, range.y, toCamera);
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 positionNDC : TEXCOORD1;
                float fogFactor : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);

                float height;
                float2 gradient;
                WaveTrain(positionWS.xz, _Time.y, radians(_WaveHeading), _WaveLength,
                          _WaveHeight, _WaveSpeed, radians(_WaveSpread), 4, height, gradient);

                float toCamera = distance(positionWS, GetCameraPositionWS());
                positionWS.y += height * FadeOut(toCamera, _DisplaceFade.xy);

                float4 positionCS = TransformWorldToHClip(positionWS);

                // Same derivation as GetVertexPositionInputs, which cannot be used here
                // because the displacement happens in world space.
                float4 ndc = positionCS * 0.5;
                output.positionNDC.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
                output.positionNDC.zw = positionCS.zw;

                output.positionCS = positionCS;
                output.positionWS = positionWS;
                output.fogFactor = ComputeFogFactor(positionCS.z);
                return output;
            }

            half4 frag(Varyings input, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 positionWS = input.positionWS;
                float3 viewDir = normalize(GetCameraPositionWS() - positionWS);
                float toCamera = distance(positionWS, GetCameraPositionWS());

                // ---- surface ---------------------------------------------------
                //
                // The swell is evaluated a second time here rather than interpolated
                // from the vertex stage: interpolating a gradient across quads several
                // metres wide flattens the crests into facets, and the whole point of
                // an analytic wave field is that a pixel can ask for its own slope.
                float swellHeight;
                float2 swellGradient;
                WaveTrain(positionWS.xz, _Time.y, radians(_WaveHeading), _WaveLength,
                          _WaveHeight, _WaveSpeed, radians(_WaveSpread), 4,
                          swellHeight, swellGradient);

                float rippleHeight;
                float2 rippleGradient;
                WaveTrain(positionWS.xz, _Time.y, radians(_WaveHeading + 55), _RippleLength,
                          1, _RippleSpeed, radians(40), 3, rippleHeight, rippleGradient);

                float rippleFade = FadeOut(toCamera, _RippleFade.xy);
                float swellFade = FadeOut(toCamera, _SwellNormalFade.xy);

                // The ripple train is evaluated at unit amplitude so its height can double
                // as a noise field for the foam below. Its slope is brought back down to
                // what a ripple a couple of metres across and an inch high really has.
                const float rippleSlope = 0.02;
                float2 gradient = swellGradient * swellFade
                                + rippleGradient * (rippleSlope * _RippleStrength * rippleFade);
                float3 normalWS = GradientToNormal(gradient, _NormalStrength);
                normalWS.y *= IS_FRONT_VFACE(facing, 1, -1);

                float2 screenUV = input.positionNDC.xy / input.positionNDC.w;
                float surfaceEye = input.positionNDC.w;

                // ---- how deep is the water under this pixel ---------------------
                float waterDepth = _DepthDistance;
            #if defined(_SHORE_ON)
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                waterDepth = max(sceneEye - surfaceEye, 0);
            #endif

                half3 behind = 0;
            #if defined(_REFRACTION_ON)
                // Offset by the surface slope, damped by distance so the wobble stays
                // the same size on screen instead of tearing across the horizon, and by
                // depth so the shallows near the sand stay legible.
                float2 offset = normalWS.xz * _RefractionStrength
                              * saturate(waterDepth) * (8 / max(surfaceEye, 8));
                float2 refractUV = screenUV + offset;

                float refractEye = LinearEyeDepth(SampleSceneDepth(refractUV), _ZBufferParams);
                // A sample that turns out to be nearer than the water belongs to
                // something standing in front of it — a pier post, the player's legs —
                // and bending that into the water smears it across the surface. Fall
                // back to looking straight through.
                UNITY_BRANCH
                if (refractEye < surfaceEye)
                {
                    refractUV = screenUV;
                    refractEye = surfaceEye + waterDepth;
                }
                behind = SampleSceneColor(refractUV);
                #if defined(_SHORE_ON)
                    // Measure the water column against the pixel actually being shown
                    // through the surface, or the colour and the depth disagree along
                    // the shoreline and the foam gets a bright fringe.
                    waterDepth = max(refractEye - surfaceEye, 0);
                #endif
            #endif

                float depth01 = saturate(waterDepth / max(_DepthDistance, 0.01));

                // ---- colour ----------------------------------------------------
                half3 color = lerp(_ShallowColor.rgb, _DeepColor.rgb, depth01);
                half alpha = lerp(_ShallowOpacity, _DeepOpacity, depth01);

                // Grazing angles see the sky rather than the water, which is what turns
                // the far half of the sea into a band that sits under the horizon
                // instead of a blue sheet running up to it.
                half fresnel = pow(1 - saturate(dot(normalWS, viewDir)), _FresnelPower);
                color = lerp(color, _HorizonColor.rgb, fresnel);
                alpha = lerp(alpha, 1, fresnel);

                // ---- light -----------------------------------------------------
                //
                // The colour above is the authored one and the light only says how
                // bright the day is. Lighting the sea as a proper diffuse surface
                // washes that colour out at noon, and leaving the light out entirely
                // leaves the water at noon colour at midnight, which the demo's day
                // cycle makes very obvious.
                float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half shadow = mainLight.shadowAttenuation;

                half3 ambient = SampleSH(normalWS);
                half3 sun = mainLight.color * shadow * saturate(dot(normalWS, mainLight.direction));
                color *= ambient + sun * _SunDiffuse;

                float3 halfVector = normalize(mainLight.direction + viewDir);
                // Kept at full precision: the sparkle exponent below is large enough that
                // a half-precision log2 near one quantises the highlight into steps.
                float nDotH = saturate(dot(normalWS, halfVector));

                half glint = pow(nDotH, exp2(7 * _Glossiness + 1));
                // Hardening the falloff turns the sun's reflection from a photographic
                // smear into a shape drawn on the water, which is the stylised half of
                // this shader doing its work.
                glint = lerp(glint, smoothstep(0.22, 0.34, glint), _GlintSharpen);

                // A far tighter lobe on the same normal — tight enough that only the
                // ripples ever tilt into it, which is why it has to stay well clear of
                // the glint exponent above or the two collapse into one highlight. It
                // lands as scattered points of light, and fades out with the ripples.
                half sparkle = pow(nDotH, 600) * _SparkleStrength * rippleFade;

                half3 specular = (glint + sparkle) * _GlintColor.rgb * mainLight.color * shadow;
                color += specular;
                // Otherwise the highlight is drawn onto see-through water and the
                // seabed shows through the middle of the sun.
                alpha = saturate(alpha + (glint + sparkle) * 0.5);

                // ---- shore -----------------------------------------------------
            #if defined(_SHORE_ON)
                // The swell carries the foam line up and down the beach with it, so the
                // waterline breathes instead of sitting where the depth buffer says the
                // sand is.
                float band = 1 - saturate((waterDepth + swellHeight * _FoamSwell)
                                          / max(_FoamDepth, 0.01));
                // Move the threshold about with the ripple field rather than the value,
                // which frays the edge of the band without thinning the whole of it.
                half edge = _FoamCoverage + _FoamBreakup * 0.5 * rippleHeight;
                half foam = smoothstep(edge, edge + _FoamSoftness, band);

                color = lerp(color, _FoamColor.rgb, foam);
                alpha = max(alpha, foam * _FoamColor.a);
            #endif

            #if defined(_REFRACTION_ON)
                // Composite against the refracted scene here instead of leaving it to
                // the hardware blend, because the hardware would blend against the
                // undistorted pixel and undo the refraction.
                color = lerp(behind, color, alpha);
                alpha = 1;
            #endif

                color = MixFog(color, input.fogFactor);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
