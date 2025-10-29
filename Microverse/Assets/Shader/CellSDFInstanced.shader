Shader "Unlit/CellSDFInstanced_Advanced"
{
    Properties{
        // 색
        _CytoplasmInner ("Cytoplasm Inner",  Color) = (0.95,0.85,0.75,1)
        _CytoplasmOuter ("Cytoplasm Outer",  Color) = (0.90,0.70,0.60,1)
        _RimColor       ("Membrane Color",   Color) = (1,0.95,0.85,1)
        _NucleusColor   ("Nucleus Color",    Color) = (0.85,0.4,0.6,1)
        _GlowColor      ("Rim Glow Color",   Color) = (1.0,0.7,0.7,1)

        // 형상
        _RimThickness   ("Rim Thickness",    Float) = 0.14
        _NucleusRatio   ("Nucleus Radius",   Float) = 0.42
        _RimNoiseAmp    ("Rim Noise Amp",    Float) = 0.05
        _RimNoiseFreq   ("Rim Noise Freq",   Float) = 6.0
        _WobbleAmp      ("Wobble Amp",       Float) = 0.035
        _WobbleFreq     ("Wobble Freq",      Float) = 2.4
        _PulseAmp       ("Pulse Amp",        Float) = 0.04
        _PulseFreq      ("Pulse Freq",       Float) = 1.0

        // 소기관
        _OrganelleDensity ("Organelles Density", Float) = 12.0
        _OrganelleSize    ("Organelles Size",    Float) = 0.03
        _OrganelleColor   ("Organelles Color",   Color) = (0.95,0.9,0.85,1)
        _OrganelleSoft    ("Organelles Softness",Float) = 0.5

        // 가짜 SSS/윤광
        _GlowStrength   ("Rim Glow Strength", Float) = 0.35
        _EdgeDarken     ("Edge Darken",       Float) = 0.25
    }
    SubShader{
        Tags{ "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off ZWrite Off

        Pass{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f {
                float4 pos : SV_POSITION;
                float2 xy  : TEXCOORD0;  // -0.5..+0.5
                float2 uv  : TEXCOORD1;  // 0..1 (소기관 해시용)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            fixed4 _CytoplasmInner, _CytoplasmOuter, _RimColor, _NucleusColor, _GlowColor, _OrganelleColor;
            float _RimThickness, _NucleusRatio, _RimNoiseAmp, _RimNoiseFreq;
            float _WobbleAmp, _WobbleFreq, _PulseAmp, _PulseFreq;
            float _OrganelleDensity, _OrganelleSize, _OrganelleSoft;
            float _GlowStrength, _EdgeDarken;

            // per-instance 메인색 (C# MPB.SetVectorArray("_Color", ...))
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color) // species/state 기반 주색
            UNITY_INSTANCING_BUFFER_END(Props)

            // 해시/노이즈 유틸
            float hash11(float n){ return frac(sin(n)*43758.5453123); }
            float hash21(float2 p){ return frac(sin(dot(p,float2(12.9898,78.233)))*43758.5453); }
            float2 hash22(float2 p){ return float2(hash21(p), hash21(p+23.17)); }

            // 부드러운 값 노이즈(간단 value noise)
            float vnoise(float2 p){
                float2 i = floor(p), f = frac(p);
                float a = hash21(i);
                float b = hash21(i+float2(1,0));
                float c = hash21(i+float2(0,1));
                float d = hash21(i+float2(1,1));
                float2 u = f*f*(3.0-2.0*f);
                return lerp(lerp(a,b,u.x), lerp(c,d,u.x), u.y);
            }

            v2f vert (appdata v){
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v,o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.xy  = v.vertex.xy;             // -0.5..+0.5
                o.uv  = v.uv;                    // 0..1
                return o;
            }

            fixed4 frag (v2f i) : SV_Target{
                UNITY_SETUP_INSTANCE_ID(i);

                // per-instance 기본색
                float4 mainCol = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);

                // 정규 반지름 r (0=center, 1=edge 근처)
                float2 p = i.xy;                 // -0.5..+0.5
                float  t = _Time.y;
                float  r = length(p) * 2.0;      // ~0..1

                // 개별 위상: 좌표 기반 해시로 간단 대체 (플랫폼 호환)
                uint iid = asuint(i.xy * 12345.6789);
                float phase = hash11(iid*1.234+7.89);

                // 막 요철 + 맥동 + 흔들림
                float pulse = 1.0 + _PulseAmp * sin(_PulseFreq*t + phase*6.2831); // 크기 맥동
                float wobble = _WobbleAmp * sin(_WobbleFreq*t + phase*6.2831 + r*6.2831);
                float rimNoise = _RimNoiseAmp * (vnoise(p*(_RimNoiseFreq*6.0) + phase*10.0) * 2.0 - 1.0);

                float rimOuter = pulse * (1.0 + wobble + rimNoise);
                float rimInner = rimOuter - _RimThickness;

                // 셀 밖이면 버림
                if (r > rimOuter) discard;

                // 세포질 기본 그라데이션: 중심쪽 밝고 따뜻하게
                float rr = saturate(r / rimOuter);
                float cytograd = smoothstep(0.0, 1.0, rr);
                fixed4 cyt = lerp(_CytoplasmInner, _CytoplasmOuter, cytograd);
                cyt.rgb = lerp(cyt.rgb, mainCol.rgb, 0.35); // per-instance 색과 블렌드

                fixed4 col = cyt;

                // 림: 가장자리로 갈수록 림색 가미 + 가장자리 약간 어둡게(두께감)
                if (r > rimInner){
                    float k = saturate((r - rimInner) / max(1e-5, (rimOuter - rimInner)));
                    col = lerp(_RimColor, col, 1.0 - k);
                    col.rgb = lerp(col.rgb, col.rgb * (1.0 - _EdgeDarken), k);
                }

                // 가짜 SSS/글로우: 가장자리에서 은은한 발광 추가
                {
                    float edge = saturate((r - (rimOuter - _RimThickness*0.6)) / (_RimThickness*0.6 + 1e-5));
                    col.rgb += _GlowColor.rgb * edge * _GlowStrength;
                }

                // 핵: 약간 오프셋 + 노이즈 블렌드
                {
                    float2 nOff = (hash22(i.xy*123.45 + phase*10.0)-0.5)*0.2*_NucleusRatio;
                    float rn = length(p - nOff) * 2.0;
                    float nR = _NucleusRatio * (1.0 + 0.05*sin(t*1.3 + phase*8.0));
                    if (rn <= nR){
                        float edge = smoothstep(nR, nR*0.85, rn);
                        float ntex = vnoise((p - nOff) * 18.0 + phase*50.0);
                        float3 ncol = lerp(_NucleusColor.rgb, _NucleusColor.rgb*1.15, ntex*0.4);
                        col.rgb = lerp(ncol, col.rgb, edge);
                    }
                }

                // 소기관: 랜덤 점/顆粒
                {
                    // 원 안에서만
                    if (r < rimInner){
                        // 타일 좌표를 늘려 여러 점 생성
                        float d = _OrganelleDensity;
                        float2 grid = p * d + phase*20.0;
                        float2 g0 = floor(grid);
                        float2 ft = frac(grid);
                        // 주변 4셀에 대해 점 스프라이트 합성
                        [unroll]
                        for (int yy=0; yy<2; yy++){
                            [unroll]
                            for (int xx=0; xx<2; xx++){
                                float2 cell = g0 + float2(xx,yy);
                                float2 seed = hash22(cell);
                                float2 center = (cell + seed - (p*d)) / d; // 로컬 오프셋
                                float pr = length(p - center) * 2.0; // 점 반경
                                float size = _OrganelleSize * (0.7 + 0.6*seed.x);
                                float soft = _OrganelleSoft;
                                float a = smoothstep(size, size*(1.0-soft), size - pr);
                                col.rgb = lerp(col.rgb, _OrganelleColor.rgb, a * 0.6);
                            }
                        }
                    }
                }

                col.a = 1;
                return col;
            }
            ENDCG
        }
    }
}
