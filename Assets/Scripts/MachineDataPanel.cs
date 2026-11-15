using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// “实时参数保存”面板：把当日存档的状态直接摆在画面上，
/// 让静态演示变成“看得见在存”的动态演示。
///
/// 显示内容：
/// - 今天 / 哪台机器 / 已保存多少个 15 分钟时段；
/// - 累计采样次数、累计演示时长、最近一次写盘时间；
/// - 最近一段存档的三个实时参数；
/// - 存储位置（本地浏览器）与云端同步状态；
/// - 立即保存 / 同步云端 / 导出 JSON / 清空今日。
///
/// 快捷键 M 收起或展开。HTML 也可以调
/// `MachineDataRecorder.TogglePanel()`（无参切换）或 `SetPanelVisible("0"/"1")`。
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1020)]
public sealed class MachineDataPanel : MonoBehaviour
{
    [Header("位置")]
    public float marginLeft = 16f;
    public float marginTop = 64f;
    public float panelWidth = 344f;

    [Tooltip("窗口高度小于这个值时只画核心几行，避免挡住左下角的时钟面板。")]
    public float compactBelowHeight = 700f;

    const float NormalHeight = 216f;
    const float CompactHeight = 176f;

    bool _confirmClear;
    float _confirmClearUntil;
    string _flashText;
    float _flashUntil;
    string _lastSavedSeen = "";

