using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 钻头（Cylinder.001）自转 + 点击弹出实时转速面板。
/// 面板里的滑块直接改变 RPM，旋转会立刻跟着变。
/// </summary>
[DisallowMultipleComponent]
public sealed class DrillController : MonoBehaviour
{
    [Header("转速")]
    [Tooltip("默认转速（RPM）。2 秒一圈 = 30 RPM。")]
    public float defaultRpm = 30f;

    [Tooltip("滑块最小转速（RPM）。")]
    public float minRpm = 0f;

    [Tooltip("滑块最大转速（RPM）。")]
    public float maxRpm = 300f;

    [Header("面板")]
    public string panelTitle = "钻头实时转速";

    [Header("点击")]
    [Tooltip("点击判定包围盒向外扩展的尺寸（米），方便点中小钻头。")]
    public float clickPadding = 0.02f;

    Transform _pivot;
    Vector3 _spinAxis = Vector3.up;
    Renderer _renderer;
    Camera _camera;
    MachineViewController _machineView;
    float _rpm;
    bool _panelOpen;

    GUIStyle _panelStyle;
    GUIStyle _titleStyle;
    GUIStyle _valueStyle;
    GUIStyle _labelStyle;
    GUIStyle _smallStyle;
    GUIStyle _smallRightStyle;
    GUIStyle _closeStyle;
    Texture2D _panelTexture;

    static Font _cjkFont;
    static bool _fontTried;

    const float PanelWidth = 400f;
    const float PanelHeight = 250f;

    /// <summary>当前实时转速。</summary>
    public float CurrentRpm => _rpm;

    /// <summary>当前每圈秒数；0 表示已停止。</summary>
    public float SecondsPerRevolution => _rpm > 0.01f ? 60f / _rpm : 0f;

    void Awake()
    {
        _camera = Camera.main;
        _machineView = _camera != null ? _camera.GetComponent<MachineViewController>() : null;
        _renderer = GetComponent<Renderer>();
        if (_renderer == null)
            _renderer = GetComponentInChildren<Renderer>();

        _rpm = Mathf.Clamp(defaultRpm > 0f ? defaultRpm : 30f, minRpm, maxRpm);
        if (maxRpm <= minRpm)
            maxRpm = minRpm + 1f;

        SetupSpin();
    }

    void SetupSpin()
    {
        var meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            Debug.LogWarning("[DrillController] 钻头上没有 MeshFilter，无法确定自转轴。", this);
            return;
        }

        Bounds localBounds = meshFilter.sharedMesh.bounds;
        Vector3 size = localBounds.size;

        // 钻头最长的那根轴就是自转轴。
        if (size.y >= size.x && size.y >= size.z)
            _spinAxis = Vector3.up;
        else if (size.x >= size.y && size.x >= size.z)
            _spinAxis = Vector3.right;
        else
            _spinAxis = Vector3.forward;

        // 以网格中心为旋转中心，避免零件原点不在轴上时产生公转。
        Vector3 centerWorld = transform.TransformPoint(localBounds.center);
        var pivot = new GameObject(name + "_SpinPivot").transform;
        pivot.SetParent(transform.parent, false);
        pivot.SetPositionAndRotation(centerWorld, transform.rotation);
        pivot.localScale = Vector3.one;

        transform.SetParent(pivot, true);
        _pivot = pivot;

