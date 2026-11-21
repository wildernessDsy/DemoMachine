using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 设备健康监测：由「时段状态 + 主轴负载 + 各轴运动」推算主轴振动、轴承温度、
/// 伺服负载、刀具寿命、累计运行时长与报警代码，并驱动机体上方的三色状态灯。
///
/// 数值不是写死的表，而是带热惯性和阻尼的仿真量，因此状态切换时有过渡过程：
/// 停机后温度缓慢下降、负载上升后振动与温度跟着爬升。
/// </summary>
[DisallowMultipleComponent]
public sealed class MachineHealth : MonoBehaviour
{
    [Header("数据来源（留空自动查找）")]
    public string machineRootName = "五轴机床";
    public MachineTimeController timeSystem;
    public CuttingParameters cutting;
    public FiveAxisPose pose;
    public PlatformController platform;

    [Header("热模型")]
    [Tooltip("环境温度（°C）。")]
    public float ambientTempC = 25f;

    [Tooltip("满载持续加工时的稳态轴承温度（°C）。")]
    public float fullLoadTempC = 75f;

    [Tooltip("温度跟随时间常数（模拟分钟），与时间倍速无关。")]
    [Min(0.5f)] public float thermalTimeConstantMinutes = 18f;

    [Tooltip("温度报警阈值（°C）。")]
    public float tempAlarmC = 66f;

    [Header("振动模型")]
    [Tooltip("空载基础振动（mm/s RMS）。")]
    public float baseVibration = 0.3f;

    [Tooltip("满载增加的振动（mm/s RMS）。")]
    public float loadVibration = 4.5f;

    [Tooltip("振动报警阈值（mm/s RMS）。")]
    public float vibrationAlarm = 3.5f;

    [Tooltip("振动跟随时间常数（模拟分钟），与时间倍速无关。")]
    [Min(0.05f)] public float vibrationTimeConstantMinutes = 1.2f;

    [Header("刀具寿命")]
    [Tooltip("满载持续加工把刀具从 100% 用到 0% 所需的模拟时间（分钟）。")]
    [Min(1f)] public float toolLifeSimMinutes = 80f;

    [Tooltip("寿命低于该百分比触发换刀提醒（%）。")]
    public float toolLifeAlarmPercent = 15f;

    [Header("面板")]
    public bool showPanel = true;
    public Key togglePanelKey = Key.H;
    public float panelWidth = 430f;
    public float panelHeight = 340f;

    [Tooltip("面板顶部位置（像素）。默认放在左上提示条与生产 OEE 面板下方。")]
    public float panelTop = 192f;

    [Header("状态灯")]
    public bool showStatusLamp = true;
    public float lampRadius = 0.055f;
    public Vector3 lampOffset = new Vector3(0f, 0.18f, 0f);

    // ---------------- 仿真量 ----------------
    /// <summary>轴承温度 °C。</summary>
    public float BearingTempC { get; private set; }

    /// <summary>主轴振动 mm/s RMS。</summary>
    public float VibrationRms { get; private set; }

    /// <summary>主轴伺服负载 %（等于切削负载）。</summary>
    public float SpindleServoLoad { get; private set; }

    /// <summary>各轴伺服负载 %：X, Y, Z, A, C。</summary>
    public float[] AxisServoLoad { get; } = new float[5];

    /// <summary>刀具寿命剩余 %。</summary>
    public float ToolLifePercent { get; private set; } = 100f;

    /// <summary>累计运行时长（小时，按模拟时间累计）。</summary>
    public float RuntimeHours { get; private set; }

    /// <summary>当前报警代码列表。</summary>
    public List<string> Alarms { get; } = new List<string>();

    MachineTimeController _time;
    CuttingParameters _cutting;
    FiveAxisPose _pose;
    PlatformController _platform;

    float _lastSimMinutes;
    int _lastToolNumber = -1;
    Transform _lamp;
    Material _lampMaterial;

    readonly StringBuilder _sb = new StringBuilder(256);

