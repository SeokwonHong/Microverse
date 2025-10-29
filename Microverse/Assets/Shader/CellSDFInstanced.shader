Shader "Unlit/CellSDFInstanced_Advanced_Dent_Osc"
{
    Properties{
        // 색
        _CytoplasmInner ("Cytoplasm Inner",  Color) = (0.95,0.85,0.75,1)
        _CytoplasmOuter ("Cytoplasm Outer",  Color) = (0.90,0.70,0.60,1)
        _RimColor       ("Membrane Color",   Color) = (1,0.95,0.85,1)
        _NucleusColor   ("Nucleus Color",    Color) = (0.85,0.4,0.6,1)
        _GlowColor      ("Rim Glow Color",   Color) = (1.0,0.7,0.7,1)

        // 형상/노이즈
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

        // ★ 덴트(국소 눌림) 제어
        _DentSharpness  ("Dent Sharpness",    Range(1,32)) = 8
        _DentStrength   ("Dent Strength",     Range(0,1)) = 1
        _DentMinOuter   ("Min Rim Outer",     Range(0.5,1)) = 0.7

        // ★ 덴트 오실레이션(타이밍 차이)
        _DentPulseFreq  ("Dent Pulse Freq",   Range(0,8)) = 2.0
        _DentOscMix     ("Dent Osc Mix",      Range(0,1)) = 0.6  // 0=정적, 1=완전 진동
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
                float2 xy  : TEXCOORD0;  // -0.5..+0.5 (쿼드 로컬)
                float2 uv  : TEXCOORD1;  // 0..1
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // ===== 유니폼 =====
            fixed4 _CytoplasmInner, _CytoplasmOuter, _RimColor, _NucleusColor, _GlowColor, _OrganelleColor;
            float _RimThickness, _NucleusRatio, _RimNoiseAmp, _RimNoiseFreq;
            float _WobbleAmp, _WobbleFreq, _PulseAmp, _PulseFreq;
            float _OrganelleDensity, _OrganelleSize, _OrganelleSoft;
            float _GlowStrength, _EdgeDarken;
            float _DentSharpness, _DentStrength, _DentMinOuter;
            float _DentPulseFreq, _DentOscMix;

            // ===== per-instance (MPB에서 세팅) =====
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)    // 메인 색
                UNITY_DEFINE_INSTANCED_PROP(float,  _Phase)    // 0..1 워블/덴트 위상
                UNITY_DEFINE_INSTANCED_PROP(float4, _DentDir0) // XY 사용
                UNITY_DEFINE_INSTANCED_PROP(float4, _DentDir1)
                UNITY_DEFINE_INSTANCED_PROP(float4, _DentDir2)
                UNITY_DEFINE_INSTANCED_PROP(float4, _DentDir3)
                UNITY_DEFINE_INSTANCED_PROP(float,  _DentAmp0) // 0..1
                UNITY_DEFINE_INSTANCED_PROP(float,  _DentAmp1)
                UNITY_DEFINE_INSTANCED_PROP(float,  _DentAmp2)
                UNITY_DEFINE_INSTANCED_PROP(float,  _DentAmp3)
                 UNITY_DEFINE_INSTANCED_PROP(float4, _PhaseVec)
            UNITY_INSTANCING_BUFFER_END(Props)

            // ===== 유틸 노이즈 =====
            float hash21(float2 p){ return frac(sin(dot(p,float2(12.9898,78.233)))*43758.5453); }
            float2 hash22(float2 p){ return float2(hash21(p), hash21(p+23.17)); }
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
                o.xy  = v.vertex.xy; // -0.5..+0.5
                o.uv  = v.uv;
                return o;
            }

            // 덴트 가중치
            float dentContribution(float2 n, float2 d, float amp, float sharp)
            {
                float w = max(0.0, dot(n, normalize(d)));
                // 샤프하게 국소화 (pow는 비용 있으니 필요시 정수제곱 근사로 교체 가능)
                w = pow(w, sharp);
                return amp * w;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                // per-instance
                float4 mainCol = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float phase = UNITY_ACCESS_INSTANCED_PROP(Props, _PhaseVec).x;

                float2 p = i.xy;         // 로컬
                float  t = _Time.y;
                float  r = length(p) * 2.0; // 0(center) ~ 1(edge 근처)

                // === 기본 동적 반경(워블/맥동/림 노이즈) ===
                float TAU = 6.2831853;
                float pulse    = 1.0 + _PulseAmp   * sin(_PulseFreq * t + phase * TAU);
                float wobble   =       _WobbleAmp  * sin(_WobbleFreq* t + phase * TAU + r*TAU);
                float rimNoise =       _RimNoiseAmp * (vnoise(p*(_RimNoiseFreq*6.0) + phase*10.0) * 2.0 - 1.0);

                float rimOuter = pulse * (1.0 + wobble + rimNoise);

                // === 덴트(국소 눌림): 경계 방향별로 rimOuter를 줄이기 ===
                {
                    float2 n = normalize(p + 1e-8); // 경계 법선 근사

                    // 인스턴스별 덴트 입력
                    float2 d0 = UNITY_ACCESS_INSTANCED_PROP(Props, _DentDir0).xy;
                    float2 d1 = UNITY_ACCESS_INSTANCED_PROP(Props, _DentDir1).xy;
                    float2 d2 = UNITY_ACCESS_INSTANCED_PROP(Props, _DentDir2).xy;
                    float2 d3 = UNITY_ACCESS_INSTANCED_PROP(Props, _DentDir3).xy;
                    float a0  = UNITY_ACCESS_INSTANCED_PROP(Props, _DentAmp0);
                    float a1  = UNITY_ACCESS_INSTANCED_PROP(Props, _DentAmp1);
                    float a2  = UNITY_ACCESS_INSTANCED_PROP(Props, _DentAmp2);
                    float a3  = UNITY_ACCESS_INSTANCED_PROP(Props, _DentAmp3);

                    // ★ 덴트 오실레이션(세포마다/덴트마다 위상 다르게)
                    float phase0 = phase + 0.13;
                    float phase1 = phase + 0.37;
                    float phase2 = phase + 0.59;
                    float phase3 = phase + 0.83;

                    float osc0 = 0.5 + 0.5 * sin(_DentPulseFreq * t + phase0 * TAU);
                    float osc1 = 0.5 + 0.5 * sin(_DentPulseFreq * t + phase1 * TAU + 1.2);
                    float osc2 = 0.5 + 0.5 * sin(_DentPulseFreq * t + phase2 * TAU + 2.4);
                    float osc3 = 0.5 + 0.5 * sin(_DentPulseFreq * t + phase3 * TAU + 3.6);

                    // 믹스: 0이면 원래 amp, 1이면 진동 최대 반영
                    a0 = lerp(a0, a0 * osc0, _DentOscMix);
                    a1 = lerp(a1, a1 * osc1, _DentOscMix);
                    a2 = lerp(a2, a2 * osc2, _DentOscMix);
                    a3 = lerp(a3, a3 * osc3, _DentOscMix);

                    // 덴트 합산
                    float dent = 0.0;
                    dent += dentContribution(n, d0, a0, _DentSharpness);
                    dent += dentContribution(n, d1, a1, _DentSharpness);
                    dent += dentContribution(n, d2, a2, _DentSharpness);
                    dent += dentContribution(n, d3, a3, _DentSharpness);
                    dent = saturate(dent * _DentStrength);

                    // rim 축소 (하한 보장)
                    rimOuter = max(_DentMinOuter, rimOuter * (1.0 - dent));
                }

                // 내부 경계
                float rimInner = rimOuter - _RimThickness;

                // 실루엣 컷아웃
                if (r > rimOuter) discard;

                // === 세포질 그라데이션 & 색 ===
                float rr = saturate(r / rimOuter);
                float cytograd = smoothstep(0.0, 1.0, rr);
                fixed4 colCyt = lerp(_CytoplasmInner, _CytoplasmOuter, cytograd);
                colCyt.rgb = lerp(colCyt.rgb, mainCol.rgb, 0.35);
                fixed4 col = colCyt;

                // 림 색/두께감
                if (r > rimInner){
                    float k = saturate((r - rimInner) / max(1e-5, (rimOuter - rimInner)));
                    col = lerp(_RimColor, col, 1.0 - k);
                    col.rgb = lerp(col.rgb, col.rgb * (1.0 - _EdgeDarken), k);
                }

                // 가장자리 글로우
                {
                    float edge = saturate((r - (rimOuter - _RimThickness*0.6)) / (_RimThickness*0.6 + 1e-5));
                    col.rgb += _GlowColor.rgb * edge * _GlowStrength;
                }

                // 핵
                {
                    float2 nOff = (hash22(i.xy*123.45 + phase*10.0)-0.5)*0.2*_NucleusRatio;
                    float rn = length(p - nOff) * 2.0;
                    float nR = _NucleusRatio * (1.0 + 0.05*sin(t*1.3 + phase*8.0));
                    if (rn <= nR){
                        float edgeN = smoothstep(nR, nR*0.85, rn);
                        float ntex  = vnoise((p - nOff) * 18.0 + phase*50.0);
                        float3 ncol = lerp(_NucleusColor.rgb, _NucleusColor.rgb*1.15, ntex*0.4);
                        col.rgb = lerp(ncol, col.rgb, edgeN);
                    }
                }

                // 소기관
                if (r < rimInner){
                    float d = _OrganelleDensity;
                    float2 grid = p * d + phase*20.0;
                    float2 g0 = floor(grid);
                    [unroll]
                    for (int yy=0; yy<2; yy++){
                        [unroll]
                        for (int xx=0; xx<2; xx++){
                            float2 cell = g0 + float2(xx,yy);
                            float2 seed = hash22(cell);
                            float2 center = (cell + seed - (p*d)) / d;
                            float pr = length(p - center) * 2.0;
                            float size = _OrganelleSize * (0.7 + 0.6*seed.x);
                            float soft = _OrganelleSoft;
                            float a = smoothstep(size, size*(1.0-soft), size - pr);
                            col.rgb = lerp(col.rgb, _OrganelleColor.rgb, a * 0.6);
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
