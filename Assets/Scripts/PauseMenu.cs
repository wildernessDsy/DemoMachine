using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 按 Esc 暂停 / 继续：Time.timeScale 归零、释放光标、显示遮罩。
/// 挂在场景任意物体上即可。
/// </summary>
[DisallowMultipleComponent]
public sealed class PauseMenu : MonoBehaviour
{
    [Header("界面")]
    public string title = "已暂停";
    public string hint = "按 Esc 或点下面的按钮继续";

    bool _paused;
    GUIStyle _title, _hint, _button;
    static Font _cjkFont;
    static bool _fontTried;

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard[Key.Escape].wasPressedThisFrame)
            SetPaused(!_paused);
    }

    public void SetPaused(bool value)
    {
        if (_paused == value)
            return;

        _paused = value;
        Time.timeScale = value ? 0f : 1f;

        if (value)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void OnDestroy()
    {
        if (_paused)
            Time.timeScale = 1f;
    }

    void OnGUI()
    {
        if (!_paused)
            return;

        EnsureStyles();

        float s = Mathf.Clamp(Screen.height / 900f, 0.9f, 1.8f);

        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = old;

        float w = 320f * s;
        float h = 170f * s;
        var panel = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

        GUI.color = new Color(0.07f, 0.08f, 0.11f, 0.95f);
        GUI.DrawTexture(panel, Texture2D.whiteTexture);
        GUI.color = old;

        _title.fontSize = Mathf.RoundToInt(26 * s);
        _hint.fontSize = Mathf.RoundToInt(13 * s);
        _button.fontSize = Mathf.RoundToInt(15 * s);

        GUI.Label(new Rect(panel.x, panel.y + 22f * s, panel.width, 36f * s), title, _title);
        GUI.Label(new Rect(panel.x, panel.y + 62f * s, panel.width, 24f * s), hint, _hint);

        var buttonRect = new Rect(panel.x + 24f * s, panel.y + 100f * s, panel.width - 48f * s, 40f * s);
        if (GUI.Button(buttonRect, "继续演示", _button))
            SetPaused(false);
    }

    void EnsureStyles()
    {
        if (_title != null)
            return;

        _title = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold
        };
        _title.normal.textColor = Color.white;

        _hint = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
        _hint.normal.textColor = new Color(0.75f, 0.8f, 0.86f);

        _button = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleCenter };
        _button.normal.textColor = new Color(0.1f, 0.12f, 0.16f);

        if (!_fontTried)
        {
            _fontTried = true;
            _cjkFont = CjkFont.Get();
        }

        if (_cjkFont != null)
        {
            _title.font = _cjkFont;
            _hint.font = _cjkFont;
            _button.font = _cjkFont;
        }
    }
}
