// ──────────────────────────────────────────────────────────────────────────────
//  BEKKOLOCO / PaintShader_FinalPerfect  –  cookie treated as SHADOW + strength
//  WITH PROPER SHADOW RECEIVING (URP)
//  - Uses URP TEXTURE2D/SAMPLER/SAMPLE_TEXTURE2D macros for mask sampling
//  - Debug toggle to visualize mask (R/G)
//  - DEPTH OF FIELD FIXED (with DepthNormals pass)
// ──────────────────────────────────────────────────────────────────────────────
Shader "BEKKOLOCO/PaintShader_FinalPerfect"
{
    Properties
    {
        _Tex0        ("Base Texture"                 , 2D   ) = "white" {}
        _Tex1        ("Texture 1 (Red channel)"     , 2D   ) = "white" {}
        _Tex2        ("Texture 2 (Green channel)"   , 2D   ) = "white" {}
        _ShadowColor ("Shadow Color"                 , Color) = (0.5, 0.5, 0.5, 1)

        // ───── Transition Controls ─────
        [Enum(Hard,0,Smooth,1,Noise,2)] _TransitionMode ("Transition Mode", Float) = 1
        _BlendSharpness ("Blend Sharpness", Range(0.1, 20)) = 1
        _BlendSharpness1 ("Blend Sharpness 1", Range(0.1, 20)) = 1
        _BlendSharpness2 ("Blend Sharpness 2", Range(0.1, 20)) = 1
        _NoiseScale     ("Noise Scale", Range(0.1, 10)) = 1
        _NoiseScale1    ("Noise Scale 1", Range(0.1, 10)) = 1
        _NoiseScale2    ("Noise Scale 2", Range(0.1, 10)) = 1
        _NoiseStrength  ("Noise Strength", Range(0, 1)) = 0.3

        // ───── Mask Controls ─────
        _Shrink ("Shrink", Range(-0.5, 0.5)) = 0
        _Spread ("Spread", Range(-0.5, 0.5)) = 0
        _LocalMaskTex ("Local Mask Texture", 2D) = "white" {}
        _LocalMaskCutoff ("Local Mask Cutoff", Range(0, 1)) = 0.5
        [Toggle] _UseLocalMask ("Use Local Mask", Float) = 0

        // ───── Shadows/Cookies ─────
        _CookieShadowStrength ("Cookie Shadow Strength" , Range(0,1)) = 1

        // ───── Debug ─────
        [Toggle] _DEBUG_MASK ("Debug Mask", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque"  "RenderPipeline"="UniversalPipeline" }
        LOD 200
        Cull Back

        // ──────────────────────────────────────────────────────────────
        //  FORWARD-LIT   (shadows + cookie-as-shadow with strength)
        // ──────────────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 2.0

            //  shadows - ENHANCED
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            //  cookies
            #pragma multi_compile_fragment _ _LIGHT_COOKIES

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Tex0);   SAMPLER(sampler_Tex0);
            TEXTURE2D(_Tex1);   SAMPLER(sampler_Tex1);
            TEXTURE2D(_Tex2);   SAMPLER(sampler_Tex2);
            TEXTURE2D(_QT_PaintMask); SAMPLER(sampler_QT_PaintMask);
            TEXTURE2D(_LocalMaskTex); SAMPLER(sampler_LocalMaskTex);

            float4 _Tex0_ST, _Tex1_ST, _Tex2_ST;
            float4 _QT_WorldToMaskScale, _QT_WorldToMaskOffset;

            float4 _ShadowColor;
            float  _CookieShadowStrength;
            float  _TransitionMode;
            float  _BlendSharpness;
            float  _BlendSharpness1;
            float  _BlendSharpness2;
            float  _NoiseScale;
            float  _NoiseScale1;
            float  _NoiseScale2;
            float  _NoiseStrength;
            float  _Shrink;
            float  _Spread;
            float  _LocalMaskCutoff;
            float  _UseLocalMask;
            float  _DEBUG_MASK;

            // Simple noise functions
            float SimpleNoise(float2 uv) {
                return frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453);
            }
            float SmoothNoise(float2 uv) {
                float2 i = floor(uv);
                float2 f = frac(uv);
                f = f * f * (3.0 - 2.0 * f);
                float a = SimpleNoise(i);
                float b = SimpleNoise(i + float2(1.0, 0.0));
                float c = SimpleNoise(i + float2(0.0, 1.0));
                float d = SimpleNoise(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            struct Attributes {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float2 uvMask     : TEXCOORD1;
            };

            struct Varyings {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 uvWorld     : TEXCOORD2;
                float2 uvMask      : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                float2 localMaskUV : TEXCOORD5;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionWS  = TransformObjectToWorld(v.positionOS.xyz);
                o.normalWS    = TransformObjectToWorldNormal(v.normalOS);
                o.positionCS  = TransformWorldToHClip(o.positionWS);

                o.uvWorld     = o.positionWS.xz;
                o.uvMask      = o.uvWorld * _QT_WorldToMaskScale.xy + _QT_WorldToMaskOffset.xy;
                o.localMaskUV = v.uvMask;
                o.shadowCoord = TransformWorldToShadowCoord(o.positionWS);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                // 1) Mask (global RT set from C#) + shrink/spread only on painted
                float3 mask = SAMPLE_TEXTURE2D(_QT_PaintMask, sampler_QT_PaintMask, i.uvMask).rgb;
                float localMask = 1.0;
                if (_UseLocalMask > 0.5)
                {
                    localMask = SAMPLE_TEXTURE2D(_LocalMaskTex, sampler_LocalMaskTex, i.localMaskUV).r;
                    clip(localMask - _LocalMaskCutoff);
                }

                if (_DEBUG_MASK > 0.5)
                {
                    // visualize: R/G channels of mask
                    return half4(mask.rg, 0, 1);
                }

                if (mask.r > 0.01) mask.r = saturate(mask.r - _Shrink + _Spread);
                if (mask.g > 0.01) mask.g = saturate(mask.g - _Shrink + _Spread);

                // 2) Sample textures (world-planar using XZ + per-tex tiling/offset)
                float2 uv0 = i.uvWorld * _Tex0_ST.xy + _Tex0_ST.zw;
                float2 uv1 = i.uvWorld * _Tex1_ST.xy + _Tex1_ST.zw;
                float2 uv2 = i.uvWorld * _Tex2_ST.xy + _Tex2_ST.zw;

                half4 col0 = SAMPLE_TEXTURE2D(_Tex0, sampler_Tex0, uv0);
                half4 col1 = SAMPLE_TEXTURE2D(_Tex1, sampler_Tex1, uv1);
                half4 col2 = SAMPLE_TEXTURE2D(_Tex2, sampler_Tex2, uv2);

                // 3) Choose blending mode
                half4 baseCol;
                if (_TransitionMode < 0.5) // Hard
                {
                    baseCol = col0;
                    if (mask.r > 0.1)      baseCol = col1;
                    else if (mask.g > 0.1) baseCol = col2;
                }
                else if (_TransitionMode < 1.5) // Smooth
                {
                    float redW = mask.r;
                    float grnW = mask.g;
                    float blend1 = max(0.0001, _BlendSharpness1);
                    float blend2 = max(0.0001, _BlendSharpness2);
                    float expansion1 = blend1 * 0.1;
                    float expansion2 = blend2 * 0.1;
                    redW = saturate(redW * (1.0 + expansion1));
                    grnW = saturate(grnW * (1.0 + expansion2));
                    float tw1 = saturate(blend1 * 0.05);
                    float tw2 = saturate(blend2 * 0.05);
                    redW = smoothstep(tw1, 1.0, redW);
                    grnW = smoothstep(tw2, 1.0, grnW);
                    float baseW = 1.0 - saturate(redW + grnW);
                    float sum = baseW + redW + grnW + 0.001;
                    baseW /= sum; redW /= sum; grnW /= sum;
                    baseCol = col0 * baseW + col1 * redW + col2 * grnW;
                }
                else // Noise
                {
                    float noise1 = SmoothNoise(i.uvWorld * max(0.0001, _NoiseScale1));
                    float noise2 = SmoothNoise(i.uvWorld * max(0.0001, _NoiseScale2));
                    float redW = mask.r; if (mask.r > 0.01) redW += (noise1 - 0.5) * _NoiseStrength;
                    float grnW = mask.g; if (mask.g > 0.01) grnW += (noise2 - 0.5) * _NoiseStrength;
                    float blend1 = max(0.0001, _BlendSharpness1);
                    float blend2 = max(0.0001, _BlendSharpness2);
                    float expansion1 = blend1 * 0.1;
                    float expansion2 = blend2 * 0.1;
                    redW = saturate(redW * (1.0 + expansion1));
                    grnW = saturate(grnW * (1.0 + expansion2));
                    float tw1 = saturate(blend1 * 0.05);
                    float tw2 = saturate(blend2 * 0.05);
                    redW = smoothstep(tw1, 1.0, redW);
                    grnW = smoothstep(tw2, 1.0, grnW);
                    float baseW = 1.0 - saturate(redW + grnW);
                    float sum = baseW + redW + grnW + 0.001;
                    baseW /= sum; redW /= sum; grnW /= sum;
                    baseCol = col0 * baseW + col1 * redW + col2 * grnW;
                }

                // 4) Lighting
                Light mainLight     = GetMainLight(i.shadowCoord);
                float shadowFactor  = mainLight.shadowAttenuation;

                #ifdef _LIGHT_COOKIES
                    float cookieAtt = SampleMainLightCookie(i.positionWS).r;
                    float cookieMix = lerp(1.0, cookieAtt, _CookieShadowStrength);
                    shadowFactor   *= cookieMix;
                #endif

                float3 lit      = baseCol.rgb * mainLight.color.rgb;
                float3 shadowed = baseCol.rgb * _ShadowColor.rgb;
                float3 finalRGB = lerp(shadowed, lit, shadowFactor);
                return half4(finalRGB, baseCol.a * localMask);
            }
            ENDHLSL
        }

        // ──────────────────────────────────────────────────────────────
        //  SHADOW-CASTER
        // ──────────────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_LocalMaskTex); SAMPLER(sampler_LocalMaskTex);

            CBUFFER_START(UnityPerMaterial)
                float _LocalMaskCutoff;
                float _UseLocalMask;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uvMask : TEXCOORD1; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 localMaskUV : TEXCOORD0; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                float3 worldNormal = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionCS = TransformWorldToHClip(ApplyShadowBias(worldPos, worldNormal, _MainLightPosition.xyz));
                OUT.localMaskUV = IN.uvMask;
                return OUT;
            }
            half4 frag(Varyings IN) : SV_Target
            {
                if (_UseLocalMask > 0.5)
                {
                    float localMask = SAMPLE_TEXTURE2D(_LocalMaskTex, sampler_LocalMaskTex, IN.localMaskUV).r;
                    clip(localMask - _LocalMaskCutoff);
                }
                return 0;
            }
            ENDHLSL
        }

        // ──────────────────────────────────────────────────────────────
        //  DEPTH-ONLY PASS
        // ──────────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma target 2.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uvMask : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 localMaskUV : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_LocalMaskTex); SAMPLER(sampler_LocalMaskTex);

            CBUFFER_START(UnityPerMaterial)
                float _LocalMaskCutoff;
                float _UseLocalMask;
            CBUFFER_END

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.localMaskUV = input.uvMask;
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                if (_UseLocalMask > 0.5)
                {
                    float localMask = SAMPLE_TEXTURE2D(_LocalMaskTex, sampler_LocalMaskTex, input.localMaskUV).r;
                    clip(localMask - _LocalMaskCutoff);
                }
                return 0;
            }
            ENDHLSL
        }

        // ──────────────────────────────────────────────────────────────
        //  DEPTH-NORMALS PASS (REQUIRED FOR POST-PROCESSING)
        // ──────────────────────────────────────────────────────────────
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma target 2.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uvMask : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 localMaskUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_LocalMaskTex); SAMPLER(sampler_LocalMaskTex);

            CBUFFER_START(UnityPerMaterial)
                float _LocalMaskCutoff;
                float _UseLocalMask;
            CBUFFER_END

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

                output.positionCS = vertexInput.positionCS;
                output.normalWS = normalInput.normalWS;
                output.localMaskUV = input.uvMask;
                return output;
            }

            half4 DepthNormalsFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 normalWS = normalize(input.normalWS);
                if (_UseLocalMask > 0.5)
                {
                    float localMask = SAMPLE_TEXTURE2D(_LocalMaskTex, sampler_LocalMaskTex, input.localMaskUV).r;
                    clip(localMask - _LocalMaskCutoff);
                }
                return half4(normalWS, 0.0);
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