    GUIStyle _titleStyle;
    GUIStyle _lineStyle;
    GUIStyle _smallStyle;
    GUIStyle _accentStyle;
    GUIStyle _warnStyle;
    GUIStyle _buttonStyle;
    static Font _cjkFont;
    static bool _fontTried;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<MachineDataPanel>() != null)
            return;

        var go = new GameObject("MachineDataPanel");
        go.AddComponent<MachineDataPanel>();
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        var recorder = MachineDataRecorder.Instance;

        if (keyboard != null && recorder != null && keyboard[Key.M].wasPressedThisFrame)
            recorder.showPanel = !recorder.showPanel;

        if (_confirmClear && Time.unscaledTime > _confirmClearUntil)
            _confirmClear = false;

        if (recorder == null)
            return;

        if (_lastSavedSeen != recorder.LastSavedAt)
        {
            _lastSavedSeen = recorder.LastSavedAt;
            if (!string.IsNullOrEmpty(_lastSavedSeen))
            {
                _flashText = "已写入本地";
                _flashUntil = Time.unscaledTime + 1.6f;
            }
        }
    }

    void OnGUI()
    {
        var recorder = MachineDataRecorder.Instance;
        if (recorder == null || !recorder.showPanel)
            return;

        EnsureStyles();

        bool compact = Screen.height < compactBelowHeight;
        float height = compact ? CompactHeight : NormalHeight;

        var panel = new Rect(marginLeft, marginTop, panelWidth, height);

        Color oldColor = GUI.color;
        GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.86f);
        GUI.DrawTexture(panel, Texture2D.whiteTexture);
        GUI.color = oldColor;

        float left = panel.x + 14f;
        float contentWidth = panel.width - 28f;
        float y = panel.y + 9f;

        GUI.Label(new Rect(left, y, contentWidth, 20f), "实时参数保存", _titleStyle);
        y += 21f;

        string date = string.IsNullOrEmpty(recorder.CurrentDate) ? MachineDayStore.Today : recorder.CurrentDate;
        string machine = recorder.MachineLabel;
        string state = recorder.IsRecording ? "记录中" : "等待选择机器";
        GUI.Label(new Rect(left, y, contentWidth, 16f), $"{date} · {machine} · {state}", _lineStyle);
        y += 17f;

        string runs = FormatDuration(recorder.RunSeconds);
        GUI.Label(new Rect(left, y, contentWidth, 16f),
            $"已保存 {recorder.SavedSlotCount}/{MachineTimeData.SlotsPerDay} 段 · {recorder.TotalSampleCount} 次采样 · {runs}",
            _lineStyle);
        y += 17f;

        if (!compact)
        {
            string savedAt = string.IsNullOrEmpty(recorder.LastSavedAt) ? "等待首次写入" : "最近写入 " + recorder.LastSavedAt;
            GUI.Label(new Rect(left, y, contentWidth, 16f), savedAt, _smallStyle);
            y += 17f;

            GUI.Label(new Rect(left, y, contentWidth, 16f), "最近存档 " + recorder.RecentSlotLine(), _smallStyle);
            y += 17f;
        }

        string cloud = MachineCloudSync.Enabled ? MachineCloudSync.Status : "本地模式";
        GUI.Label(new Rect(left, y, contentWidth, 16f), "存储 浏览器本地 · 云端 " + cloud, _smallStyle);
        y += 18f;

        if (!string.IsNullOrEmpty(recorder.LastError))
        {
            GUI.Label(new Rect(left, y, contentWidth, 16f), recorder.LastError, _warnStyle);
        }
        else if (Time.unscaledTime < _flashUntil && !string.IsNullOrEmpty(_flashText))
        {
            GUI.Label(new Rect(left, y, contentWidth, 16f), _flashText, _accentStyle);
        }
        else if (!string.IsNullOrEmpty(recorder.LastExport))
        {
            GUI.Label(new Rect(left, y, contentWidth, 16f), recorder.LastExport + " · " + MachineDataDownload.LastLocation, _smallStyle);
        }
        else if (!string.IsNullOrEmpty(recorder.LastMessage))
        {
            GUI.Label(new Rect(left, y, contentWidth, 16f), recorder.LastMessage, _smallStyle);
        }

        float buttonY = panel.yMax - 82f;
        float halfWidth = (contentWidth - 8f) * 0.5f;

        if (GUI.Button(new Rect(left, buttonY, halfWidth, 26f), "立即保存", _buttonStyle))
        {
            recorder.SaveNow();
            _flashText = "手动保存完成";
            _flashUntil = Time.unscaledTime + 1.6f;
        }

        if (GUI.Button(new Rect(left + halfWidth + 8f, buttonY, halfWidth, 26f), "同步云端", _buttonStyle))
            recorder.SyncNow();

        buttonY += 32f;

        if (GUI.Button(new Rect(left, buttonY, halfWidth, 26f), "导出 JSON", _buttonStyle))
            recorder.ExportToday();

        string clearLabel = _confirmClear ? "再点一次确认" : "清空今日";
        if (GUI.Button(new Rect(left + halfWidth + 8f, buttonY, halfWidth, 26f), clearLabel, _buttonStyle))
        {
            if (_confirmClear)
            {
                recorder.ClearToday();
                _confirmClear = false;
                _flashText = "今日存档已清空";
                _flashUntil = Time.unscaledTime + 1.6f;
            }
            else
            {
                _confirmClear = true;
                _confirmClearUntil = Time.unscaledTime + 3f;
            }
        }

        string toggleHint = recorder.showPanel ? "收起" : "展开";
        GUI.Label(new Rect(left, panel.yMax - 18f, contentWidth, 14f),
            "按 M " + toggleHint + " · 存档在浏览器里，刷新不丢", _smallStyle);
    }

    static string FormatDuration(float seconds)
    {
        if (seconds < 1f)
            return "0 秒";
        if (seconds < 60f)
            return Mathf.RoundToInt(seconds).ToString(CultureInfo.InvariantCulture) + " 秒";

        int totalSeconds = Mathf.RoundToInt(seconds);
        return (totalSeconds / 60).ToString(CultureInfo.InvariantCulture) + " 分 " +
               (totalSeconds % 60).ToString(CultureInfo.InvariantCulture) + " 秒";
    }

    void EnsureStyles()
    {
        if (_titleStyle != null)
            return;

        Font font = GetCjkFont();

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.78f, 0.92f, 1f) },
        };

        _lineStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = new Color(0.86f, 0.9f, 0.96f) },
        };

        _smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal = { textColor = new Color(0.66f, 0.72f, 0.8f) },
        };

        _accentStyle = new GUIStyle(_smallStyle)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.45f, 1f, 0.65f) },
        };

        _warnStyle = new GUIStyle(_smallStyle)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(1f, 0.72f, 0.42f) },
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleCenter,
        };
        _buttonStyle.normal.textColor = new Color(0.92f, 0.95f, 1f);
        _buttonStyle.hover.textColor = Color.white;
        _buttonStyle.active.textColor = Color.white;
        _buttonStyle.focused.textColor = new Color(0.92f, 0.95f, 1f);

        if (font != null)
        {
            _titleStyle.font = font;
            _lineStyle.font = font;
            _smallStyle.font = font;
            _accentStyle.font = font;
            _warnStyle.font = font;
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
}
