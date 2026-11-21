using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 切削工艺参数：S 主轴转速、F 进给速度、ap 切深、ae 切宽、T 刀号，
/// 以及由 ap×ae×F 估算的主轴负载与功率。
///
/// - S 驱动主轴自转（可见效果）；
/// - T 驱动刀盘转位到对应刀位；
/// - 数值可由 MachineTimeController 按数据库时段下发，面板滑块手动覆盖 8 秒。
/// </summary>
[DisallowMultipleComponent]
public sealed class CuttingParameters : MonoBehaviour
{
    [Header("节点路径（相对机床根节点）")]
    public string machineRootName = "五轴机床";

    [Tooltip("真正自转的刀具节点：只含旋转的刀柄/刀具，不含主轴外壳。")]
    public string spindleToolPath = "Y/Y轴/X/X轴/Z/Z轴/主轴/Cylinder.001";

    public string toolTrayPath = "m/Solid_381/Extruded.020/Solid_1790/刀盘";

    [Header("自转轴（局部方向）")]
    public Vector3 toolTrayLocalAxis = Vector3.up;

    [Header("主轴可视化")]
    [Tooltip("由刀具节点上的 DrillController 驱动自转。本组件只下发可视化转速，" +
             "不直接写 Transform —— 避免转错物体（外壳）或转错轴（水平轴）。")]
    public bool driveSpindleVisual = true;

    [Tooltip("可视化最大转速（rpm）。真实 S 可达上万转，按比例缩放以免画面频闪。")]
    [Min(1f)] public float visualMaxRpm = 300f;

    [Header("工艺范围")]
    public Vector2 spindleRpmRange = new Vector2(0f, 12000f);
    public Vector2 feedRange = new Vector2(0f, 10000f);
    public Vector2 cutDepthRange = new Vector2(0f, 5f);
    public Vector2 cutWidthRange = new Vector2(0f, 20f);
    [Range(2, 60)] public int toolCount = 24;

    [Tooltip("主轴额定功率（kW），用于把负载百分比换算成功率。")]
    [Min(0.1f)] public float ratedPowerKw = 15f;

    [Tooltip("材料去除率达到该值（mm³/min）时视为 100% 负载。")]
    [Min(1f)] public float fullLoadRemovalRate = 120000f;

    [Header("面板")]
    public bool showPanel = true;
    public Key togglePanelKey = Key.C;
    public float panelWidth = 440f;
    public float panelHeight = 320f;

    [Header("调试")]
    public bool logSetup = true;

    [Header("状态联动")]
    [Tooltip("由 MachineTimeController 每帧写入。非 running / warmup 时主轴不转、负载归零；" +
             "手动拖过滑块则不受状态限制，方便单独调试。")]
    public string machineStateKey = "idle";

    [Header("当前值")]
    public float spindleRpm;
    public float feedRateMmpm;
    public float cutDepthMm;
    public float cutWidthMm;
    public int toolNumber = 1;

    Transform _machineRoot;
    Transform _spindleTool;
    Transform _toolTray;
    DrillController _drill;

    Quaternion _toolTrayRest = Quaternion.identity;

    float _trayAngle;
    float _trayTargetAngle;

    float _manualUntil;

    Texture2D _panelTexture;
    GUIStyle _titleStyle;
    GUIStyle _labelStyle;
    GUIStyle _valueStyle;
    GUIStyle _smallStyle;

    static Font _cjkFont;
    static bool _fontTried;

    /// <summary>刚拖过滑块时为 true，数据库暂时让位。</summary>
    public bool IsManual => Time.time < _manualUntil;

    public bool IsReady => _drill != null || _toolTray != null;

    /// <summary>状态允许主轴转（加工 / 升温）。</summary>
    public bool StateAllowsSpindle => machineStateKey == "running" || machineStateKey == "warmup";

    /// <summary>当前是否真的在转（手动调试时不受状态限制）。</summary>
    public bool IsSpinning => IsManual || StateAllowsSpindle;

    /// <summary>材料去除率 mm³/min。</summary>
    public float RemovalRate => Mathf.Max(0f, cutDepthMm) * Mathf.Max(0f, cutWidthMm) * Mathf.Max(0f, feedRateMmpm);

    /// <summary>主轴负载百分比（可超过 100 表示过载）；非加工状态为 0。</summary>
    public float LoadPercent => IsSpinning
        ? Mathf.Clamp(RemovalRate / Mathf.Max(1f, fullLoadRemovalRate) * 100f, 0f, 150f)
        : 0f;

    /// <summary>主轴功率 kW。</summary>
    public float PowerKw => LoadPercent / 100f * ratedPowerKw;

