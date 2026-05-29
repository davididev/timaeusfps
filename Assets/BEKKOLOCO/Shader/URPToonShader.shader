Shader "BEKKOLOCO/URPToonShader"
{
    Properties
    {
        _MainTex                ("Albedo Texture", 2D)                         = "white" {}
        _HighlightColor         ("Highlight Color", Color)                     = (1,1,1,1)
        _ShadowColor            ("Shadow Color",   Color)                      = (0.5,0.5,0.5,1)

        [Toggle]_UseShadowTexture("Use Shadow Texture", Float)                = 0
        _ShadowTex              ("Shadow Texture", 2D)                         = "gray"  {}

        _NormalMap              ("Normal Map",     2D)                         = "bump"  {}
        _NormalStrength         ("Normal Strength", Range(0,2))               = 1

        [HDR]_EmissionColor     ("Emission Color", Color)                      = (0,0,0,1)
        _EmissionTex            ("Emission Texture", 2D)                       = "black" {}
        _EmissionStrength       ("Emission Strength", Range(0,2))              = 0

        _RampThreshold          ("Ramp Threshold",  Range(0,1))                = 0.5
        _RampSmoothing          ("Ramp Smoothing",  Range(0.001,1))            = 0.1
        _SpecularPower          ("Specular Power",  Range(0,100))              = 10
        _SpecularStrength       ("Specular Strength", Range(0,1))              = 0.5

        _RimColor               ("Rim Color",      Color)                      = (1,1,1,1)
        _RimPower               ("Rim Power",      Range(0,10))                = 3
        _RimStrength            ("Rim Strength",   Range(0,1))                 = 0.5

        _Cutoff                 ("Alpha Cutoff",   Range(0,1))                 = 0.5
        [Toggle]_AlphaClip      ("Alpha Clipping", Float)                      = 0
        _LocalMaskTex           ("Local Mask Texture", 2D)                     = "white" {}
        _LocalMaskCutoff        ("Local Mask Cutoff", Range(0,1))              = 0.5
        [Toggle]_UseLocalMask   ("Use Local Mask", Float)                      = 0

        // ★ new property
        _CookieShadowStrength   ("Cookie Shadow Strength", Range(0,1))         = 1
    }

    SubShader
    {
        Tags { "RenderType"   = "TransparentCutout"
               "RenderPipeline" = "UniversalPipeline"
               "Queue"        = "AlphaTest" }

        // ──────────────────────────────────────────────────────
        //  FORWARD-LIT PASS
        // ──────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            // ★ compile cookie variants
            #pragma multi_compile_fragment _ _LIGHT_COOKIES

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LightCookie/LightCookieInput.hlsl"  // ★
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_MainTex);      SAMPLER(sampler_MainTex);
            TEXTURE2D(_NormalMap);    SAMPLER(sampler_NormalMap);
            TEXTURE2D(_EmissionTex);  SAMPLER(sampler_EmissionTex);
            TEXTURE2D(_ShadowTex);    SAMPLER(sampler_ShadowTex);
            TEXTURE2D(_LocalMaskTex); SAMPLER(sampler_LocalMaskTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST, _NormalMap_ST, _EmissionTex_ST, _ShadowTex_ST;
                float4 _HighlightColor, _ShadowColor;
                float  _UseShadowTexture, _NormalStrength;
                float4 _EmissionColor;  float _EmissionStrength;
                float  _RampThreshold,  _RampSmoothing;
                float  _SpecularPower,  _SpecularStrength;
                float4 _RimColor;       float _RimPower, _RimStrength;
                float  _Cutoff, _AlphaClip;
                float  _LocalMaskCutoff, _UseLocalMask;

                // ★ new uniform
                float  _CookieShadowStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 uvMask     : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 viewDirWS   : TEXCOORD2;
                float3 positionWS  : TEXCOORD3;
                float3 tangentWS   : TEXCOORD4;
                float3 bitangentWS : TEXCOORD5;
                float4 shadowCoord : TEXCOORD6;
                float2 maskUV      : TEXCOORD7;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // vertex
            Varyings vert (Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS  = TransformWorldToHClip(OUT.positionWS);

                OUT.uv          = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.maskUV      = IN.uvMask;
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                OUT.tangentWS   = TransformObjectToWorldDir(IN.tangentOS.xyz);
                OUT.bitangentWS = cross(OUT.normalWS, OUT.tangentWS) * IN.tangentOS.w;

                OUT.viewDirWS   = GetWorldSpaceNormalizeViewDir(OUT.positionWS);
                OUT.shadowCoord = TransformWorldToShadowCoord(OUT.positionWS);

                return OUT;
            }

            // fragment
            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                //----------------------------------------------------------
                // Base textures & alpha-clip
                //----------------------------------------------------------
                half4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half localMask = 1.0h;
                if (_UseLocalMask > 0.5)
                    localMask = SAMPLE_TEXTURE2D(_LocalMaskTex, sampler_LocalMaskTex, IN.maskUV).r;
                if (_AlphaClip > 0.5 || _UseLocalMask > 0.5)
                    clip(albedo.a * localMask - max(_Cutoff, _LocalMaskCutoff));

                //----------------------------------------------------------
                // Normal mapping
                //----------------------------------------------------------
                half3 nrmSample = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, IN.uv));
                nrmSample       = lerp(half3(0,0,1), nrmSample, _NormalStrength);
                float3x3 TBN    = float3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS);
                half3 normalWS  = normalize(mul(nrmSample, TBN));

                //----------------------------------------------------------
                // Lighting & shadows
                //----------------------------------------------------------
                Light mainLight = GetMainLight(IN.shadowCoord);
                half3 L         = normalize(mainLight.direction);
                half3 V         = normalize(IN.viewDirWS);
                half3 H         = normalize(L + V);

                half  NdotL     = saturate(dot(normalWS, L));
                half  ramp      = smoothstep(_RampThreshold - _RampSmoothing,
                                             _RampThreshold + _RampSmoothing, NdotL);

                half shadowAtt  = mainLight.shadowAttenuation;

                // ★ cookie shadow modulation
            #ifdef _LIGHT_COOKIES
                half cookieAtt  = SampleMainLightCookie(IN.positionWS).r;
                half cookieMix  = lerp(1.0h, cookieAtt, _CookieShadowStrength);
                shadowAtt      *= cookieMix;
            #endif

                ramp *= shadowAtt;

                //----------------------------------------------------------
                // Specular, rim, emission
                //----------------------------------------------------------
                half NdotH      = saturate(dot(normalWS, H));
                half spec       = pow(NdotH, _SpecularPower) * _SpecularStrength;

                half NdotV      = 1.0 - saturate(dot(normalWS, V));
                half rim        = pow(NdotV, _RimPower) * _RimStrength;

                half3 emissionTx= SAMPLE_TEXTURE2D(_EmissionTex, sampler_EmissionTex, IN.uv).rgb;
                half3 emission  = _EmissionColor.rgb * emissionTx * _EmissionStrength;

                //----------------------------------------------------------
                // Highlight / shadow choice
                //----------------------------------------------------------
                half4 shadowSrc = _UseShadowTexture
                                    ? SAMPLE_TEXTURE2D(_ShadowTex, sampler_ShadowTex, IN.uv)
                                    : _ShadowColor;

                half4 baseCol   = lerp(shadowSrc, _HighlightColor, ramp) * albedo;

                //----------------------------------------------------------
                // Compose final colour
                //----------------------------------------------------------
                half4 litCol    = baseCol + half4(spec.xxx, 0) * half4(mainLight.color, 1);
                half4 finalCol  = litCol + (_RimColor * rim) + half4(emission, 0);

                finalCol.a      = albedo.a * localMask;
                return finalCol;
            }
            ENDHLSL
        }

        // ──────────────────────────────────────────────────────
        //  SHADOW-CASTER PASS  (unchanged)
        // ──────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest  LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex   ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x
            #pragma target 2.0
            #pragma multi_compile_shadowcaster

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            TEXTURE2D(_LocalMaskTex); SAMPLER(sampler_LocalMaskTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float  _Cutoff, _AlphaClip, _LocalMaskCutoff, _UseLocalMask;
                float4 _NormalMap_ST, _EmissionTex_ST, _ShadowTex_ST;
                float4 _HighlightColor, _ShadowColor;
                float  _UseShadowTexture, _NormalStrength;
                float4 _EmissionColor; float _EmissionStrength;
                float  _RampThreshold, _RampSmoothing;
                float  _SpecularPower, _SpecularStrength;
                float4 _RimColor; float _RimPower, _RimStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
                float2 uvMask     : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv         : TEXCOORD0;
                float2 maskUV     : TEXCOORD1;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 GetShadowPositionHClip(Attributes IN)
            {
                float3 posWS  = TransformObjectToWorld(IN.positionOS.xyz);
                float3 nrmWS  = TransformObjectToWorldNormal(IN.normalOS);

            #if SHADOWS_SCREEN
                return TransformWorldToHClip(ApplyShadowBias(posWS, nrmWS, _MainLightPosition.xyz));
            #else
                Light mainLight  = GetMainLight();
                float4 posCS     = TransformWorldToHClip(ApplyShadowBias(posWS, nrmWS, mainLight.direction));
            #if UNITY_REVERSED_Z
                posCS.z = min(posCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                posCS.z = max(posCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return posCS;
            #endif
            }

            Varyings ShadowPassVertex(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.uv         = TRANSFORM_TEX(IN.texcoord, _MainTex);
                OUT.maskUV     = IN.uvMask;
                OUT.positionCS = GetShadowPositionHClip(IN);
                return OUT;
            }

            half4 ShadowPassFragment(Varyings IN) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half localMask = 1.0h;
                if (_UseLocalMask > 0.5)
                    localMask = SAMPLE_TEXTURE2D(_LocalMaskTex, sampler_LocalMaskTex, IN.maskUV).r;
                if (_AlphaClip > 0.5 || _UseLocalMask > 0.5)
                    clip(albedo.a * localMask - max(_Cutoff, _LocalMaskCutoff));
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Lit"
}
