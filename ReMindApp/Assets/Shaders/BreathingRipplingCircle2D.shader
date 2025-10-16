Shader "ReMind/BreathingRipplingCircle2D"
{
    Properties
    {
        _MainColor     ("Main Color (RGBA)", Color) = (1,1,1,1)

        // 基础形状
        _BaseRadius    ("Base Radius (0-1 UV)", Range(0.0, 1.0)) = 0.35
        _EdgeSoftness  ("Edge Softness", Range(0.0001, 0.2))     = 0.01
        _Center        ("Center (UV)", Vector)                   = (0.5, 0.5, 0, 0)
        _Aspect        ("Aspect (width/height)", Float)          = 1.0

        // 不规则（角向谐波）
        _IrrAmp        ("Irregularity Amp", Range(0.0, 0.6))     = 0.15
        _IrrW1         ("Harmonic Mix (3 vs 5)", Range(0.0, 1.0))= 0.6
        _IrrPhase1     ("Phase H3", Range(0, 6.28318))           = 0.0
        _IrrPhase2     ("Phase H5", Range(0, 6.28318))           = 1.2

        // 呼吸
        _BreathAmp     ("Breath Amplitude", Range(0.0, 0.6))     = 0.08
        _BreathSpeed   ("Breath Speed", Range(0.0, 10.0))        = 1.0

        // 扩散波纹
        _Diffuse       ("Diffuse (0..1)", Range(0.0, 1.0))        = 0.0
        _RippleAmp     ("Ripple Amplitude", Range(0.0, 0.3))      = 0.06
        _RippleFreq    ("Ripple Frequency (cycles/UV)", Range(0.1, 30.0)) = 8.0
        _DiffuseRange  ("Diffuse Push Distance", Range(0.0, 2.0)) = 0.8
        _RippleSharp   ("Ripple Sharpness", Range(0.0, 10.0))     = 2.0

        // 额外时间缩放（可做慢动作等）
        _TimeScale     ("Time Scale", Range(0.0, 3.0))            = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        LOD 100

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0; // 假定 0..1
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            float4 _MainColor;

            float _BaseRadius;
            float _EdgeSoftness;
            float4 _Center;      // xy 用
            float  _Aspect;

            float _IrrAmp;
            float _IrrW1;
            float _IrrPhase1;
            float _IrrPhase2;

            float _BreathAmp;
            float _BreathSpeed;

            float _Diffuse;
            float _RippleAmp;
            float _RippleFreq;
            float _DiffuseRange;
            float _RippleSharp;

            float _TimeScale;

            // Unity 提供
            // float4 _Time;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            // 角向不规则度：用多个谐波让圆边“起伏”
            float IrregularRadiusFactor(float theta)
            {
                // 3θ 与 5θ 两组正弦叠加
                float h3 = sin(3.0 * theta + _IrrPhase1);
                float h5 = sin(5.0 * theta + _IrrPhase2);
                float mixH = lerp(h5, h3, _IrrW1);
                return 1.0 + _IrrAmp * mixH;
            }

            // 柔和的带相位平移的径向波（扩散用）
            float RadialRipple(float r, float t)
            {
                // 把扩散值映射到一个“位移距离”，用于向外推移波前
                float phaseShift = _Diffuse * _DiffuseRange;

                // 波函数：sin(2π * freq * (r - phaseShift) - ωt)
                // 这里用时间推进波，也可只靠 Diffuse 推进
                float w = 2.0 * UNITY_PI * _RippleFreq * (r - phaseShift) - t;
                float s = sin(w);

                // 锐化一下波峰（让“溢散”更像一朵朵浪）
                float sharp = pow(saturate(0.5 * (s + 1.0)), _RippleSharp); // 0..1, 峰更尖
                // 映回 -1..1，用于正负偏移
                float shaped = (sharp * 2.0 - 1.0);

                return _RippleAmp * shaped;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // 时间
                float t = _Time.y * _TimeScale;

                // UV 空间到“等轴”空间：x 乘以纵横比，避免拉伸下变椭圆
                float2 p = uv - _Center.xy;
                p.x *= _Aspect;

                float r = length(p);
                float theta = atan2(p.y, p.x); // -π..π

                // 基本半径（角向不规则）
                float baseR = _BaseRadius * IrregularRadiusFactor(theta);

                // 呼吸（整体缩放）
                float breath = 1.0 + _BreathAmp * sin(t * _BreathSpeed * 2.0 * UNITY_PI);
                float R = baseR * breath;

                // 扩散波纹（把波峰向外推）
                float ripple = RadialRipple(r, t);

                // SDF：圆盘距离（r - R），叠加波纹偏移
                float dist = r - (R + ripple);

                // 柔和边缘，dist<0 为内部
                float alpha = 1.0 - smoothstep(0.0, _EdgeSoftness, dist);

                // 颜色输出
                fixed4 col = _MainColor;
                col.a *= alpha;

                return col;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
