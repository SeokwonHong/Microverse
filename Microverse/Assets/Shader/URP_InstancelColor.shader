Shader "Universal Render Pipeline/Unlit Instanced Circle"
{
    Properties
    {
        _BaseColor ("Base Color (fallback)", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalRenderPipeline"
                "Queue"="Transparent"
                "RenderType"="Transparent" }

        Pass
        {
            Name "ForwardUnlit"
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 col : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float4 posWS = float4(TransformObjectToWorld(IN.positionOS), 1.0);
                OUT.positionHCS = TransformWorldToHClip(posWS.xyz);
                OUT.uv = IN.uv;
                OUT.col = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                // UV 중심(0.5, 0.5) 기준으로 반지름 계산
                float2 c = IN.uv - 0.5;
                float dist = length(c);

                // 원의 경계 부드럽게: 반지름 0.5 기준, 경계 두께 0.02
                float alpha = smoothstep(0.5, 0.48, dist);

                return half4(IN.col.rgb, IN.col.a * alpha);
            }
            ENDHLSL
        }
    }
}
