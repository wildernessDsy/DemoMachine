using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 红色碗状工作台的三轴平动 + 实时参数面板 + 下方支撑柱。
///
/// 特性：
/// 1) 目标列表里的部件同步平动（局部坐标），红色碗和转台一起走；
/// 2) 运行时自动生成一根灰色支撑柱，柱底固定、柱顶贴着碗底，
///    平台上升时柱子跟着“伸长”，看起来就是柱子把碗顶起来；
/// 3) 面板默认常显在右上角，进 Play 就能看到数据，不需要点碗；
/// 4) 不再调用相机特写，所以 F / ↑↓←→ 随时可以自由移动视角；
/// 5) 默认开启自动往复演示，进 Play 碗就自己动。
/// </summary>
[DisallowMultipleComponent]
public sealed class PlatformController : MonoBehaviour
{
    [Header("目标部件（可多个）")]
    [Tooltip("拖入要平动的 Transform。留空时控制脚本所在节点本身。")]
    public Transform[] targets;

    [Header("行程范围（米；x=最小值, y=最大值）")]
    [Tooltip("X 左右：默认 -30mm ~ +4mm，按实际避让穿模调整。")]
    public Vector2 xRange = new Vector2(-0.03f, 0.004f);

    [Tooltip("Y 高度：只允许向上，默认 0 ~ 120mm。")]
    public Vector2 yRange = new Vector2(0f, 0.12f);

    [Tooltip("Z 前后：默认 ±120mm。")]
    public Vector2 zRange = new Vector2(-0.12f, 0.12f);

    [Header("运动参数")]
    [Range(0.005f, 0.5f)] public float speed = 0.06f;
    [Range(0.01f, 2f)] public float acceleration = 0.25f;

    [Header("支撑柱")]
    [Tooltip("生成一根灰色柱子顶在碗下面。")]
    public bool addSupportPillar = true;

    [Tooltip("柱子底端比碗初始底面低多少米（决定柱子初始可见长度）。")]
    [Min(0.02f)] public float pillarBaseDrop = 0.28f;

    [Tooltip("柱子半径 = 碗包围盒水平半宽 × 该系数。")]
    [Range(0.1f, 0.9f)] public float pillarRadiusFactor = 0.5f;

    public Color pillarColor = new Color(0.55f, 0.57f, 0.6f, 1f);

    [Header("面板")]
    public string panelTitle = "工作台运动参数";
    [Tooltip("勾上则进 Play 就显示面板；默认关闭，点红色碗才出现。")]
    public bool openPanelOnStart = false;
    public float clickPadding = 0.03f;

    [Header("默认状态")]
    [Tooltip("进 Play 就自动往复演示。")]
    public bool playAutoDemoOnStart = true;

    [System.NonSerialized] public bool autoDemo;

    Transform[] _roots;
    Vector3[] _baseWorldPositions;
    Renderer[] _renderers;
    Camera _camera;

    Vector3 _offset;
    Vector3 _targetOffset;
    Vector3 _velocity;

    float _demoPhase;
    bool _panelOpen;

    Transform _pillar;
    float _pillarTopBaseY;
    float _pillarBottomY;
    float _pillarRadius;
    float _pillarX;
    float _pillarZ;
    bool _pillarReady;

    GUIStyle _panelStyle, _titleStyle, _labelStyle, _valueStyle, _smallStyle, _closeStyle, _buttonStyle;
    Texture2D _panelTexture;
    static Font _cjkFont;
    static bool _fontTried;

    const float PanelWidth = 440f;
    const float PanelHeight = 430f;

    public Vector3 CurrentOffset => _offset;
    public bool PanelOpen => _panelOpen;

    float _manualSpeedUntil;

    /// <summary>用户刚拖过速度滑块时，数据库驱动暂时让位。</summary>
    public bool IsSpeedManual => Time.time < _manualSpeedUntil;

    /// <summary>时间/数据库系统调用：按 mm/s 设置工作台速度。</summary>
    public void SetExternalSpeedMms(float mms)
    {
        if (IsSpeedManual)
            return;

        float mps = Mathf.Clamp(mms / 1000f, 0.005f, 0.5f);
        if (Mathf.Abs(speed - mps) < 0.0001f)
            return;

        speed = mps;
    }

    void Awake()
    {
        NormalizeRanges();
        ResolveTargets();
        _camera = Camera.main;
        _panelOpen = openPanelOnStart;
        autoDemo = playAutoDemoOnStart;
    }

    void Start()
    {
        if (addSupportPillar)
            BuildPillar();
    }