    void Awake()
    {
        ResolveNodes();
    }

    void Start()
    {
        if (logSetup)
        {
            Debug.Log($"[CuttingParameters] 刀具={(_spindleTool != null ? _spindleTool.name : "未找到")} " +
                      $"DrillController={(_drill != null ? "已找到" : "未找到")} " +
                      $"刀盘={(_toolTray != null ? _toolTray.name : "未找到")} 刀位数={toolCount}", this);
        }
    }

    void ResolveNodes()
    {
        if (_machineRoot == null)
        {
            GameObject machine = GameObject.Find(machineRootName);
            if (machine != null)
                _machineRoot = machine.transform;
        }

        if (_machineRoot == null)
            return;

        if (_spindleTool == null && _drill == null)
        {
            _spindleTool = _machineRoot.Find(spindleToolPath);
            if (_spindleTool != null)
                _drill = _spindleTool.GetComponent<DrillController>();
        }

        if (_drill == null)
        {
            // DrillController 由 DrillDemoBootstrap 在 AfterSceneLoad 添加，
            // 与本组件的执行顺序不确定，因此这里遍历兜底查找。
            foreach (var candidate in _machineRoot.GetComponentsInChildren<DrillController>(true))
            {
                _drill = candidate;
                _spindleTool = candidate.transform;
                break;
            }
        }

        if (_toolTray == null)
        {
            _toolTray = _machineRoot.Find(toolTrayPath);
            if (_toolTray != null)
                _toolTrayRest = _toolTray.localRotation;
        }
    }

    /// <summary>数据库下发工艺参数（手动优先期间忽略）。</summary>
    public void SetExternal(float rpm, float feed, float ap, float ae, int tool)
    {
        if (IsManual)
            return;

        spindleRpm = Mathf.Clamp(rpm, spindleRpmRange.x, spindleRpmRange.y);
        feedRateMmpm = Mathf.Clamp(feed, feedRange.x, feedRange.y);
        cutDepthMm = Mathf.Clamp(ap, cutDepthRange.x, cutDepthRange.y);
        cutWidthMm = Mathf.Clamp(ae, cutWidthRange.x, cutWidthRange.y);
        toolNumber = Mathf.Clamp(tool, 1, toolCount);
    }

    /// <summary>面板手动设定，8 秒内优先于数据库。</summary>
    public void MarkManual()
    {
        _manualUntil = Time.time + MachineTimeData.ManualHoldSeconds;
    }

    void Update()
    {
        if (!IsReady)
        {
            ResolveNodes();
            return;
        }

        // 主轴自转：交给刀具节点上的 DrillController。
        // 它只旋转 Cylinder.001，且自转轴由网格包围盒实测得到（局部 Z），
        // 因此不会带动主轴外壳，也不会绕水平轴翻滚。
        if (driveSpindleVisual && _drill != null)
        {
            float ratio = IsSpinning
                ? Mathf.InverseLerp(0f, Mathf.Max(1f, spindleRpmRange.y), spindleRpm)
                : 0f;
            _drill.SetExternalRpm(ratio * visualMaxRpm);
        }

        if (_toolTray != null)
        {
            float step = (360f / Mathf.Max(2, toolCount)) * (toolNumber - 1);
            _trayTargetAngle = Mathf.Repeat(step, 360f);

            _trayAngle = Mathf.MoveTowardsAngle(_trayAngle, _trayTargetAngle, 120f * Time.deltaTime);
            _toolTray.localRotation = Quaternion.AngleAxis(_trayAngle, toolTrayLocalAxis.normalized) * _toolTrayRest;
        }

        if (Keyboard.current != null && Keyboard.current[togglePanelKey].wasPressedThisFrame)
            showPanel = !showPanel;
    }

    void OnGUI()
    {
        if (!showPanel)
            return;

        EnsureStyles();

        float width = panelWidth;
        float height = panelHeight;
        float x = Screen.width - width - 24f;
        float y = Screen.height - height - 16f;
        var panel = new Rect(x, y, width, height);

        Color old = GUI.color;
        GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.86f);
        GUI.DrawTexture(panel, Texture2D.whiteTexture);
        GUI.color = old;

        float left = panel.x + 16f;
        float contentWidth = panel.width - 32f;
        float top = panel.y + 10f;

        GUI.Label(new Rect(left, top, contentWidth, 18f), "切削工艺参数", _titleStyle);
        float rowY = top + 24f;

