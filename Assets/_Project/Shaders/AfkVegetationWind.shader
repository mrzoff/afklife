Shader "AFK Island/VegetationWind"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (0.35, 0.55, 0.28, 1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.35
        _SwayAmount ("Sway Amount", Range(0, 1.5)) = 0.35
        _SwaySpeed ("Sway Speed", Range(0.1, 4)) = 1.1
        _SwayHeightStart ("Sway From Height", Range(0, 2)) = 0.15
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            // Stage 11E-4: both variants stay in the player. Applicator enables clip only for alpha-cutout sources.
            #pragma multi_compile_local _ _ALPHATEST_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _SwayAmount;
                half _SwaySpeed;
                half _SwayHeightStart;
            CBUFFER_END

            // Set by WorldVisualEnvironment (one writer for the whole island).
            float4 _AfkWindParams; // xy = dir, z = strength 0..1, w = gust 0..1

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 posOS = input.positionOS.xyz;
                float heightMask = saturate((posOS.y - _SwayHeightStart) / max(0.001, 1.5 - _SwayHeightStart));
                float2 windDir = _AfkWindParams.xy;
                if (dot(windDir, windDir) < 1e-4)
                {
                    windDir = float2(1, 0);
                }
                windDir = normalize(windDir);
                float strength = saturate(_AfkWindParams.z);
                float gust = saturate(_AfkWindParams.w);
                float t = _Time.y * _SwaySpeed;
                float wave = sin(t + posOS.x * 1.7 + posOS.z * 1.3) * 0.65
                           + sin(t * 1.7 + posOS.z * 2.1) * 0.35;
                float sway = wave * _SwayAmount * heightMask * (0.35 + strength * 0.65) * (0.7 + gust * 0.9);
                posOS.xz += windDir * sway;

                VertexPositionInputs posInputs = GetVertexPositionInputs(posOS);
                VertexNormalInputs nInputs = GetVertexNormalInputs(input.normalOS);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = nInputs.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                #if defined(_ALPHATEST_ON)
                clip(albedo.a - _Cutoff);
                #endif

                Light mainLight = GetMainLight();
                half ndotl = saturate(dot(normalize(input.normalWS), mainLight.direction));
                half3 lighting = mainLight.color * (0.35 + ndotl * 0.65) + half3(0.15, 0.18, 0.14);
                return half4(albedo.rgb * lighting, albedo.a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
