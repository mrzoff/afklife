Shader "AFK Island/WaterWaves"
{
    Properties
    {
        _DeepColor ("Deep Color", Color) = (0.05, 0.22, 0.38, 0.92)
        _ShallowColor ("Shallow Color", Color) = (0.18, 0.55, 0.62, 0.78)
        _FoamColor ("Foam Color", Color) = (0.85, 0.95, 1.0, 0.85)
        _WaveHeight ("Wave Height", Range(0, 0.8)) = 0.18
        _WaveLength ("Wave Length", Range(0.5, 40)) = 8
        _WaveSpeed ("Wave Speed", Range(0.1, 4)) = 1.1
        _RippleScale ("Ripple Scale", Range(0.5, 20)) = 3.5
        _Shininess ("Shininess", Range(8, 256)) = 64
        _FresnelPower ("Fresnel", Range(0.5, 8)) = 3.2
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
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _DeepColor;
                half4 _ShallowColor;
                half4 _FoamColor;
                half _WaveHeight;
                half _WaveLength;
                half _WaveSpeed;
                half _RippleScale;
                half _Shininess;
                half _FresnelPower;
            CBUFFER_END

            // WorldVisualEnvironment: xy = dir, z = strength, w = gust
            float4 _AfkWindParams;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float wave : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float2 WindDir()
            {
                float2 d = _AfkWindParams.xy;
                if (dot(d, d) < 1e-4)
                {
                    d = float2(1, 0.15);
                }
                return normalize(d);
            }

            void SampleWaves(float3 posWS, out float height, out float3 normalWS)
            {
                float2 wind = WindDir();
                float strength = saturate(_AfkWindParams.z);
                float gust = saturate(_AfkWindParams.w);
                float amp = _WaveHeight * (0.35 + strength * 0.9) * (0.75 + gust * 0.55);
                float2 across = float2(-wind.y, wind.x);
                float t = _Time.y * _WaveSpeed * (0.7 + strength * 0.8);

                float k1 = 6.2831853 / max(0.5, _WaveLength);
                float k2 = k1 * 1.7;
                float k3 = k1 * 0.55;

                float p1 = dot(posWS.xz, wind) * k1 + t;
                float p2 = dot(posWS.xz, across) * k2 + t * 1.35;
                float p3 = dot(posWS.xz, wind * 0.6 + across * 0.4) * k3 - t * 0.8;
                float rip = sin(posWS.x * _RippleScale + posWS.z * _RippleScale * 0.7 + t * 2.2);

                float s1 = sin(p1);
                float s2 = sin(p2);
                float s3 = sin(p3);
                height = (s1 * 0.55 + s2 * 0.28 + s3 * 0.12 + rip * 0.05) * amp;

                // Analytic-ish normal from wave slopes.
                float dx = cos(p1) * k1 * wind.x * 0.55
                         + cos(p2) * k2 * across.x * 0.28
                         + cos(p3) * k3 * (wind.x * 0.6 + across.x * 0.4) * 0.12
                         + cos(posWS.x * _RippleScale + posWS.z * _RippleScale * 0.7 + t * 2.2) * _RippleScale * 0.05;
                float dz = cos(p1) * k1 * wind.y * 0.55
                         + cos(p2) * k2 * across.y * 0.28
                         + cos(p3) * k3 * (wind.y * 0.6 + across.y * 0.4) * 0.12
                         + cos(posWS.x * _RippleScale + posWS.z * _RippleScale * 0.7 + t * 2.2) * _RippleScale * 0.7 * 0.05;
                dx *= amp;
                dz *= amp;
                normalWS = normalize(float3(-dx, 1.0, -dz));
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float height;
                float3 nWS;
                SampleWaves(posWS, height, nWS);
                posWS.y += height;

                output.positionWS = posWS;
                output.normalWS = nWS;
                output.wave = height;
                output.positionCS = TransformWorldToHClip(posWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float strength = saturate(_AfkWindParams.z);
                float gust = saturate(_AfkWindParams.w);
                float3 n = normalize(input.normalWS);
                float3 viewDir = GetWorldSpaceNormalizeViewDir(input.positionWS);
                Light mainLight = GetMainLight();

                float crest = saturate(input.wave / max(0.001, _WaveHeight) * 0.5 + 0.5);
                half3 albedo = lerp(_DeepColor.rgb, _ShallowColor.rgb, crest);
                float foamMask = saturate((crest - 0.72) * 3.5) * (0.25 + strength * 0.9 + gust * 0.4);
                albedo = lerp(albedo, _FoamColor.rgb, foamMask);

                half ndotl = saturate(dot(n, mainLight.direction));
                half3 lighting = mainLight.color * (0.35 + ndotl * 0.65) + half3(0.12, 0.16, 0.2);

                float3 halfDir = normalize(mainLight.direction + viewDir);
                float spec = pow(saturate(dot(n, halfDir)), _Shininess) * (0.25 + strength * 0.55);
                albedo += mainLight.color * spec * 0.65;

                float fresnel = pow(1.0 - saturate(dot(n, viewDir)), _FresnelPower);
                albedo = lerp(albedo, _ShallowColor.rgb * 1.15, fresnel * 0.35);

                half alpha = lerp(_DeepColor.a, _ShallowColor.a, crest);
                alpha = saturate(alpha + foamMask * 0.15 + fresnel * 0.12);
                return half4(albedo * lighting, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
