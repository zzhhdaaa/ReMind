using UnityEngine;

/// <summary>
/// 按住空格：保持“微微收缩”的姿态（小幅脉动）
/// 松开空格：进入爆发（前10%）→ 回落（后90%）
/// 爆发期会临时提升 Ripple 参数，增强冲击波观感。
/// 可选覆盖 _MainColor 以控制颜色/透明度。
/// </summary>
[RequireComponent(typeof(Renderer))]
public class RipplePulseHoldExplodeController : MonoBehaviour
{
    private enum State { Idle, Holding, Exploding, Settling }

    [Header("Timeline")]
    [SerializeField] private float totalDuration = 2f;
    [Range(0.05f, 0.5f)]
    [SerializeField] private float explodeFraction = 0.10f;

    [Header("Holding (按住时的“微微收缩”脉动)")]
    [Tooltip("按住时的基准整体缩放（小于1略收缩，=1不变）")]
    [SerializeField] private float holdBaseScaleMul = 0.96f;
    [Tooltip("按住时的细微脉动幅度（在基准上做 ± 振荡）")]
    [SerializeField] private float holdPulseAmp = 0.02f;
    [Tooltip("按住时的脉动速度（Hz）")]
    [SerializeField] private float holdPulseHz = 1.2f;
    [Tooltip("按住时的透明度乘子（1=不变；<1略淡）")]
    [SerializeField, Range(0f,1f)] private float holdAlphaMul = 1f;

    [Header("Explode (爆发期目标)")]
    [SerializeField] private float explodeScaleMul = 1.35f;
    [SerializeField, Range(0f,1f)] private float explodeDiffuse = 1f;
    [SerializeField, Range(0f,1f)] private float explodeMinAlpha = 0.35f;
    [SerializeField] private float explodeEdgeSoftnessMul = 3.0f;

    [Header("Explode Ripple Boost (冲击波增强)")]
    [Tooltip("爆发期临时提升的 RippleAmp 目标（会从初始值 Lerp 到这里）")]
    [SerializeField] private float explodeRippleAmp = 0.12f;
    [Tooltip("爆发期临时提升的 RippleFreq 目标")]
    [SerializeField] private float explodeRippleFreq = 12f;
    [Tooltip("爆发期临时提升的 RippleSharp 目标")]
    [SerializeField] private float explodeRippleSharp = 4f;

    [Header("Easing")]
    [SerializeField] private AnimationCurve explodeEase = AnimationCurve.EaseInOut(0,0,1,1);
    [SerializeField] private AnimationCurve settleEase  = AnimationCurve.EaseInOut(0,0,1,1);

    [Header("Shader Property Names")]
    [SerializeField] private string colorProp        = "_MainColor";
    [SerializeField] private string diffuseProp      = "_Diffuse";
    [SerializeField] private string edgeSoftnessProp = "_EdgeSoftness";
    [SerializeField] private string rippleAmpProp    = "_RippleAmp";
    [SerializeField] private string rippleFreqProp   = "_RippleFreq";
    [SerializeField] private string rippleSharpProp  = "_RippleSharp";

    [Header("Color Override (可控颜色)")]
    [SerializeField] private bool overrideColor = false;
    [SerializeField] private Color overrideBaseColor = Color.white;

    private Renderer _renderer;
    private MaterialPropertyBlock _mpb;

    // 初始采样（从材质和物体读取）
    private Vector3 _initScale;
    private float _initDiffuse;
    private float _initEdgeSoftness;
    private float _initRippleAmp;
    private float _initRippleFreq;
    private float _initRippleSharp;
    private Color _initColor;

    // 运行态
    private State _state = State.Idle;
    private float _time;       // 爆发/回落过程计时（0..totalDuration）
    private float _explodeT;   // 爆发阶段时长
    private float _settleT;    // 回落阶段时长

    // 缓存：爆发开始时把“当前值”当成起点，便于无缝衔接
    private Vector3 _startScale;
    private float _startDiffuse;
    private float _startEdgeSoftness;
    private float _startRippleAmp;
    private float _startRippleFreq;
    private float _startRippleSharp;
    private float _startAlpha;  // 从当前Alpha到爆发期目标再回落