    void OnValidate()
    {
        NormalizeRanges();
    }

    /// <summary>高度只允许向上（下限 0）；区间写反或上限小于下限时自动纠正。</summary>
    void NormalizeRanges()
    {
        yRange = new Vector2(Mathf.Max(0f, yRange.x), Mathf.Max(0.001f, yRange.y));
        if (yRange.y <= yRange.x + 0.001f)
            yRange.y = yRange.x + 0.12f;

        if (xRange.y <= xRange.x + 0.0001f)
            xRange.y = xRange.x + 0.01f;

        if (zRange.y <= zRange.x + 0.0001f)
            zRange.y = zRange.x + 0.01f;
    }

    void ResolveTargets()
    {
        if (targets == null || targets.Length == 0)
            targets = new[] { transform };

        var list = new List<Transform>(targets.Length);
        foreach (var t in targets)
        {
            if (t != null)
                list.Add(t);
        }
        if (list.Count == 0)
            list.Add(transform);

        _roots = list.ToArray();
        _baseWorldPositions = new Vector3[_roots.Length];
        for (int i = 0; i < _roots.Length; i++)
            _baseWorldPositions[i] = _roots[i].position;

        var rendererList = new List<Renderer>();
        foreach (var t in _roots)
            rendererList.AddRange(t.GetComponentsInChildren<Renderer>(true));
        _renderers = rendererList.ToArray();
    }

    // ------------------------------------------------------------------ 柱子

    void BuildPillar()
    {
        if (_renderers == null || _renderers.Length == 0)
            return;

        bool has = false;
        Bounds bounds = default;
        foreach (var r in _renderers)
        {
            if (r == null)
                continue;
            if (!has)
            {
                bounds = r.bounds;
                has = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }
        if (!has)
            return;

        _pillarTopBaseY = bounds.min.y;               // 碗当前底面高度
        _pillarBottomY = _pillarTopBaseY - pillarBaseDrop; // 柱底固定在这里
        _pillarX = bounds.center.x;
        _pillarZ = bounds.center.z;
        _pillarRadius = Mathf.Max(0.03f,
            Mathf.Min(bounds.extents.x, bounds.extents.z) * pillarRadiusFactor);

        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "SupportPillar";
        var collider = go.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        var renderer = go.GetComponent<Renderer>();
        if (renderer != null)
        {
            var mat = new Material(FindLitShader());
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", pillarColor);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", pillarColor);
            renderer.material = mat;
        }

        _pillar = go.transform;
        _pillarReady = true;
        UpdatePillar();
    }

    static Shader FindLitShader()
    {
        string[] names =
        {
            "Universal Render Pipeline/Lit",
            "Standard",
            "Unlit/Color",
            "Sprites/Default"
        };

        foreach (var n in names)
        {
            var s = Shader.Find(n);
            if (s != null)
                return s;
        }
        return Shader.Find("Hidden/InternalErrorShader");
    }

    /// <summary>柱底不动，柱顶跟着碗底走：上升时柱子变长，下降时变短。</summary>
    void UpdatePillar()
    {
        if (!_pillarReady || _pillar == null)
            return;

        float top = _pillarTopBaseY + _offset.y;
        float height = top - _pillarBottomY;

        if (height < 0.02f)
        {
            _pillar.gameObject.SetActive(false);
            return;
        }

        _pillar.gameObject.SetActive(true);
        // Cylinder 原始高度为 2（scale.y = 1 时高 2），所以 scale.y = height / 2。
        _pillar.position = new Vector3(
            _pillarX + _offset.x,
            _pillarBottomY + height * 0.5f,
            _pillarZ + _offset.z);

        _pillar.localScale = new Vector3(_pillarRadius * 2f, height * 0.5f, _pillarRadius * 2f);
    }

    // ------------------------------------------------------------------ 每帧

    void Update()
    {
        if (_roots == null)
            ResolveTargets();

        HandleClick();

        if (_panelOpen && Keyboard.current != null && Keyboard.current[Key.Escape].wasPressedThisFrame)
            _panelOpen = false;

        if (autoDemo)
        {
            _demoPhase += Time.deltaTime * Mathf.Max(0.05f, speed) * 6f;
            _targetOffset = new Vector3(
                0f,
                0f,
                Mathf.Lerp(zRange.x, zRange.y, 0.5f - 0.5f * Mathf.Cos(_demoPhase)));
        }

        _targetOffset = new Vector3(
            Mathf.Clamp(_targetOffset.x, xRange.x, xRange.y),
            Mathf.Clamp(_targetOffset.y, yRange.x, yRange.y),
            Mathf.Clamp(_targetOffset.z, zRange.x, zRange.y));

        StepMotion(Time.deltaTime);

        for (int i = 0; i < _roots.Length; i++)
        {
            if (_roots[i] != null)
                _roots[i].position = _baseWorldPositions[i] + _offset;
        }

        UpdatePillar();
    }