    Texture2D _panelTexture;
    GUIStyle _titleStyle;
    GUIStyle _labelStyle;
    GUIStyle _valueStyle;
    GUIStyle _smallStyle;
    GUIStyle _stateStyle;
    static Font _cjkFont;
    static bool _fontTried;

    void Awake()
    {
        Binding();
    }

    void Start()
    {
        BearingTempC = ambientTempC;
        VibrationRms = baseVibration;
        _lastSimMinutes = _time != null ? _time.CurrentMinutes : 0f;

        if (showStatusLamp)
            BuildStatusLamp();

        if (_time == null)
            Debug.LogWarning("[MachineHealth] 未找到 MachineTimeController，健康数值将按实时时钟估算。", this);
    }

    void Binding()
    {
        if (_time == null) _time = timeSystem != null ? timeSystem : FindAnyObjectByType<MachineTimeController>();
        if (_cutting == null) _cutting = cutting != null ? cutting : FindAnyObjectByType<CuttingParameters>();
        if (_pose == null) _pose = pose != null ? pose : FindAnyObjectByType<FiveAxisPose>();
        if (_platform == null) _platform = platform != null ? platform : FindAnyObjectByType<PlatformController>();
    }

    void Update()
    {
        Binding();

        float dtReal = Mathf.Max(Time.deltaTime, 1e-4f);

        bool machining = _time != null ? _time.IsMachining : false;
        bool warmup = _time != null && _time.CurrentStateKey == "warmup";
        float load = _cutting != null ? _cutting.LoadPercent : 0f;

        // ---- 先算这帧推进的模拟分钟数（跨零点循环）----
        float deltaMinutes = 0f;
        if (_time != null)
        {
            float minutes = _time.CurrentMinutes;
            deltaMinutes = minutes - _lastSimMinutes;
            if (deltaMinutes < 0f)
                deltaMinutes += 24f * 60f;
            _lastSimMinutes = minutes;
        }
        else
        {
            deltaMinutes = dtReal / 60f;   // 没有模拟时钟时退回真实分钟
        }

        // ---- 振动：按模拟时间做一阶跟随 ----
        float vibrationTarget = baseVibration
            + loadVibration * Mathf.Clamp01(load / 100f)
            + (machining ? 0.15f : 0f);

        VibrationRms = FollowExp(VibrationRms, vibrationTarget, deltaMinutes, vibrationTimeConstantMinutes);

        // ---- 温度：按模拟时间做一阶热惯性 ----
        float runningFraction = machining ? Mathf.Lerp(0.55f, 1f, Mathf.Clamp01(load / 100f))
                              : warmup ? 0.25f
                              : 0f;
        float tempTarget = Mathf.Lerp(ambientTempC, fullLoadTempC, Mathf.Clamp01(runningFraction));

        BearingTempC = FollowExp(BearingTempC, tempTarget, deltaMinutes, thermalTimeConstantMinutes);

        // ---- 主轴伺服负载：用真实时间跟随，保证画面响应 ----
        SpindleServoLoad = Mathf.Lerp(SpindleServoLoad, load, 1f - Mathf.Exp(-dtReal / 0.6f));

        // ---- 各轴伺服负载 ----
        float platformSpeed = _platform != null ? _platform.speed : 0f;         // m/s
        float linearLoad = Mathf.Clamp01(platformSpeed / 0.3f) * 62f;            // 0~62 %
        float rotarySpeed = 0f;
        if (_pose != null && _pose.IsReady)
        {
            // 用目标角速度估算回转轴负载（受 maxAngularSpeed 限制）
            rotarySpeed = Mathf.Max(
                Mathf.Abs(_pose.TargetADeg - _pose.CurrentADeg),
                Mathf.Abs(_pose.TargetCDeg - _pose.CurrentCDeg));
        }

        float rotaryLoad = Mathf.Clamp01(rotarySpeed / 30f) * 55f;

        float targetXyz = machining ? linearLoad : linearLoad * 0.25f;
        float targetAc = machining ? rotaryLoad : rotaryLoad * 0.25f;

        float axisAlpha = 1f - Mathf.Exp(-dtReal / 0.5f);
        AxisServoLoad[0] = Mathf.Lerp(AxisServoLoad[0], targetXyz, axisAlpha);
        AxisServoLoad[1] = Mathf.Lerp(AxisServoLoad[1], targetXyz, axisAlpha);
        AxisServoLoad[2] = Mathf.Lerp(AxisServoLoad[2], targetXyz * 1.1f, axisAlpha);
        AxisServoLoad[3] = Mathf.Lerp(AxisServoLoad[3], targetAc, axisAlpha);
        AxisServoLoad[4] = Mathf.Lerp(AxisServoLoad[4], targetAc * 0.8f, axisAlpha);

        // ---- 刀具寿命：按模拟时间消耗 ----
        if (_cutting != null)
        {
            if (_lastToolNumber < 0)
                _lastToolNumber = _cutting.toolNumber;
            else if (_cutting.toolNumber != _lastToolNumber)
            {
                // 换刀 → 寿命复位
                _lastToolNumber = _cutting.toolNumber;
                ToolLifePercent = 100f;
            }
        }

        if (_time != null)
        {
            if (machining)
            {
                RuntimeHours += deltaMinutes / 60f;

                float wearPerMinute = 100f / Mathf.Max(1f, toolLifeSimMinutes) * Mathf.Clamp01(load / 100f);
                ToolLifePercent = Mathf.Max(0f, ToolLifePercent - wearPerMinute * deltaMinutes);
            }
        }

        UpdateAlarms();
        UpdateLamp();

        if (Keyboard.current != null && Keyboard.current[togglePanelKey].wasPressedThisFrame)
        {
            showPanel = !showPanel;
            if (showPanel)
                _panelTexture = null;   // 重新生成样式
        }
    }