    void Awake()
    {
        _renderer = GetComponent<Renderer>();
        _mpb = new MaterialPropertyBlock();
        CacheInitialsFromMaterial();
        ComputePhaseDurations();
    }

    void OnValidate()
    {
        if (Application.isPlaying) ComputePhaseDurations();
    }

    private void ComputePhaseDurations()
    {
        _explodeT = Mathf.Max(0.0001f, totalDuration * Mathf.Clamp01(explodeFraction));
        _settleT  = Mathf.Max(0.0001f, totalDuration - _explodeT);
    }

    private void CacheInitialsFromMaterial()
    {
        _initScale = transform.localScale;

        _renderer.GetPropertyBlock(_mpb);
        // 颜色：如果材质里没设，默认白 1
        _initColor = _mpb.GetVector(colorProp);
        if (_initColor.a <= 0f) _initColor.a = 1f;

        _initDiffuse      = SafeGetFloat(diffuseProp, 0f);
        _initEdgeSoftness = SafeGetFloat(edgeSoftnessProp, 0.01f);
        _initRippleAmp    = SafeGetFloat(rippleAmpProp, 0.06f);
        _initRippleFreq   = SafeGetFloat(rippleFreqProp, 8f);
        _initRippleSharp  = SafeGetFloat(rippleSharpProp, 2f);
    }

    private float SafeGetFloat(string prop, float fallback)
    {
        float v = _mpb.GetFloat(prop);
        if (float.IsNaN(v) || Mathf.Approximately(v, 0f) && !_mpb.HasVector(colorProp) && !_mpb.HasFloat(prop))
            return fallback;
        return v == 0f ? fallback : v; // 简单容错
    }

    void Update()
    {
        // 输入：空格按下/松开
        if (Input.GetKeyDown(KeyCode.Space))
        {
            StartHolding();
        }
        if (Input.GetKeyUp(KeyCode.Space))
        {
            // 结束 Holding → 进入爆发
            StartExplodeFromCurrent();
        }

        switch (_state)
        {
            case State.Idle:
                // 无操作
                break;

            case State.Holding:
                UpdateHolding();
                break;

            case State.Exploding:
                UpdateExploding();
                break;

            case State.Settling:
                UpdateSettling();
                break;
        }
    }

    private void StartHolding()
    {
        // 进入 Holding 前先采样当前材质与缩放作为“初始参考”
        CacheInitialsFromMaterial();
        _state = State.Holding;
    }

    private void UpdateHolding()
    {
        // 小幅脉动（“微微收缩”）
        float pulse = Mathf.Sin(Time.time * Mathf.PI * 2f * holdPulseHz) * holdPulseAmp;
        float sMul = Mathf.Max(0.01f, holdBaseScaleMul + pulse); // 保底
        transform.localScale = _initScale * sMul;

        // alpha 略调
        float alpha = (_initColor.a) * holdAlphaMul;
        ApplyToMaterial(_initDiffuse, _initEdgeSoftness, alpha,
                        _initRippleAmp, _initRippleFreq, _initRippleSharp);
    }

    private void StartExplodeFromCurrent()
    {
        // 将“当前状态”作为爆发起点，保证无缝过渡
        _renderer.GetPropertyBlock(_mpb);

        _startScale        = transform.localScale;
        _startDiffuse      = _mpb.GetFloat(diffuseProp);
        _startEdgeSoftness = _mpb.GetFloat(edgeSoftnessProp);
        _startRippleAmp    = _mpb.GetFloat(rippleAmpProp);
        _startRippleFreq   = _mpb.GetFloat(rippleFreqProp);
        _startRippleSharp  = _mpb.GetFloat(rippleSharpProp);

        Color cur = _mpb.GetVector(colorProp);
        _startAlpha = cur.a <= 0f ? _initColor.a : cur.a;

        _time = 0f;
        _state = State.Exploding;
    }