    /// <summary>按目标节点的真实位移计算当前偏移，避免显示与实际不同步。</summary>
    Vector3 ActualOffset()
    {
        if (_roots == null || _roots.Length == 0 || _baseWorldPositions == null)
            return _offset;

        Vector3 delta = Vector3.zero;
        int count = 0;
        for (int i = 0; i < _roots.Length; i++)
        {
            if (_roots[i] == null)
                continue;
            delta += _roots[i].position - _baseWorldPositions[i];
            count++;
        }

        return count > 0 ? delta / count : _offset;
    }

    void StepMotion(float dt)
    {
        Vector3 toTarget = _targetOffset - _offset;
        float distance = toTarget.magnitude;

        if (distance < 1e-5f)
        {
            _offset = _targetOffset;
            _velocity = Vector3.zero;
            return;
        }

        Vector3 dir = toTarget / distance;
        float allowedSpeed = Mathf.Min(speed, distance / Mathf.Max(dt, 1e-5f));
        Vector3 desiredVelocity = dir * allowedSpeed;

        _velocity = Vector3.MoveTowards(_velocity, desiredVelocity, acceleration * dt);
        _offset += _velocity * dt;

        if ((_targetOffset - _offset).sqrMagnitude < 1e-8f)
        {
            _offset = _targetOffset;
            _velocity = Vector3.zero;
        }
    }

    void HandleClick()
    {
        var mouse = Mouse.current;
        if (mouse == null || _camera == null || _renderers == null || _renderers.Length == 0)
            return;
        if (!mouse.leftButton.wasPressedThisFrame)
            return;

        Vector2 screenPosition = mouse.position.ReadValue();
        if (_panelOpen && GetPanelRect().Contains(new Vector2(screenPosition.x, Screen.height - screenPosition.y)))
            return;

        Ray ray = _camera.ScreenPointToRay(new Vector3(screenPosition.x, screenPosition.y, 0f));
        foreach (var r in _renderers)
        {
            if (r == null)
                continue;
            Bounds b = r.bounds;
            b.Expand(clickPadding);
            if (b.IntersectRay(ray))
            {
                _panelOpen = !_panelOpen;
                return;
            }
        }
    }

    [ContextMenu("复位到基准位置")]
    public void ResetToBase()
    {
        _offset = Vector3.zero;
        _targetOffset = Vector3.zero;
        _velocity = Vector3.zero;
        autoDemo = false;
        if (_roots == null)
            return;

        for (int i = 0; i < _roots.Length; i++)
        {
            if (_roots[i] != null)
                _roots[i].position = _baseWorldPositions[i];
        }
        UpdatePillar();
    }

    /// <summary>切换机器时恢复该机器的工作台位置。</summary>
    public void SetOffset(Vector3 offset)
    {
        autoDemo = false;
        _velocity = Vector3.zero;
        _offset = new Vector3(
            Mathf.Clamp(offset.x, xRange.x, xRange.y),
            Mathf.Clamp(offset.y, yRange.x, yRange.y),
            Mathf.Clamp(offset.z, zRange.x, zRange.y));
        _targetOffset = _offset;

        if (_roots == null)
            return;

        for (int i = 0; i < _roots.Length; i++)
        {
            if (_roots[i] != null)
                _roots[i].position = _baseWorldPositions[i] + _offset;
        }
        UpdatePillar();
    }

    // ------------------------------------------------------------------ 面板

    Rect GetPanelRect()
    {
        float width = Mathf.Clamp(Screen.width * 0.26f, 260f, PanelWidth);
        float x = Screen.width - width - 24f;
        float y = 16f;
        float height = Mathf.Min(PanelHeight, Screen.height - 32f);
        return new Rect(x, y, width, height);
    }