    static float FollowExp(float current, float target, float deltaMinutes, float timeConstantMinutes)
    {
        if (deltaMinutes <= 0f)
            return current;

        float alpha = 1f - Mathf.Exp(-deltaMinutes / Mathf.Max(1e-4f, timeConstantMinutes));
        return current + (target - current) * Mathf.Clamp01(alpha);
    }

    void UpdateAlarms()
    {
        Alarms.Clear();

        if (BearingTempC >= tempAlarmC)
            Alarms.Add($"ALM-201 主轴轴承过热 {BearingTempC:F0}°C");
        if (VibrationRms >= vibrationAlarm)
            Alarms.Add($"ALM-301 主轴振动超限 {VibrationRms:F1} mm/s");
        if (SpindleServoLoad > 100f)
            Alarms.Add($"ALM-401 主轴过载 {SpindleServoLoad:F0}%");
        if (ToolLifePercent <= toolLifeAlarmPercent)
            Alarms.Add($"ALM-501 刀具寿命不足 {ToolLifePercent:F0}%");
        if (_time != null && _time.CurrentStateKey == "maintenance")
            Alarms.Add("ALM-601 设备处于维护模式");
    }

    // ---------------- 状态灯 ----------------

    void BuildStatusLamp()
    {
        GameObject machine = GameObject.Find(machineRootName);
        if (machine == null)
            return;

        bool hasBounds = false;
        var bounds = new Bounds(machine.transform.position, Vector3.zero);
        foreach (var r in machine.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null) continue;
            if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
            else bounds.Encapsulate(r.bounds);
        }

        Vector3 position = hasBounds
            ? new Vector3(bounds.center.x, bounds.max.y, bounds.center.z)
            : machine.transform.position + Vector3.up * 2f;

        GameObject lamp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        lamp.name = "状态灯";
        lamp.transform.SetParent(machine.transform, true);
        lamp.transform.position = position + lampOffset;
        lamp.transform.localScale = Vector3.one * (lampRadius * 2f);

