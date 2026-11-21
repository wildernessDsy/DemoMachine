using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 生产 OEE（设备综合效率）：
///   稼动率 A = 运行时间 / 计划时间
///   性能   P = 理想循环时间 / 实际循环时间
///   良品率 Q = 良品数 / 总产出
///   OEE   = A × P × Q
///
/// 全部按「模拟时间」累计，与时间倍速无关：
/// 实际节拍受刀具磨损与报警拖慢，因此性能会随寿命下降而降低；
/// 完工时若存在报警或刀具寿命过低，则该件判为不良。
/// </summary>
[DisallowMultipleComponent]
public sealed class MachineOee : MonoBehaviour
{
    [Header("数据来源（留空自动查找）")]
    public MachineTimeController timeSystem;
    public CuttingParameters cutting;
    public MachineHealth health;

    [Header("能耗模型")]
    [Tooltip("待机功率（kW）：液压、冷却、控制柜等常开负载。")]
    [Min(0f)] public float standbyPowerKw = 2.5f;

    [Header("性能损失")]
    [Tooltip("刀具全新时的节拍损失（0.06 = 慢 6%）。")]
    [Range(0f, 0.5f)] public float baseSlowdown = 0.06f;

    [Tooltip("刀具寿命耗尽时额外增加的节拍损失。")]
    [Range(0f, 1f)] public float wearSlowdown = 0.18f;

    [Tooltip("存在报警时额外增加的节拍损失。")]
    [Range(0f, 0.5f)] public float alarmSlowdown = 0.05f;

    [Header("质量判定")]
    [Tooltip("刀具寿命低于该百分比时，产出的零件判为不良（%）。")]
    public float scrapToolLifePercent = 20f;

    [Tooltip("主轴负载超过该百分比（过载）时，产出的零件判为不良（%）。" +
             "温度与振动属于预警，只拖慢节拍、不判废。")]
    public float scrapOverloadPercent = 100f;

    [Header("面板")]
    public bool showPanel = true;
    public Key togglePanelKey = Key.O;
    public float panelWidth = 430f;
    public float panelHeight = 100f;
    public float panelTop = 84f;

    // ---------------- 累计量 ----------------
    /// <summary>计划生产时间（分钟）：只统计 running + paused，待机与维护属计划停机，不计入分母。</summary>
    public float PlannedMinutes { get; private set; }

    /// <summary>实际加工时间（分钟）。</summary>
    public float RunMinutes { get; private set; }

    /// <summary>计划停机时间（暂停等）。</summary>
    public float DownMinutes => Mathf.Max(0f, PlannedMinutes - RunMinutes);

    public int TotalCount { get; private set; }
    public int GoodCount { get; private set; }

    public float EnergyKwh { get; private set; }

    /// <summary>稼动率 %。</summary>
    public float Availability => PlannedMinutes > 0.01f ? Mathf.Clamp01(RunMinutes / PlannedMinutes) * 100f : 0f;

    /// <summary>性能 %（理想节拍 / 实际节拍）。</summary>
    public float Performance => Mathf.Clamp01(1f / (1f + CurrentSlowdown)) * 100f;

    /// <summary>良品率 %。</summary>
    public float Quality => TotalCount > 0 ? Mathf.Clamp01((float)GoodCount / TotalCount) * 100f : 100f;

    /// <summary>OEE %。</summary>
    public float Oee => Availability / 100f * Performance / 100f * Quality / 100f * 100f;

    public float EnergyPerPart => TotalCount > 0 ? EnergyKwh / TotalCount : 0f;

    /// <summary>当前节拍损失比例（含磨损与报警）。</summary>
    public float CurrentSlowdown
    {
        get
        {
            float wear = 1f - Mathf.Clamp01(ToolLife01);
            float slowdown = baseSlowdown + wearSlowdown * wear;
            if (_health != null && _health.Alarms.Count > 0)
                slowdown += alarmSlowdown;
            return slowdown;
        }
    }

    float ToolLife01 => _health != null ? Mathf.Clamp01(_health.ToolLifePercent / 100f) : 1f;

    MachineTimeController _time;
    CuttingParameters _cutting;
    MachineHealth _health;

    float _lastSimMinutes;
    float _partProgress;

    Texture2D _panelTexture;
    GUIStyle _titleStyle;
    GUIStyle _bigStyle;
    GUIStyle _labelStyle;
    GUIStyle _smallStyle;
    GUIStyle _buttonStyle;
    static Font _cjkFont;
    static bool _fontTried;

    void Awake()
    {
        Binding();
    }

    void Start()
    {
        _lastSimMinutes = _time != null ? _time.CurrentMinutes : 0f;
    }

    void Binding()
    {
        if (_time == null) _time = timeSystem != null ? timeSystem : FindAnyObjectByType<MachineTimeController>();
        if (_cutting == null) _cutting = cutting != null ? cutting : FindAnyObjectByType<CuttingParameters>();
        if (_health == null) _health = health != null ? health : FindAnyObjectByType<MachineHealth>();
    }

    void Update()
    {
        Binding();

        // 推进的模拟分钟数（跨零点循环）
        float deltaMinutes;
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
            deltaMinutes = Time.deltaTime / 60f;
        }

        if (deltaMinutes <= 0f)
            return;

        bool machining = _time != null && _time.IsMachining;
        string stateKey = _time != null ? _time.CurrentStateKey : "";
        bool scheduled = stateKey == "running" || stateKey == "paused";
        float deltaSeconds = deltaMinutes * 60f;

