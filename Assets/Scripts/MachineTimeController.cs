using System;
using UnityEngine;

/// <summary>
/// 模拟时间 + 数据库驱动：
/// - 24 小时循环，默认 08:00 开始，1 现实秒 = 10 模拟分钟；
/// - 按当前时间读取 15 分钟时段，并把三个参数发给对应面板；
/// - 用户刚拖过滑块时，该参数在 8 秒内保持手动值。
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)]
public sealed class MachineTimeController : MonoBehaviour
{
    [Header("模拟时钟")]
    [Range(0f, 24f)] public float startHour = 8f;
    [Min(0.1f)] public float minutesPerRealSecond = 10f;
    public bool loop = true;
    public bool interpolateBetweenSlots = true;
    public bool showClock = true;

    MachineTimeSlot[] _slots;
    string _sourcePath;
    float _minutes;
    int _currentSlot = -1;
    string _currentState = "";
    bool _paused;
    bool _loaded;

    PlatformController _platform;
    SpindleReciprocate _spindle;
    DrillController _drill;

    GUIStyle _panelStyle;
    GUIStyle _titleStyle;
    GUIStyle _timeStyle;
    GUIStyle _labelStyle;
    GUIStyle _smallStyle;
    GUIStyle _buttonStyle;
    GUIStyle _manualStyle;
    Texture2D _panelTexture;

    static Font _cjkFont;
    static bool _fontTried;

    public float CurrentMinutes => _minutes;
    public int CurrentSlot => _currentSlot;
    public bool IsPaused => _paused;

    void Awake()
    {
        _minutes = Mathf.Repeat(startHour, 24f) * 60f;
        _slots = MachineTimeData.Load(out _sourcePath);
        _loaded = _slots != null && _slots.Length == MachineTimeData.SlotsPerDay;

        if (!_loaded)
            Debug.LogError("[MachineTime] 数据未就绪，时钟仍会运行，但不会驱动参数。");
    }

    void Start()
    {
        ResolveComponents();
        ApplyCurrentSlot();

        Debug.Log($"[MachineTime] start={FormatTime(_minutes)} speed={minutesPerRealSecond:0.#}x " +
                  $"slots={(_slots != null ? _slots.Length : 0)} source={_sourcePath}");
    }

    void Update()
    {
        ResolveComponents();

        if (!_paused)
        {
            _minutes += Time.deltaTime * minutesPerRealSecond;
            if (loop)
                _minutes = Mathf.Repeat(_minutes, 24f * 60f);
            else
                _minutes = Mathf.Clamp(_minutes, 0f, 24f * 60f - 0.001f);
        }

        ApplyCurrentSlot();
    }

    void ResolveComponents()
    {
        if (_platform == null)
            _platform = UnityEngine.Object.FindAnyObjectByType<PlatformController>();
        if (_spindle == null)
            _spindle = UnityEngine.Object.FindAnyObjectByType<SpindleReciprocate>();
        if (_drill == null)
            _drill = UnityEngine.Object.FindAnyObjectByType<DrillController>();
    }

    void ApplyCurrentSlot()
    {
        if (!_loaded || _slots == null || _slots.Length < MachineTimeData.SlotsPerDay)
            return;

        float minutes = Mathf.Clamp(_minutes, 0f, 24f * 60f - 0.001f);
        int slot = Mathf.Clamp(
            Mathf.FloorToInt(minutes / MachineTimeData.SlotMinutes),
            0, MachineTimeData.SlotsPerDay - 1);
        int next = (slot + 1) % MachineTimeData.SlotsPerDay;

        float t = interpolateBetweenSlots
            ? (minutes - slot * MachineTimeData.SlotMinutes) / MachineTimeData.SlotMinutes
            : 0f;

        MachineTimeSlot a = _slots[slot];
        MachineTimeSlot b = _slots[next];

        float worktable = Mathf.Lerp(a.worktableSpeedMms, b.worktableSpeedMms, t);
        float spindle = Mathf.Lerp(a.spindleReciprocatingSpeedMms, b.spindleReciprocatingSpeedMms, t);
        float drill = Mathf.Lerp(a.drillRpm, b.drillRpm, t);

        if (_platform != null && !_platform.IsSpeedManual)
            _platform.SetExternalSpeedMms(worktable);
        if (_spindle != null && !_spindle.IsSpeedManual)
            _spindle.SetExternalSpeedMms(spindle);
        if (_drill != null && !_drill.IsRpmManual)
            _drill.SetExternalRpm(drill);

        _currentSlot = slot;
        _currentState = a.machineState;
    }

    bool AnyManual =>
        (_platform != null && _platform.IsSpeedManual) ||
        (_spindle != null && _spindle.IsSpeedManual) ||
        (_drill != null && _drill.IsRpmManual);

    string FormatTime(float minutes)
    {
        int total = Mathf.FloorToInt(Mathf.Repeat(minutes, 24f * 60f));
        return $"{total / 60:00}:{total % 60:00}";
    }