        rowY = DrawRow(left, rowY, contentWidth, "S 主轴转速", ref spindleRpm, spindleRpmRange.x, spindleRpmRange.y, "rpm", 0);
        rowY = DrawRow(left, rowY, contentWidth, "F 进给速度", ref feedRateMmpm, feedRange.x, feedRange.y, "mm/min", 0);
        rowY = DrawRow(left, rowY, contentWidth, "ap 切削深度", ref cutDepthMm, cutDepthRange.x, cutDepthRange.y, "mm", 1);
        rowY = DrawRow(left, rowY, contentWidth, "ae 切削宽度", ref cutWidthMm, cutWidthRange.x, cutWidthRange.y, "mm", 1);

        // 刀号：离散滑块
        GUI.Label(new Rect(left, rowY, contentWidth, 16f), $"T 刀号   {toolNumber:00} / {toolCount}", _labelStyle);
        float nextTool = GUI.HorizontalSlider(new Rect(left, rowY + 16f, contentWidth, 12f), toolNumber, 1f, toolCount);
        int rounded = Mathf.Clamp(Mathf.RoundToInt(nextTool), 1, toolCount);
        if (rounded != toolNumber)
        {
            toolNumber = rounded;
            MarkManual();
        }
        rowY += 34f;

        // 负载条
        float load = LoadPercent;
        var barRect = new Rect(left, rowY, contentWidth, 14f);
        GUI.color = new Color(1f, 1f, 1f, 0.14f);
        GUI.DrawTexture(barRect, Texture2D.whiteTexture);

        float fill = Mathf.Clamp01(load / 120f);
        GUI.color = load >= 100f ? new Color(1f, 0.35f, 0.3f) : load >= 75f ? new Color(1f, 0.75f, 0.3f) : new Color(0.35f, 0.85f, 0.55f);
        GUI.DrawTexture(new Rect(barRect.x, barRect.y, barRect.width * fill, barRect.height), Texture2D.whiteTexture);
        GUI.color = old;

        rowY += 18f;
        GUI.Label(new Rect(left, rowY, contentWidth, 16f),
            $"主轴负载  {load:F0} %    功率  {PowerKw:F1} / {ratedPowerKw:F0} kW", _valueStyle);
        rowY += 20f;

        GUI.Label(new Rect(left, rowY, contentWidth, 16f),
            $"去除率 {RemovalRate:F0} mm³/min · 满负载 {fullLoadRemovalRate:F0}", _smallStyle);

        float visualRpm = IsSpinning
            ? Mathf.InverseLerp(0f, Mathf.Max(1f, spindleRpmRange.y), spindleRpm) * visualMaxRpm
            : 0f;
        GUI.Label(new Rect(left, rowY + 16f, contentWidth, 16f),
            _drill != null
                ? (IsSpinning
                    ? $"刀具可视化 {visualRpm:F0} rpm（真实 S 按 {visualMaxRpm:F0} rpm 缩放显示）"
                    : "当前状态不转动（手动拖滑块可强制测试）")
                : "未找到 DrillController · 主轴不旋转",
            _smallStyle);

        GUI.Label(new Rect(left, panel.yMax - 20f, contentWidth, 16f),
            $"{togglePanelKey} 开关面板 · 拖滑块后 8 秒内手动优先", _smallStyle);
    }

    float DrawRow(float left, float y, float width, string label, ref float value, float min, float max, string unit, int decimals)
    {
        string text = decimals > 0 ? value.ToString("F" + decimals) : value.ToString("F0");
        GUI.Label(new Rect(left, y, width, 16f), $"{label}   {text} {unit}", _labelStyle);

        float next = GUI.HorizontalSlider(new Rect(left, y + 16f, width, 12f), value, min, max);
        if (Mathf.Abs(next - value) > 1e-4f)
        {
            value = next;
            MarkManual();
        }

        return y + 34f;
    }

    void EnsureStyles()
    {
        if (_panelTexture != null)
            return;

        _panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        _panelTexture.SetPixel(0, 0, new Color(0.04f, 0.05f, 0.07f, 0.86f));
        _panelTexture.Apply();

        if (!_fontTried)
        {
            _fontTried = true;
            _cjkFont = CjkFont.Get();
        }

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = Color.white }
        };

        _valueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        _smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal = { textColor = Color.white }
        };

        if (_cjkFont != null)
        {
            _titleStyle.font = _cjkFont;
            _labelStyle.font = _cjkFont;
            _valueStyle.font = _cjkFont;
            _smallStyle.font = _cjkFont;
        }
    }

    void OnDestroy()
    {
        if (_panelTexture != null)
            Destroy(_panelTexture);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<CuttingParameters>() != null)
            return;

        GameObject machine = GameObject.Find("五轴机床");
        if (machine == null)
            return;

        machine.AddComponent<CuttingParameters>();
    }
}