    private void UpdateExploding()
    {
        _time += Time.deltaTime;
        float t = Mathf.Min(_time, _explodeT);
        float u = t / _explodeT;                 // 0..1
        float e = explodeEase.Evaluate(u);

        // scale
        Vector3 scl = Vector3.LerpUnclamped(_startScale, _initScale * explodeScaleMul, e);

        // diffuse
        float d = Mathf.LerpUnclamped(_startDiffuse, explodeDiffuse, e);

        // softness
        float soft = Mathf.LerpUnclamped(_startEdgeSoftness, _initEdgeSoftness * explodeEdgeSoftnessMul, e);

        // alpha
        float a = Mathf.LerpUnclamped(_startAlpha, explodeMinAlpha, e);

        // ripple boosts
        float rAmp   = Mathf.LerpUnclamped(_startRippleAmp,   explodeRippleAmp,   e);
        float rFreq  = Mathf.LerpUnclamped(_startRippleFreq,  explodeRippleFreq,  e);
        float rSharp = Mathf.LerpUnclamped(_startRippleSharp, explodeRippleSharp, e);

        transform.localScale = scl;
        ApplyToMaterial(d, soft, a, rAmp, rFreq, rSharp);

        if (_time >= _explodeT)
        {
            _time = 0f;
            _state = State.Settling;
        }
    }

    private void UpdateSettling()
    {
        _time += Time.deltaTime;
        float t = Mathf.Min(_time, _settleT);
        float u = t / _settleT;                 // 0..1
        float e = settleEase.Evaluate(u);

        // scale：从爆发目标回到“原始初始Scale”
        Vector3 scl = Vector3.LerpUnclamped(_initScale * explodeScaleMul, _initScale, e);

        // diffuse 回到初始
        float d = Mathf.LerpUnclamped(explodeDiffuse, _initDiffuse, e);

        // softness 回到初始
        float soft = Mathf.LerpUnclamped(_initEdgeSoftness * explodeEdgeSoftnessMul, _initEdgeSoftness, e);

        // alpha 回到初始
        float a = Mathf.LerpUnclamped(explodeMinAlpha, _initColor.a, e);

        // ripple 参数回到初始
        float rAmp   = Mathf.LerpUnclamped(explodeRippleAmp,   _initRippleAmp,   e);
        float rFreq  = Mathf.LerpUnclamped(explodeRippleFreq,  _initRippleFreq,  e);
        float rSharp = Mathf.LerpUnclamped(explodeRippleSharp, _initRippleSharp, e);

        transform.localScale = scl;
        ApplyToMaterial(d, soft, a, rAmp, rFreq, rSharp);

        if (_time >= _settleT)
        {
            // 完成一次循环
            transform.localScale = _initScale;
            ApplyToMaterial(_initDiffuse, _initEdgeSoftness, _initColor.a,
                            _initRippleAmp, _initRippleFreq, _initRippleSharp);
            _state = State.Idle;
        }
    }

    private void ApplyToMaterial(float diffuse, float edgeSoftness, float alpha,
                                 float rippleAmp, float rippleFreq, float rippleSharp)
    {
        _renderer.GetPropertyBlock(_mpb);

        _mpb.SetFloat(diffuseProp, diffuse);
        _mpb.SetFloat(edgeSoftnessProp, edgeSoftness);
        _mpb.SetFloat(rippleAmpProp, rippleAmp);
        _mpb.SetFloat(rippleFreqProp, rippleFreq);
        _mpb.SetFloat(rippleSharpProp, rippleSharp);

        // 颜色：可选覆盖
        Color baseCol = overrideColor ? overrideBaseColor : _initColor;
        baseCol.a = alpha;
        _mpb.SetColor(colorProp, baseCol);

        _renderer.SetPropertyBlock(_mpb);
    }

    // —— 可选外部 API ——

    /// <summary>外部代码可直接设置总时长。</summary>
    public void SetTotalDuration(float seconds)
    {
        totalDuration = Mathf.Max(0.05f, seconds);
        ComputePhaseDurations();
    }

    /// <summary>程序触发一次完整“松手爆发”流程（不经过Holding）。</summary>
    public void TriggerOnce()
    {
        CacheInitialsFromMaterial();
        _startScale        = _initScale;
        _startDiffuse      = _initDiffuse;
        _startEdgeSoftness = _initEdgeSoftness;
        _startRippleAmp    = _initRippleAmp;
        _startRippleFreq   = _initRippleFreq;
        _startRippleSharp  = _initRippleSharp;
        _startAlpha        = _initColor.a;

        _time = 0f;
        _state = State.Exploding;
    }
}