        var collider = lamp.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader == null)
            shader = Shader.Find("Standard");

        _lampMaterial = new Material(shader) { name = "StatusLampRuntime" };
        var renderer = lamp.GetComponent<MeshRenderer>();
        if (renderer != null)
            renderer.sharedMaterial = _lampMaterial;

        _lamp = lamp.transform;
    }

    void UpdateLamp()
    {
        if (_lampMaterial == null)
            return;

        Color color = StateColor();
        if (_lampMaterial.HasProperty("_BaseColor"))
            _lampMaterial.SetColor("_BaseColor", color);
        if (_lampMaterial.HasProperty("_Color"))
            _lampMaterial.SetColor("_Color", color);
    }

    Color StateColor()
    {
        string state = _time != null ? _time.CurrentStateKey : "";

        switch (state)
        {
            case "running": return new Color(0.25f, 0.95f, 0.45f);
            case "warmup": return new Color(1f, 0.78f, 0.25f);
            case "paused": return new Color(1f, 0.62f, 0.15f);
            case "maintenance": return new Color(1f, 0.28f, 0.28f);
            default: return new Color(0.55f, 0.6f, 0.68f);
        }
    }

    void OnDestroy()
    {
        if (_panelTexture != null)
            Destroy(_panelTexture);
        if (_lampMaterial != null)
            Destroy(_lampMaterial);
    }

    // ---------------- 面板 ----------------

    void OnGUI()
    {
        if (!showPanel)
            return;

        EnsureStyles();

        float width = panelWidth;
        float height = panelHeight;
        float x = 16f;

        // 固定排在左上提示条 / 生产 OEE 面板下方
        float timePanelTop = Screen.height - 336f;
        float y = Mathf.Clamp(panelTop, 84f, Mathf.Max(84f, timePanelTop - height - 8f));

        var panel = new Rect(x, y, width, height);

        Color old = GUI.color;
        GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.86f);
        GUI.DrawTexture(panel, Texture2D.whiteTexture);
        GUI.color = old;

        float left = panel.x + 16f;
        float contentWidth = panel.width - 32f;
        float top = panel.y + 10f;

        GUI.Label(new Rect(left, top, contentWidth - 150f, 18f), "设备健康监测", _titleStyle);

        // 状态徽标
        string stateName = _time != null ? _time.CurrentStateChinese : "未知";
        Rect badge = new Rect(left + contentWidth - 118f, top + 1f, 118f, 18f);
        GUI.color = StateColor();
        GUI.DrawTexture(badge, Texture2D.whiteTexture);
        GUI.color = old;
        _stateStyle.normal.textColor = new Color(0.06f, 0.07f, 0.09f);
        GUI.Label(badge, stateName, _stateStyle);

        float y2 = top + 26f;

        y2 = DrawGauge(left, y2, contentWidth, "主轴振动", VibrationRms, 0f, vibrationAlarm * 1.4f, "mm/s", "F2", vibrationAlarm);
        y2 = DrawGauge(left, y2, contentWidth, "轴承温度", BearingTempC, ambientTempC, fullLoadTempC * 1.1f, "°C", "F1", tempAlarmC);
        y2 = DrawGauge(left, y2, contentWidth, "主轴负载", SpindleServoLoad, 0f, 120f, "%", "F0", 100f);

        // 五轴伺服负载
        GUI.Label(new Rect(left, y2, contentWidth, 16f), "各轴伺服负载", _labelStyle);
        y2 += 18f;

        string[] names = { "X", "Y", "Z", "A", "C" };
        float cellGap = 6f;
        float cellWidth = (contentWidth - cellGap * 4f) / 5f;

        for (int i = 0; i < 5; i++)
        {
            var cell = new Rect(left + i * (cellWidth + cellGap), y2, cellWidth, 34f);

            GUI.color = new Color(1f, 1f, 1f, 0.14f);
            GUI.DrawTexture(new Rect(cell.x, cell.y + 14f, cell.width, 8f), Texture2D.whiteTexture);

            float v = Mathf.Clamp01(AxisServoLoad[i] / 100f);
            GUI.color = v >= 0.85f ? new Color(1f, 0.35f, 0.3f)
                      : v >= 0.65f ? new Color(1f, 0.75f, 0.3f)
                      : new Color(0.35f, 0.85f, 0.55f);
            GUI.DrawTexture(new Rect(cell.x, cell.y + 14f, cell.width * v, 8f), Texture2D.whiteTexture);
            GUI.color = old;

            GUI.Label(new Rect(cell.x, cell.y - 2f, cell.width, 15f), names[i], _smallStyle);
            GUI.Label(new Rect(cell.x, cell.y + 23f, cell.width, 14f), $"{AxisServoLoad[i]:F0}", _smallStyle);
        }

        y2 += 40f;

        // 刀具寿命 + 运行时长
        float lifeFrac = Mathf.Clamp01(ToolLifePercent / 100f);
        GUI.color = new Color(1f, 1f, 1f, 0.14f);
        GUI.DrawTexture(new Rect(left, y2 + 15f, contentWidth, 8f), Texture2D.whiteTexture);
        GUI.color = lifeFrac <= toolLifeAlarmPercent / 100f ? new Color(1f, 0.35f, 0.3f)
                  : lifeFrac <= 0.4f ? new Color(1f, 0.75f, 0.3f)
                  : new Color(0.35f, 0.85f, 0.55f);
        GUI.DrawTexture(new Rect(left, y2 + 15f, contentWidth * lifeFrac, 8f), Texture2D.whiteTexture);
        GUI.color = old;

        string toolText = _cutting != null ? $"T{_cutting.toolNumber:00}" : "T--";
        GUI.Label(new Rect(left, y2 - 2f, contentWidth, 16f),
            $"刀具寿命  {ToolLifePercent:F1}%   ({toolText})", _labelStyle);
        y2 += 28f;

        GUI.Label(new Rect(left, y2, contentWidth, 16f),
            $"累计运行  {RuntimeHours:F2} h   ·   报警 {Alarms.Count} 条", _labelStyle);
        y2 += 18f;

        if (Alarms.Count > 0)
        {
            int shown = 0;
            for (int i = 0; i < Alarms.Count && shown < 2; i++, shown++)
            {
                GUI.Label(new Rect(left, y2, contentWidth, 16f), "● " + Alarms[i], _alarmStyle);
                y2 += 16f;
            }

            if (Alarms.Count > shown)
                GUI.Label(new Rect(left, y2, contentWidth, 16f), $"   另有 {Alarms.Count - shown} 条报警", _smallStyle);
        }
        else
        {
            GUI.Label(new Rect(left, y2, contentWidth, 16f), "● 无报警", _smallStyle);
        }

        GUI.Label(new Rect(left, panel.yMax - 20f, contentWidth, 16f),
            $"{togglePanelKey} 开关面板 · 停机后温度缓慢回落", _smallStyle);
    }

    GUIStyle _alarmStyle;

    float DrawGauge(float left, float y, float width, string label, float value, float min, float max, string unit, string format, float alarmAt)
    {
        GUI.Label(new Rect(left, y, width, 16f), $"{label}   {value.ToString(format)} {unit}", _labelStyle);

        float frac = Mathf.InverseLerp(min, max, value);
        float alarmFrac = Mathf.InverseLerp(min, max, alarmAt);

        GUI.color = new Color(1f, 1f, 1f, 0.14f);
        GUI.DrawTexture(new Rect(left, y + 17f, width, 8f), Texture2D.whiteTexture);

        GUI.color = frac >= alarmFrac ? new Color(1f, 0.35f, 0.3f)
                  : frac >= alarmFrac * 0.8f ? new Color(1f, 0.75f, 0.3f)
                  : new Color(0.35f, 0.85f, 0.55f);
        GUI.DrawTexture(new Rect(left, y + 17f, width * Mathf.Clamp01(frac), 8f), Texture2D.whiteTexture);
        GUI.color = Color.white;

        return y + 30f;
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

        _stateStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        _alarmStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal = { textColor = new Color(1f, 0.45f, 0.4f) }
        };

        if (_cjkFont != null)
        {
            _titleStyle.font = _cjkFont;
            _labelStyle.font = _cjkFont;
            _valueStyle.font = _cjkFont;
            _smallStyle.font = _cjkFont;
            _stateStyle.font = _cjkFont;
            _alarmStyle.font = _cjkFont;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<MachineHealth>() != null)
            return;

        var machine = GameObject.Find("五轴机床");
        if (machine != null)
            machine.AddComponent<MachineHealth>();
    }
}
