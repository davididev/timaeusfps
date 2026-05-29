Shader "BEKKOLOCO/Triplanar_URPToonShader"
{
    Properties
    {
        _MainTex ("Albedo Texture", 2D) = "white" {}
        _TextureScale ("Texture Scale", Range(0.1, 10)) = 1

        _HighlightColor ("Highlight Color", Color) = (1,1,1,1)
        _ShadowColor ("Shadow Color", Color) = (0.5,0.5,0.5,1)
        [Toggle] _UseShadowTexture ("Use Shadow Texture", Float) = 0
        _ShadowTex ("Shadow Texture", 2D) = "gray" {}

        _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1

        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        _EmissionTex ("Emission Texture", 2D) = "black" {}
        _EmissionStrength ("Emission Strength", Range(0, 2)) = 0

        _RampThreshold ("Ramp Threshold", Range(0, 1)) = 0.5
        _RampSmoothing ("Ramp Smoothing", Range(0.001, 1)) = 0.1
        _SpecularPower ("Specular Power", Range(0, 100)) = 10
        _SpecularStrength ("Specular Strength", Range(0, 1)) = 0.5

        _RimColor ("Rim Color", Color) = (1,1,1,1)
        _RimPower ("Rim Power", Range(0, 10)) = 3
        _RimStrength ("Rim Strength", Range(0, 1)) = 0.5

        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [Toggle] _AlphaClip ("Alpha Clipping", Float) = 0

        [Toggle] _LockTriplanar ("Lock Triplanar Projection", Float) = 0
    }
    
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "RenderPipeline"="UniversalPipeline" "Queue"="AlphaTest" }
        LOD 100
        
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _LIGHT_COOKIES  
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            
            // Texture and sampler declarations
            TEXTURE2D(_MainTex);            SAMPLER(sampler_MainTex);
            TEXTURE2D(_NormalMap);          SAMPLER(sampler_NormalMap);
            TEXTURE2D(_EmissionTex);        SAMPLER(sampler_EmissionTex);
            TEXTURE2D(_ShadowTex);          SAMPLER(sampler_ShadowTex);
            
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _TextureScale;

                float4 _HighlightColor;
                float4 _ShadowColor;
                float _UseShadowTexture;
                float4 _ShadowTex_ST;

                float4 _NormalMap_ST;
                float _NormalStrength;

                float4 _EmissionColor;
                float4 _EmissionTex_ST;
                float _EmissionStrength;

                float _RampThreshold;
                float _RampSmoothing;
                float _SpecularPower;
                float _SpecularStrength;

                float4 _RimColor;
                float _RimPower;
                float _RimStrength;

                float _Cutoff;
                float _AlphaClip;

                float _LockTriplanar;
                float4x4 _LockedWorldMatrix;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 objectPosWS : TEXCOORD7; // Position du centre de l'objet pour la technique sticky
                float3 lockedPos  : TEXCOORD8;
                float3 lockedNormal : TEXCOORD9;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float3 tangentWS  : TEXCOORD3;
                float3 bitangentWS: TEXCOORD4;
                float3 viewDirWS  : TEXCOORD5;
                float4 shadowCoord: TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionWS = worldPos;
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.shadowCoord = TransformWorldToShadowCoord(worldPos);

                // Position du centre de l'objet dans l'espace monde (pour technique sticky)
                OUT.objectPosWS = TransformObjectToWorld(float3(0, 0, 0));

                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.tangentWS = TransformObjectToWorldDir(IN.tangentOS.xyz);
                OUT.bitangentWS = cross(OUT.normalWS, OUT.tangentWS) * IN.tangentOS.w;
                
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                
                OUT.viewDirWS = normalize(_WorldSpaceCameraPos - worldPos);

                // Compute locked position and normal (garde la logique originale pour compatibility)
                OUT.lockedPos = mul(_LockedWorldMatrix, float4(IN.positionOS.xyz, 1.0)).xyz;
                OUT.lockedNormal = normalize(mul((float3x3)_LockedWorldMatrix, IN.normalOS));

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Choose triplanar position and normal based on lock mode
                float3 triPos;
                float3 triNormal;
                
                if (_LockTriplanar > 0.5)
                {
                    // MODE LOCKED = TECHNIQUE STICKY : position relative à l'objet
                    triPos = IN.positionWS - IN.objectPosWS;
                    triNormal = IN.normalWS;
                }
                else
                {
                    // MODE NORMAL : position world absolue (texture glisse)
                    triPos = IN.positionWS;
                    triNormal = IN.normalWS;
                }

                // Triplanar albedo sampling
                float3 absNormal = abs(triNormal);
                float sum = absNormal.x + absNormal.y + absNormal.z;
                float3 triW = absNormal / sum;

                float2 uvX = triPos.zy * _TextureScale;
                float2 uvY = triPos.xz * _TextureScale;
                float2 uvZ = triPos.xy * _TextureScale;

                half4 colX = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvX);
                half4 colY = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvY);
                half4 colZ = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvZ);
                half4 albedo = colX * triW.x + colY * triW.y + colZ * triW.z;
                
                // Alpha clipping
                if (_AlphaClip > 0.5)
                {
                    clip(albedo.a - _Cutoff);
                }
                
                // Triplanar normal mapping
                half3 normalMapX = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvX));
                half3 normalMapY = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvY));
                half3 normalMapZ = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uvZ));
                half3 blendedNormal = normalMapX * triW.x + normalMapY * triW.y + normalMapZ * triW.z;
                blendedNormal = lerp(half3(0, 0, 1), blendedNormal, _NormalStrength);
                float3x3 TBN = float3x3(normalize(IN.tangentWS),
                                        normalize(IN.bitangentWS),
                                        normalize(IN.normalWS));
                half3 normalWS = normalize(mul(blendedNormal, TBN));
                
                // Lighting & shadows
                Light mainLight = GetMainLight(IN.shadowCoord);
                float3 lightDir = normalize(mainLight.direction);
                float NdotL = saturate(dot(normalWS, lightDir));
                float lightIntensity = smoothstep(_RampThreshold - _RampSmoothing,
                                                  _RampThreshold + _RampSmoothing,
                                                  NdotL);
                lightIntensity *= mainLight.shadowAttenuation;

                #ifdef _LIGHT_COOKIES
                    float cookieAtt = SampleMainLightCookie(IN.positionWS).r;
                    lightIntensity *= cookieAtt;
                #endif

                // Specular
                float3 viewDir = normalize(IN.viewDirWS);
                float3 halfDir = normalize(lightDir + viewDir);
                float NdotH = saturate(dot(normalWS, halfDir));
                float specular = pow(NdotH, _SpecularPower) * _SpecularStrength;
                
                // Rim lighting
                float NdotV = 1.0 - saturate(dot(normalWS, viewDir));
                float rimIntensity = pow(NdotV, _RimPower) * _RimStrength;
                
                // Emission
                half4 emissionTex = SAMPLE_TEXTURE2D(_EmissionTex, sampler_EmissionTex, IN.uv);
                half3 emission = _EmissionColor.rgb * emissionTex.rgb * _EmissionStrength;
                
                // Shadow texture or color
                half4 shadowTex = SAMPLE_TEXTURE2D(_ShadowTex, sampler_ShadowTex, IN.uv);
                half4 shadowSource = _UseShadowTexture > 0.5 ? shadowTex : _ShadowColor;
                
                // Final color
                half4 baseColor = lerp(shadowSource, _HighlightColor, lightIntensity) * albedo;
                half4 litColor = baseColor + half4(specular, specular, specular, 0);
                half4 finalColor = litColor + half4(_RimColor.rgb * rimIntensity, 0) + half4(emission, 0);
                finalColor.a = albedo.a;
                
                return finalColor;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _TextureScale;
                float _Cutoff;
                float _AlphaClip;
                float _LockTriplanar;
                float4x4 _LockedWorldMatrix;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

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
                float3 positionWS : TEXCOORD0;
                float3 objectPosWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float2 uv         : TEXCOORD3;
                float3 lockedPos  : TEXCOORD4;
                float3 lockedNormal : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float3 _LightDirection;

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionWS = worldPos;
                OUT.objectPosWS = TransformObjectToWorld(float3(0, 0, 0));
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(worldPos, OUT.normalWS, _LightDirection));
                OUT.positionCS = positionCS;
                OUT.uv = IN.uv;

                // Compute locked position and normal
                OUT.lockedPos = mul(_LockedWorldMatrix, float4(IN.positionOS.xyz, 1.0)).xyz;
                OUT.lockedNormal = normalize(mul((float3x3)_LockedWorldMatrix, IN.normalOS));

                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target
            {
                // Même logique que dans le fragment shader principal
                float3 triPos;
                float3 triNormal;
                
                if (_LockTriplanar > 0.5)
                {
                    // MODE LOCKED = TECHNIQUE STICKY
                    triPos = IN.positionWS - IN.objectPosWS;
                    triNormal = IN.normalWS;
                }
                else
                {
                    // MODE NORMAL : position absolue
                    triPos = IN.positionWS;
                    triNormal = IN.normalWS;
                }

                // Triplanar sampling for alpha
                float3 absNormal = abs(triNormal);
                float sum = absNormal.x + absNormal.y + absNormal.z;
                float3 triW = absNormal / sum;

                float2 uvX = triPos.zy * _TextureScale;
                float2 uvY = triPos.xz * _TextureScale;
                float2 uvZ = triPos.xy * _TextureScale;

                half4 colX = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvX);
                half4 colY = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvY);
                half4 colZ = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvZ);
                half4 albedo = colX * triW.x + colY * triW.y + colZ * triW.z;

                if (_AlphaClip > 0.5)
                {
                    clip(albedo.a - _Cutoff);
                }
                return 0;
            }
            ENDHLSL
        }

        Pass {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex DN_vert
            #pragma fragment DN_frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DN_vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                float3 worldPos   = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS    = TransformWorldToHClip(worldPos);
                OUT.normalWS      = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 DN_frag (Varyings IN) : SV_Target
            {
                float3 normalVS = normalize(TransformWorldToViewDir(IN.normalWS));
                float depth = LinearEyeDepth(IN.positionCS.z, _ZBufferParams);
                return half4(normalVS * 0.5 + 0.5, depth);
            }
            ENDHLSL
        }

    }
    
    FallBack "Universal Render Pipeline/Lit"
}