    string StateChinese(string state)
    {
        switch (state)
        {
            case "warmup": return "升温";
            case "running": return "加工";
            case "paused": return "暂停";
            case "maintenance": return "维护";
            default: return "待机";
        }
    }

    void OnGUI()
    {
        if (!showClock)
            return;

        EnsureStyles();

        const float width = 310f;
        const float height = 160f;
        var panel = new Rect(16f, Screen.height - height - 16f, width, height);
        GUI.Box(panel, GUIContent.none, _panelStyle);

        float left = panel.x + 16f;
        float top = panel.y + 10f;
        float contentWidth = panel.width - 32f;

        GUI.Label(new Rect(left, top, contentWidth, 18f), "模拟时间 / 数据库驱动", _titleStyle);

        string timeText = FormatTime(_minutes);
        GUI.Label(new Rect(left, top + 20f, 120f, 40f), timeText, _timeStyle);

        string slotText = _currentSlot >= 0 && _slots != null && _slots.Length > _currentSlot
            ? _slots[_currentSlot].timeSlot
            : "--:--";
        GUI.Label(new Rect(left + 132f, top + 24f, contentWidth - 132f, 18f),
            $"数据库时段 {slotText}", _smallStyle);
        GUI.Label(new Rect(left + 132f, top + 42f, contentWidth - 132f, 18f),
            StateChinese(_currentState), _smallStyle);

        GUI.Label(new Rect(left, top + 62f, 120f, 18f),
            $"时间倍速 ×{minutesPerRealSecond:0.#}", _smallStyle);

        if (AnyManual)
            GUI.Label(new Rect(left + 130f, top + 62f, contentWidth - 130f, 18f),
                "手动优先中", _manualStyle);
        else
            GUI.Label(new Rect(left + 130f, top + 62f, contentWidth - 130f, 18f),
                "数据库跟随中", _smallStyle);

        float buttonY = top + 92f;
        if (GUI.Button(new Rect(left, buttonY, 62f, 24f), _paused ? "继续" : "暂停", _buttonStyle))
            _paused = !_paused;

        if (GUI.Button(new Rect(left + 70f, buttonY, 52f, 24f), "×1", _buttonStyle))
            minutesPerRealSecond = 1f;
        if (GUI.Button(new Rect(left + 128f, buttonY, 52f, 24f), "×10", _buttonStyle))
            minutesPerRealSecond = 10f;
        if (GUI.Button(new Rect(left + 186f, buttonY, 52f, 24f), "×60", _buttonStyle))
            minutesPerRealSecond = 60f;

        GUI.Label(new Rect(left, panel.yMax - 22f, contentWidth, 16f),
            "96 段 × 15 分钟 · 23:45 → 00:00 循环", _smallStyle);
    }

    void EnsureStyles()
    {
        if (_panelStyle != null)
            return;

        _panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        _panelTexture.SetPixel(0, 0, new Color(0.04f, 0.05f, 0.07f, 0.82f));
        _panelTexture.Apply();

        Font font = GetCjkFont();

        _panelStyle = new GUIStyle
        {
            normal = { background = _panelTexture },
            border = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(0, 0, 0, 0),
        };

        _titleStyle = new GUIStyle
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.85f, 0.9f, 1f) },
        };
        _timeStyle = new GUIStyle
        {
            fontSize = 30,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.55f, 0.9f, 1f) },
        };
        _labelStyle = new GUIStyle
        {
            fontSize = 12,
            normal = { textColor = new Color(0.72f, 0.78f, 0.86f) },
        };
        _smallStyle = new GUIStyle
        {
            fontSize = 11,
            normal = { textColor = new Color(0.65f, 0.72f, 0.8f) },
        };
        _manualStyle = new GUIStyle
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(1f, 0.75f, 0.3f) },
        };
        _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 11 };

        if (font != null)
        {
            _titleStyle.font = font;
            _timeStyle.font = font;
            _labelStyle.font = font;
            _smallStyle.font = font;
            _manualStyle.font = font;
            _buttonStyle.font = font;
        }
    }

    static Font GetCjkFont()
    {
        if (_fontTried)
            return _cjkFont;
        _fontTried = true;

        string[] names = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" };
        foreach (string name in names)
        {
            try
            {
                Font font = Font.CreateDynamicFontFromOSFont(name, 16);
                if (font != null)
                {
                    _cjkFont = font;
                    break;
                }
            }
            catch
            {
                // try next font
            }
        }
        return _cjkFont;
    }

    void OnDestroy()
    {
        if (_panelTexture != null)
            Destroy(_panelTexture);
    }
}

/// <summary>场景里没有 MachineTimeSystem 时的运行时兜底。</summary>
public static class MachineTimeBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureTimeSystem()
    {
        if (UnityEngine.Object.FindAnyObjectByType<MachineTimeController>() != null)
            return;

        var go = new GameObject("MachineTimeSystem");
        go.AddComponent<MachineTimeController>();
    }
}
