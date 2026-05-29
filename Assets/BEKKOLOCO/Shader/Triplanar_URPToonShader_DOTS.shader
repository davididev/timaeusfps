Shader "BEKKOLOCO/Simple_DOTS_Triplanar"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _TextureScale ("Texture Scale", Range(0.1, 10)) = 1
        _Color ("Color", Color) = (1,1,1,1)
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    
    #ifdef UNITY_DOTS_INSTANCING_ENABLED
        #include "Packages/com.unity.rendering.hybrid/ShaderLibrary/UnityDOTSInstancing.hlsl"
    #endif
    
    CBUFFER_START(UnityPerMaterial)
        float4 _MainTex_ST;
        float _TextureScale;
        float4 _Color;
    CBUFFER_END

    TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
    
    // Simple triplanar function
    half4 SampleTriplanar(float3 worldPos, float3 worldNormal)
    {
        float3 absNormal = abs(worldNormal);
        float sum = absNormal.x + absNormal.y + absNormal.z;
        float3 triW = absNormal / sum;

        float2 uvX = worldPos.zy * _TextureScale;
        float2 uvY = worldPos.xz * _TextureScale;
        float2 uvZ = worldPos.xy * _TextureScale;

        half4 colX = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvX);
        half4 colY = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvY);
        half4 colZ = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvZ);
        
        return colX * triW.x + colY * triW.y + colZ * triW.z;
    }
    ENDHLSL
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                #ifdef UNITY_DOTS_INSTANCING_ENABLED
                    float4x4 objectToWorld = unity_DOTSInstanceData[unity_InstanceID].ObjectToWorld;
                    float3 worldPos = mul(objectToWorld, IN.positionOS).xyz;
                    OUT.normalWS = normalize(mul((float3x3)objectToWorld, IN.normalOS));
                #else
                    float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                    OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                #endif

                OUT.positionWS = worldPos;
                OUT.positionCS = TransformWorldToHClip(worldPos);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Simple triplanar sampling
                half4 triplanarColor = SampleTriplanar(IN.positionWS, IN.normalWS);
                
                // Basic lighting
                Light mainLight = GetMainLight();
                float3 lightDir = normalize(mainLight.direction);
                float NdotL = saturate(dot(normalize(IN.normalWS), lightDir));
                
                // Simple toon lighting
                float lightIntensity = step(0.5, NdotL);
                
                half4 finalColor = triplanarColor * _Color;
                finalColor.rgb *= lightIntensity * mainLight.color.rgb;
                finalColor.a = 1;
                
                return finalColor;
            }
            ENDHLSL
        }
    }
    
    FallBack "Universal Render Pipeline/Lit"
}
