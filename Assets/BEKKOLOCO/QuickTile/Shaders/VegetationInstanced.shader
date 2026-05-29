// VegetationInstanced.shader
// URP-compatible shader with wind animation for vegetation cards.
// Lighting matches PaintShader_FinalPerfect (ShadowColor + cookies).
// Supports both GPU indirect (DrawMeshInstancedIndirect) and CPU instancing (DrawMeshInstanced).

Shader "BEKKOLOCO/VegetationInstanced"
{
    Properties
    {
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Color   ("Color Tint",  Color) = (1,1,1,1)
        _Cutoff  ("Alpha Cutoff", Range(0,1)) = 0.516
        _ShadowColor ("Shadow Color", Color) = (0.0, 0.55, 0.6, 1)
        _CookieShadowStrength ("Cookie Shadow Strength", Range(0,1)) = 0.71

        [Header(Wind)]
        _WindStrength ("Wind Strength", Range(0, 1)) = 0.432
        _WindSpeed    ("Wind Speed",    Range(0, 5)) = 1.27
        _WindDirection("Wind Direction (XZ)", Vector) = (1, 0, 0.5, 0)
        _WindTurbulence("Turbulence",   Range(0, 2)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
        }

        // ════════════════════════════════════════════════════════════
        //  Pass 0: Forward Lit  (same lighting as PaintShader)
        // ════════════════════════════════════════════════════════════
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            AlphaToMask On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // ── Procedural instancing (GPU indirect path) ──
            struct VegetationInstance
            {
                float3 position;
                float  rotation;
                float  scale;
                uint   packedGroundColor; // unused in Card mode, but layout must match compute buffer
            };

            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                StructuredBuffer<VegetationInstance> _VisibleInstances;
            #endif

            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    VegetationInstance inst = _VisibleInstances[unity_InstanceID];

                    float s  = inst.scale;
                    float c  = cos(inst.rotation);
                    float sn = sin(inst.rotation);
                    float invS = 1.0 / max(s, 0.0001);

                    float tx = -((invS * c) * inst.position.x + (-invS * sn) * inst.position.z);
                    float ty = -(invS * inst.position.y);
                    float tz = -((invS * sn) * inst.position.x + (invS * c) * inst.position.z);

                    unity_ObjectToWorld = 0.0;
                    unity_ObjectToWorld._11_21_31_41 = float4(s * c, 0.0, -s * sn, 0.0);
                    unity_ObjectToWorld._12_22_32_42 = float4(0.0, s, 0.0, 0.0);
                    unity_ObjectToWorld._13_23_33_43 = float4(s * sn, 0.0, s * c, 0.0);
                    unity_ObjectToWorld._14_24_34_44 = float4(inst.position.x, inst.position.y, inst.position.z, 1.0);

                    unity_WorldToObject = 0.0;
                    unity_WorldToObject._11_21_31_41 = float4(invS * c, 0.0, invS * sn, 0.0);
                    unity_WorldToObject._12_22_32_42 = float4(0.0, invS, 0.0, 0.0);
                    unity_WorldToObject._13_23_33_43 = float4(-invS * sn, 0.0, invS * c, 0.0);
                    unity_WorldToObject._14_24_34_44 = float4(tx, ty, tz, 1.0);
                #endif
            }

            // ── Properties ──
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _Color;
                half   _Cutoff;
                half4  _ShadowColor;
                float  _CookieShadowStrength;
                float  _WindStrength;
                float  _WindSpeed;
                float4 _WindDirection;
                float  _WindTurbulence;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                float  fogFactor   : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // ── Wind displacement ──
                float windWeight = saturate(input.positionOS.y);
                if (_WindStrength > 0.001 && windWeight > 0.001)
                {
                    float3 worldPosPreWind = TransformObjectToWorld(input.positionOS.xyz);
                    float2 windDir = normalize(_WindDirection.xz + float2(0.001, 0.001));
                    float phase = _Time.y * _WindSpeed + dot(worldPosPreWind.xz, windDir) * 0.5;
                    float sway = sin(phase) + sin(phase * 2.3 + 0.7) * _WindTurbulence * 0.5;
                    float displacement = sway * _WindStrength * windWeight;
                    input.positionOS.x += displacement * windDir.x;
                    input.positionOS.z += displacement * windDir.y;
                }

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS  = posInputs.positionCS;
                output.positionWS  = posInputs.positionWS;
                output.normalWS    = TransformObjectToWorldNormal(input.normalOS);
                output.uv          = TRANSFORM_TEX(input.uv, _MainTex);
                output.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);
                output.shadowCoord = TransformWorldToShadowCoord(posInputs.positionWS);

                return output;
            }

            half4 frag(Varyings input, half facing : VFACE) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 texColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color;

                // Alpha cutout
                clip(texColor.a - _Cutoff);

                // ── Lighting (same as PaintShader_FinalPerfect) ──
                Light mainLight    = GetMainLight(input.shadowCoord);
                float shadowFactor = mainLight.shadowAttenuation;

                #ifdef _LIGHT_COOKIES
                    float cookieAtt = SampleMainLightCookie(input.positionWS).r;
                    float cookieMix = lerp(1.0, cookieAtt, _CookieShadowStrength);
                    shadowFactor   *= cookieMix;
                #endif

                float3 lit      = texColor.rgb * mainLight.color.rgb;
                float3 shadowed = texColor.rgb * _ShadowColor.rgb;
                float3 finalRGB = lerp(shadowed, lit, shadowFactor);

                finalRGB = MixFog(finalRGB, input.fogFactor);
                return half4(finalRGB, texColor.a);
            }
            ENDHLSL
        }

        // ════════════════════════════════════════════════════════════
        //  Pass 1: Shadow Caster (same style as PaintShader)
        // ════════════════════════════════════════════════════════════
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _Color;
                half   _Cutoff;
                half4  _ShadowColor;
                float  _CookieShadowStrength;
                float  _WindStrength;
                float  _WindSpeed;
                float4 _WindDirection;
                float  _WindTurbulence;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);

                // Wind
                float windWeight = saturate(IN.positionOS.y);
                if (_WindStrength > 0.001 && windWeight > 0.001)
                {
                    float3 wp = TransformObjectToWorld(IN.positionOS.xyz);
                    float2 wd = normalize(_WindDirection.xz + float2(0.001, 0.001));
                    float phase = _Time.y * _WindSpeed + dot(wp.xz, wd) * 0.5;
                    float sway = sin(phase) + sin(phase * 2.3 + 0.7) * _WindTurbulence * 0.5;
                    float disp = sway * _WindStrength * windWeight;
                    IN.positionOS.x += disp * wd.x;
                    IN.positionOS.z += disp * wd.y;
                }

                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                float3 worldNormal = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionCS = TransformWorldToHClip(ApplyShadowBias(worldPos, worldNormal, _MainLightPosition.xyz));
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // ════════════════════════════════════════════════════════════
        //  Pass 2: Depth Only
        // ════════════════════════════════════════════════════════════
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma target 2.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _Color;
                half   _Cutoff;
                half4  _ShadowColor;
                float  _CookieShadowStrength;
                float  _WindStrength;
                float  _WindSpeed;
                float4 _WindDirection;
                float  _WindTurbulence;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_TARGET
            {
                half alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // ════════════════════════════════════════════════════════════
        //  Pass 3: Depth Normals (post-processing / DOF)
        // ════════════════════════════════════════════════════════════
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma target 2.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _Color;
                half   _Cutoff;
                half4  _ShadowColor;
                float  _CookieShadowStrength;
                float  _WindStrength;
                float  _WindSpeed;
                float4 _WindDirection;
                float  _WindTurbulence;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);
                output.positionCS = vertexInput.positionCS;
                output.normalWS = normalInput.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            half4 DepthNormalsFragment(Varyings input) : SV_TARGET
            {
                half alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a;
                clip(alpha - _Cutoff);
                return half4(normalize(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
