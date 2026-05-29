// VegetationGrass.shader
// URP GPU-indirect grass shader — Zelda-style coloured blades, no texture required.
// All passes use procedural:setup for DrawMeshInstancedIndirect compatibility.
// Lighting matches PaintShader_FinalPerfect (ShadowColor + cookies).
// Per-instance ground color: blade hue comes from the painted ground texture.
// _Color / _BottomColor act as top/base tint multipliers over that sampled hue.

Shader "BEKKOLOCO/VegetationGrass"
{
    Properties
    {
        [Header(Color)]
        _Color      ("Top Color",    Color) = (0.35, 0.75, 0.2, 1)
        _BottomColor("Bottom Color (fallback)", Color) = (0.15, 0.4, 0.08, 1)

        [Header(Lighting)]
        _ShadowColor ("Shadow Color", Color) = (0.0, 0.55, 0.6, 1)
        _CookieShadowStrength ("Cookie Shadow Strength", Range(0,1)) = 0.71

        [Header(Ground Color)]
        _GroundColorDarken ("Ground Darken", Range(0, 1)) = 0.35
        _GroundColorDesat  ("Ground Desaturate", Range(0, 1)) = 0.2

        [Header(Wind)]
        _WindStrength  ("Wind Strength",  Range(0, 1)) = 0.432
        _WindSpeed     ("Wind Speed",     Range(0, 5)) = 1.27
        _WindDirection ("Wind Direction (XZ)", Vector) = (1, 0, 0.5, 0)
        _WindTurbulence("Turbulence",     Range(0, 2)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry+100"
            "RenderPipeline" = "UniversalPipeline"
        }

        // ════════════════════════════════════════════════════════════
        //  Pass 0: Forward Lit
        // ════════════════════════════════════════════════════════════
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

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
                uint   packedGroundColor;
            };

            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                StructuredBuffer<VegetationInstance> _VisibleInstances;
                static float3 _InstanceGroundColor;
            #endif

            float3 UnpackColorRGB(uint packed)
            {
                return float3(
                    (packed        & 0xFF) / 255.0,
                    ((packed >> 8) & 0xFF) / 255.0,
                    ((packed >> 16)& 0xFF) / 255.0
                );
            }

            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    VegetationInstance inst = _VisibleInstances[unity_InstanceID];
                    _InstanceGroundColor = UnpackColorRGB(inst.packedGroundColor);

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

            // WindZone globals (set by VegetationGPURenderer from scene WindZone)
            float4 _WindZoneDirection;
            float  _WindZoneStrength;
            float  _WindZoneSpeed;
            float  _WindZoneTurbulence;

            CBUFFER_START(UnityPerMaterial)
                half4  _Color;
                half4  _BottomColor;
                half4  _ShadowColor;
                float  _CookieShadowStrength;
                float  _GroundColorDarken;
                float  _GroundColorDesat;
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
                float  heightGrad  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                float  fogFactor   : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                float3 groundColor : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.heightGrad = input.uv.y;

                // Pass per-instance ground color to fragment
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    output.groundColor = _InstanceGroundColor;
                #else
                    output.groundColor = float3(1.0, 1.0, 1.0);
                #endif

                // ── Wind displacement (WindZone globals override material params) ──
                float  wzStr = _WindZoneStrength;
                float  finalWindStr   = wzStr > 0.001 ? wzStr             : _WindStrength;
                float  finalWindSpd   = wzStr > 0.001 ? _WindZoneSpeed    : _WindSpeed;
                float2 finalWindDir2  = wzStr > 0.001 ? _WindZoneDirection.xz : _WindDirection.xz;
                float  finalWindTurb  = wzStr > 0.001 ? _WindZoneTurbulence : _WindTurbulence;

                float windWeight = output.heightGrad;
                if (finalWindStr > 0.001 && windWeight > 0.001)
                {
                    float3 worldPosPreWind = TransformObjectToWorld(input.positionOS.xyz);
                    float2 windDir = normalize(finalWindDir2 + float2(0.001, 0.001));
                    float phase = _Time.y * finalWindSpd + dot(worldPosPreWind.xz, windDir) * 0.5;
                    float sway = sin(phase) + sin(phase * 2.3 + 0.7) * finalWindTurb * 0.5;
                    float displacement = sway * finalWindStr * windWeight;
                    input.positionOS.x += displacement * windDir.x;
                    input.positionOS.z += displacement * windDir.y;
                }

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS  = posInputs.positionCS;
                output.positionWS  = posInputs.positionWS;
                output.normalWS    = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);
                output.shadowCoord = TransformWorldToShadowCoord(posInputs.positionWS);

                return output;
            }

            half4 frag(Varyings input, half facing : VFACE) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // Gradient: ground color at base → _Color (grass tint) at tip
                half3 groundBase = input.groundColor;
                half lumGround = dot(groundBase, half3(0.299, 0.587, 0.114));
                if (lumGround < 0.01) groundBase = _BottomColor.rgb;

                // Tone down the sampled ground color (darken + desaturate)
                groundBase *= (1.0 - _GroundColorDarken);
                half  grey = dot(groundBase, half3(0.299, 0.587, 0.114));
                groundBase = lerp(groundBase, half3(grey, grey, grey), _GroundColorDesat);

                half3 bladeColor = lerp(groundBase, _Color.rgb, saturate(input.heightGrad));

                // ── Lighting (PaintShader_FinalPerfect style) ──
                Light mainLight    = GetMainLight(input.shadowCoord);
                float shadowFactor = mainLight.shadowAttenuation;

                #ifdef _LIGHT_COOKIES
                    float cookieAtt = SampleMainLightCookie(input.positionWS).r;
                    float cookieMix = lerp(1.0, cookieAtt, _CookieShadowStrength);
                    shadowFactor   *= cookieMix;
                #endif

                float3 lit      = bladeColor * mainLight.color.rgb;
                float3 shadowed = bladeColor * _ShadowColor.rgb;
                float3 finalRGB = lerp(shadowed, lit, shadowFactor);

                finalRGB = MixFog(finalRGB, input.fogFactor);
                return half4(finalRGB, 1.0);
            }
            ENDHLSL
        }

        // ════════════════════════════════════════════════════════════
        //  Pass 1: Shadow Caster
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
            #pragma target 4.5
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct VegetationInstance
            {
                float3 position;
                float  rotation;
                float  scale;
                uint   packedGroundColor;
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

            // WindZone globals
            float4 _WindZoneDirection;
            float  _WindZoneStrength;
            float  _WindZoneSpeed;
            float  _WindZoneTurbulence;

            CBUFFER_START(UnityPerMaterial)
                half4  _Color;
                half4  _BottomColor;
                half4  _ShadowColor;
                float  _CookieShadowStrength;
                float  _GroundColorDarken;
                float  _GroundColorDesat;
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
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);

                // WindZone override
                float  wzStr = _WindZoneStrength;
                float  fStr  = wzStr > 0.001 ? wzStr             : _WindStrength;
                float  fSpd  = wzStr > 0.001 ? _WindZoneSpeed    : _WindSpeed;
                float2 fDir  = wzStr > 0.001 ? _WindZoneDirection.xz : _WindDirection.xz;
                float  fTurb = wzStr > 0.001 ? _WindZoneTurbulence : _WindTurbulence;

                float windWeight = IN.uv.y;
                if (fStr > 0.001 && windWeight > 0.001)
                {
                    float3 wp = TransformObjectToWorld(IN.positionOS.xyz);
                    float2 wd = normalize(fDir + float2(0.001, 0.001));
                    float phase = _Time.y * fSpd + dot(wp.xz, wd) * 0.5;
                    float sway = sin(phase) + sin(phase * 2.3 + 0.7) * fTurb * 0.5;
                    float disp = sway * fStr * windWeight;
                    IN.positionOS.x += disp * wd.x;
                    IN.positionOS.z += disp * wd.y;
                }

                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                float3 worldNormal = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionCS = TransformWorldToHClip(ApplyShadowBias(worldPos, worldNormal, _MainLightPosition.xyz));
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
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
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct VegetationInstance
            {
                float3 position;
                float  rotation;
                float  scale;
                uint   packedGroundColor;
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

            CBUFFER_START(UnityPerMaterial)
                half4  _Color;
                half4  _BottomColor;
                half4  _ShadowColor;
                float  _CookieShadowStrength;
                float  _GroundColorDarken;
                float  _GroundColorDesat;
                float  _WindStrength;
                float  _WindSpeed;
                float4 _WindDirection;
                float  _WindTurbulence;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                return output;
            }

            half4 frag(Varyings input) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }

        // ════════════════════════════════════════════════════════════
        //  Pass 3: Depth Normals
        // ════════════════════════════════════════════════════════════
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct VegetationInstance
            {
                float3 position;
                float  rotation;
                float  scale;
                uint   packedGroundColor;
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

            CBUFFER_START(UnityPerMaterial)
                half4  _Color;
                half4  _BottomColor;
                half4  _ShadowColor;
                float  _CookieShadowStrength;
                float  _GroundColorDarken;
                float  _GroundColorDesat;
                float  _WindStrength;
                float  _WindSpeed;
                float4 _WindDirection;
                float  _WindTurbulence;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);
                output.positionCS = vertexInput.positionCS;
                output.normalWS = normalInput.normalWS;
                return output;
            }

            half4 frag(Varyings input) : SV_TARGET
            {
                return half4(normalize(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