        // ---- 时间统计：只有 running / paused 计入计划生产时间 ----
        if (scheduled)
            PlannedMinutes += deltaMinutes;
        if (machining)
            RunMinutes += deltaMinutes;

        // ---- 能耗：待机功率 + 主轴功率 ----
        float powerKw = standbyPowerKw;
        if (_cutting != null)
            powerKw += _cutting.PowerKw;
        EnergyKwh += powerKw * deltaMinutes / 60f;

        // ---- 产量 ----
        if (machining)
        {
            MachineTimeSlot slot = _time != null ? _time.CurrentSlotData : null;
            float idealCycle = slot != null ? Mathf.Max(1f, slot.cycleTimeSeconds) : 150f;
            float actualCycle = idealCycle * (1f + CurrentSlowdown);

            _partProgress += deltaSeconds / actualCycle;

            while (_partProgress >= 1f)
            {
                _partProgress -= 1f;
                TotalCount++;

                bool bad = false;
                if (_health != null)
                {
                    if (_health.ToolLifePercent <= scrapToolLifePercent)
                        bad = true;
                    if (_health.SpindleServoLoad > scrapOverloadPercent)
                        bad = true;
                }

                if (!bad)
                    GoodCount++;
            }
        }

        if (Keyboard.current != null && Keyboard.current[togglePanelKey].wasPressedThisFrame)
        {
            showPanel = !showPanel;
            if (showPanel)
                _panelTexture = null;
        }
    }

    /// <summary>清零所有累计量。</summary>
    [ContextMenu("重置统计")]
    public void ResetStatistics()
    {
        PlannedMinutes = 0f;
        RunMinutes = 0f;
        TotalCount = 0;
        GoodCount = 0;
        EnergyKwh = 0f;
        _partProgress = 0f;
        _lastSimMinutes = _time != null ? _time.CurrentMinutes : 0f;
    }

    void OnDestroy()
    {
        if (_panelTexture != null)
            Destroy(_panelTexture);
    }

    void OnGUI()
    {
        if (!showPanel)
            return;

        EnsureStyles();

        float width = panelWidth;
        float height = panelHeight;
        var panel = new Rect(16f, panelTop, width, height);

        Color old = GUI.color;
        GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.86f);
        GUI.DrawTexture(panel, Texture2D.whiteTexture);
        GUI.color = old;

        float left = panel.x + 16f;
        float contentWidth = panel.width - 32f;
        float top = panel.y + 8f;

        GUI.Label(new Rect(left, top, contentWidth - 70f, 18f), "生产 OEE", _titleStyle);

        if (GUI.Button(new Rect(panel.xMax - 72f, top - 2f, 56f, 22f), "重置", _buttonStyle))
            ResetStatistics();

        GUI.Label(new Rect(left, top + 18f, 150f, 34f), $"{Oee:F1}%", _bigStyle);

        // 三大分项
        float statX = left + 150f;
        float statWidth = (contentWidth - 150f) / 3f;
        DrawStat(statX, top + 20f, statWidth, "稼动率", Availability);
        DrawStat(statX + statWidth, top + 20f, statWidth, "性能", Performance);
        DrawStat(statX + statWidth * 2f, top + 20f, statWidth, "良品率", Quality);

        GUI.Label(new Rect(left, top + 58f, contentWidth, 16f),
            $"产出 {GoodCount}/{TotalCount} 件   ·   累计 {EnergyKwh:F1} kWh   ·   单件 {EnergyPerPart:F2} kWh",
            _smallStyle);

        GUI.Label(new Rect(left, top + 74f, contentWidth, 16f),
            $"运行 {RunMinutes / 60f:F1}h / 计划 {PlannedMinutes / 60f:F1}h   ·   {togglePanelKey} 开关面板",
            _smallStyle);    }

    void DrawStat(float x, float y, float width, string label, float percent)
    {
        GUI.Label(new Rect(x, y, width, 16f), label, _smallStyle);
        GUI.Label(new Rect(x, y + 15f, width, 18f), $"{percent:F1}%", _labelStyle);

        var bar = new Rect(x, y + 34f, width - 8f, 5f);
        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.14f);
        GUI.DrawTexture(bar, Texture2D.whiteTexture);

        float v = Mathf.Clamp01(percent / 100f);
        GUI.color = v >= 0.85f ? new Color(0.35f, 0.85f, 0.55f)
                  : v >= 0.65f ? new Color(1f, 0.75f, 0.3f)
                  : new Color(1f, 0.4f, 0.35f);
        GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * v, bar.height), Texture2D.whiteTexture);
        GUI.color = old;
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
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        _bigStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 28,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = Color.white }
        };

        _smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal = { textColor = Color.white }
        };

        _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 11 };
        _buttonStyle.normal.textColor = Color.white;
        _buttonStyle.hover.textColor = Color.white;
        _buttonStyle.active.textColor = Color.white;

        if (_cjkFont != null)
        {
            _titleStyle.font = _cjkFont;
            _bigStyle.font = _cjkFont;
            _labelStyle.font = _cjkFont;
            _smallStyle.font = _cjkFont;
            _buttonStyle.font = _cjkFont;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<MachineOee>() != null)
            return;

        var host = FindAnyObjectByType<MachineHealth>();
        if (host != null)
        {
            host.gameObject.AddComponent<MachineOee>();
            return;
        }

        var machine = GameObject.Find("五轴机床");
        if (machine != null)
            machine.AddComponent<MachineOee>();
    }
}
