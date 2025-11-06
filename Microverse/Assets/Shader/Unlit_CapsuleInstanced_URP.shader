Shader "Unlit/CapsuleInstanced_URP"
{
    Properties
    {
        // (Optional) shows up in the inspector, but color is driven per-instance.
        _BaseColor ("Color (instanced)", Color) = (0.2, 0.9, 0.8, 1)
        _Softness  ("Edge Softness", Range(0,0.25)) = 0.06
        _Glow      ("Edge Glow", Range(0,1)) = 0.25
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "RenderType"="Transparent"
            "IgnoreProjector"="True"
            "RenderPipeline"="UniversalRenderPipeline"
        }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata
            {
                float3 positionOS : POSITION;     // quad vertices in [-0.5..0.5]
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 positionHCS : SV_POSITION;
                float2 localXY     : TEXCOORD0;  // quad local XY
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // -------- Material constants (SRP Batcher friendly) --------
            CBUFFER_START(UnityPerMaterial)
                half  _Softness;
                half  _Glow;
            CBUFFER_END

            // -------- Per-instance properties --------
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(half4, _BaseColor) // per instance color
            UNITY_INSTANCING_BUFFER_END(Props)

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                // Using the object→clip transform (URP)
                o.positionHCS = TransformObjectToHClip(v.positionOS);
                // We rely on the mesh being a unit quad scaled in C# using TRS
                o.localXY = v.positionOS.xy;
                return o;
            }

            // SDF of a capsule aligned to +X axis, centered at origin, segment from -a..+b, radius r
            float sdCapsule(float2 p, float a, float b, float r)
            {
                float2 pa = p - float2(-a, 0.0);
                float2 ba = float2(a + b, 0.0);
                float  h  = saturate(dot(pa, ba) / dot(ba, ba));
                return length(pa - ba * h) - r;
            }

            half4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                // Per-instance color (set via MPB.SetVectorArray("_BaseColor", …))
                half4 col = UNITY_ACCESS_INSTANCED_PROP(Props, _BaseColor);

                // Our quad is scaled in C# via TRS:
                //   scale.x = capsule length, scale.y = thickness (diameter)
                // In the shader we assume a unit capsule in local quad space:
                //   segment half-lengths a=b=0.5, radius r=0.5 (fits the unit quad)
                float d = sdCapsule(i.localXY, 0.5, 0.5, 0.5);

                // Soft alpha edge
                half alpha = smoothstep(_Softness, 0.0h, -d);

                // Optional rim glow (inside the edge a bit)
                half rim = saturate(1.0h - saturate((-d) / max(1e-4h, _Softness * 2.0h)));
                col.rgb += rim * _Glow * 0.35h;

                col.a *= alpha;
                clip(col.a - 0.001h);
                return col;
            }
            ENDHLSL
        }
    }
}