        Debug.Log($"[DrillController] mesh size={size} spinAxis={_spinAxis} pivot={centerWorld} rpm={_rpm}");
    }

    void Update()
    {
        if (_pivot != null && _rpm > 0.0001f)
        {
            // RPM -> 度/秒：360 / 60 = 6
            _pivot.Rotate(_spinAxis, _rpm * 6f * Time.deltaTime, Space.Self);
        }

        var mouse = Mouse.current;
        if (mouse == null || _camera == null || _renderer == null)
            return;

        if (mouse.leftButton.wasPressedThisFrame)
        {
            Vector2 screenPosition = mouse.position.ReadValue();
            Vector2 guiPosition = new Vector2(screenPosition.x, Screen.height - screenPosition.y);

            if (_panelOpen && GetPanelRect().Contains(guiPosition))
                return;

            Ray ray = _camera.ScreenPointToRay(new Vector3(screenPosition.x, screenPosition.y, 0f));
            Bounds pickBounds = _renderer.bounds;
            pickBounds.Expand(clickPadding);
            if (pickBounds.IntersectRay(ray))
                SetPanelOpen(!_panelOpen);
        }

        if (_panelOpen && Keyboard.current != null)
        {
            var keyboard = Keyboard.current;
            Key exitKey = _machineView != null ? _machineView.resetKey : Key.R;
            if (keyboard[exitKey].wasPressedThisFrame || keyboard[Key.Escape].wasPressedThisFrame)
                SetPanelOpen(false);
        }
    }

    void SetPanelOpen(bool open)
    {
        _panelOpen = open;

        if (_machineView == null)
            return;

        if (open)
            _machineView.FocusOn(transform);
        else
            _machineView.ClearFocus();
    }

    Rect GetPanelRect()
    {
        float width = Mathf.Clamp(Screen.width * 0.38f, 300f, PanelWidth);
        float x = Screen.width - width - 24f;
        float y = (Screen.height - PanelHeight) * 0.5f;
        return new Rect(x, y, width, PanelHeight);
    }

    void OnGUI()
    {
        if (!_panelOpen)
            return;

        EnsureStyles();

        Rect panel = GetPanelRect();
        GUI.Box(panel, GUIContent.none, _panelStyle);

        float left = panel.x + 18f;
        float top = panel.y + 12f;
        float contentWidth = panel.width - 36f;

        GUI.Label(new Rect(left, top, contentWidth - 40f, 30f), panelTitle, _titleStyle);
        if (GUI.Button(new Rect(panel.xMax - 42f, top, 26f, 26f), "×", _closeStyle))
            SetPanelOpen(false);

        GUI.Label(new Rect(left, top + 38f, contentWidth, 52f), $"{_rpm:F1} RPM", _valueStyle);

        string sub = _rpm > 0.01f ? $"≈ {60f / _rpm:F2} 秒 / 圈" : "已停止";
        GUI.Label(new Rect(left, top + 92f, contentWidth, 22f), sub, _smallStyle);

        float sliderY = panel.yMax - 76f;
        GUI.Label(new Rect(left, sliderY - 26f, contentWidth, 22f), "拖动滑块调节转速", _labelStyle);

        float value = GUI.HorizontalSlider(new Rect(left, sliderY, contentWidth, 20f), _rpm, minRpm, maxRpm);
        if (Mathf.Abs(value - _rpm) > 0.0001f)
            _rpm = Mathf.Clamp(value, minRpm, maxRpm);

        GUI.Label(new Rect(left, sliderY + 22f, contentWidth, 20f), $"{minRpm:F0} RPM", _smallStyle);
        GUI.Label(new Rect(left, sliderY + 22f, contentWidth, 20f), $"{maxRpm:F0} RPM", _smallRightStyle);
    }

    void EnsureStyles()
    {
        if (_panelStyle != null)
            return;

        _panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        _panelTexture.SetPixel(0, 0, new Color(0.055f, 0.065f, 0.085f, 0.78f));
        _panelTexture.Apply();

        _panelStyle = new GUIStyle
        {
            normal = { background = _panelTexture },
            border = new RectOffset(0, 0, 0, 0)
        };

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        _valueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 34,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.45f, 1f, 0.65f) }
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            normal = { textColor = new Color(0.85f, 0.9f, 0.95f) }
        };

        _smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = new Color(0.65f, 0.7f, 0.78f) }
        };

        _smallRightStyle = new GUIStyle(_smallStyle)
        {
            alignment = TextAnchor.MiddleRight
        };

        _closeStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 20,
            alignment = TextAnchor.MiddleCenter
        };

        Font font = GetCjkFont();
        if (font != null)
        {
            _titleStyle.font = font;
            _valueStyle.font = font;
            _labelStyle.font = font;
            _smallStyle.font = font;
            _smallRightStyle.font = font;
            _closeStyle.font = font;
        }
    }

    static Font GetCjkFont()
    {
        if (_fontTried)
            return _cjkFont;

        _fontTried = true;
        try
        {
            _cjkFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial Unicode MS" }, 20);
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

/// <summary>
/// 场景加载后自动把 DrillController 挂到主轴下的 Cylinder.001 上。
/// 这样不需要改动机床预制体，也不会在场景里留下额外组件。
/// </summary>
public static class DrillDemoBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void RuntimeSetup()
    {
        Setup();
    }

    public static void Setup()
    {
        var machine = GameObject.Find("五轴机床");
        if (machine == null)
            return;

        var drill = FindDeep(machine.transform, "Cylinder.001");
        if (drill == null)
            return;

        if (drill.GetComponent<DrillController>() == null)
            drill.gameObject.AddComponent<DrillController>();
    }

    static Transform FindDeep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == name)
                return t;
        }
        return null;
    }
}