    void OnGUI()
    {
        if (!_panelOpen)
            return;

        EnsureStyles();

        Rect panel = GetPanelRect();
        {
            Color boxColor = GUI.color;
            GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.86f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = boxColor;
        }

        float left = panel.x + 14f;
        float contentWidth = panel.width - 28f;
        float y = panel.y + 10f;

        GUI.Label(new Rect(left, y, contentWidth - 34f, 24f), panelTitle, _titleStyle);
        if (GUI.Button(new Rect(panel.xMax - 36f, y, 22f, 22f), "×", _closeStyle))
            _panelOpen = false;
        y += 30f;

        Vector3 shown = ActualOffset();
        string pos = $"X {_offset.x * 1000f:+0;-0;0}   Y {_offset.y * 1000f:+0;-0;0}   Z {_offset.z * 1000f:+0;-0;0} mm";
        GUI.Label(new Rect(left, y, contentWidth, 20f), pos, _valueStyle);
        y += 24f;

        y = DrawAxis(left, y, contentWidth, "X  左右", ref _targetOffset.x, xRange.x, xRange.y);
        y = DrawAxis(left, y, contentWidth, "Y  高度", ref _targetOffset.y, yRange.x, yRange.y);
        y = DrawAxis(left, y, contentWidth, "Z  前后", ref _targetOffset.z, zRange.x, zRange.y);

        GUI.Label(new Rect(left, y, contentWidth, 18f), $"速度 {speed * 1000f:F0} mm/s", _labelStyle);
        float nextSpeed = GUI.HorizontalSlider(new Rect(left, y + 18f, contentWidth, 14f), speed, 0.005f, 0.3f);
        if (Mathf.Abs(nextSpeed - speed) > 1e-5f)
        {
            speed = nextSpeed;
            _manualSpeedUntil = Time.time + MachineTimeData.ManualHoldSeconds;
        }
        y += 40f;

        GUI.Label(new Rect(left, y, contentWidth, 18f), $"加速度 {acceleration:F2} m/s²", _labelStyle);
        acceleration = GUI.HorizontalSlider(new Rect(left, y + 18f, contentWidth, 14f), acceleration, 0.01f, 1.5f);
        y += 40f;

        if (GUI.Button(new Rect(left, y, (contentWidth - 8f) * 0.5f, 26f), autoDemo ? "停止演示" : "自动演示", _buttonStyle))
            autoDemo = !autoDemo;

        if (GUI.Button(new Rect(left + (contentWidth + 8f) * 0.5f, y, (contentWidth - 8f) * 0.5f, 26f), "重置", _buttonStyle))
            ResetToBase();

        GUI.Label(new Rect(left, panel.yMax - 22f, contentWidth, 18f),
            "F 视角模式 · Esc 关面板", _smallStyle);
    }

    /// <summary>拖动任一轴滑块时自动退出演示模式，避免和自动轨迹打架。</summary>
    float DrawAxis(float left, float y, float width, string label, ref float value, float min, float max)
    {
        GUI.Label(new Rect(left, y, width, 18f), $"{label}    {value * 1000f:+0;-0;0} mm", _labelStyle);
        float next = GUI.HorizontalSlider(new Rect(left, y + 18f, width, 14f), value, min, max);
        if (Mathf.Abs(next - value) > 1e-5f)
        {
            value = next;
            autoDemo = false;
        }
        return y + 40f;
    }

    void EnsureStyles()
    {
        if (_panelStyle != null)
            return;

        _panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        _panelTexture.SetPixel(0, 0, new Color(0.055f, 0.065f, 0.085f, 0.88f));
        _panelTexture.Apply();

        _panelStyle = new GUIStyle { normal = { background = _panelTexture }, border = new RectOffset(0, 0, 0, 0) };

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = new Color(0.85f, 0.9f, 0.95f) }
        };

        _valueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.45f, 1f, 0.65f) }
        };

        _smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal = { textColor = new Color(0.65f, 0.7f, 0.78f) }
        };

        _closeStyle = new GUIStyle(GUI.skin.button) { fontSize = 16, alignment = TextAnchor.MiddleCenter };

        Font font = GetCjkFont();
        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleCenter
        };
        Color btnText = new Color(0.92f, 0.95f, 1f);
        _buttonStyle.normal.textColor = btnText;
        _buttonStyle.hover.textColor = Color.white;
        _buttonStyle.active.textColor = Color.white;
        _buttonStyle.focused.textColor = btnText;
        if (font != null)
        {
            _titleStyle.font = font;
            _labelStyle.font = font;
            _valueStyle.font = font;
            _smallStyle.font = font;
            _closeStyle.font = font;
        _buttonStyle.font = font;
        }
    }

    static Font GetCjkFont()
    {
        if (_fontTried)
            return _cjkFont;

        _fontTried = true;
        try
        {
            _cjkFont = CjkFont.Get();
        }
        catch
        {
            _cjkFont = null;
        }

        return _cjkFont;
    }

    void OnDestroy()
    {
        if (_panelTexture != null)
            Destroy(_panelTexture);
    }
}
