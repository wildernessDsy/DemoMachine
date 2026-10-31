using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 主轴（蓝色滑块 + 钻头）在演示模式下的左右往返平动，
/// 并且支持“点一下整块 → 镜头推近 → 右侧弹出速度面板 → 手动调速度”。
///
/// 设计要点：
/// 1) 只在自己身上做一段“有界限”的局部位移，不改父级、不改模型、不碰别的脚本；
/// 2) 行程由 travelMin / travelMax 定义（米，相对进入 Play 时的位置），
///    并额外用机床整机的包围盒做一次收口，保证不会跑出模型范围；
/// 3) 位移曲线是正弦的，两端自然减速，看起来像真机轴的软反向，没有生硬折返；
/// 4) 点击判定用“射线 → Renderer.bounds 命中距离”，谁离相机更近就用谁的面板
///    （钻头比主轴近 → 归 DrillController；否则归这里），避免两个面板同时弹出。
///
/// 排查用：
/// - 面板不出现时，先按 togglePanelKey（默认 P）看面板能不能单独显示：
///   能显示 → 问题在点击命中；不能显示 → 脚本没更新/没挂上。
/// - 打开 debugClicks，每次点击都会在 Console 打印 self / drill 的命中距离。
///
/// 这台机床上轴链里的节点（X轴 / X / Z轴 / Z / 主轴）Local +Y ≈ 屏幕左右方向：
/// 相机沿机床 -X 看（MachineViewController.frontLocalDirection = Vector3.right），
/// 屏幕横向 = 机床 local ±Z；而 FBX 里名为 "Y" 的节点带 180° 旋转，
/// 把子级的 local Y 映射到了机床 +Z，所以 Local Y 就是左右。
///
/// 左右行程各自独立：travelMin 管一侧、travelMax 管另一侧，改哪个就只动哪一侧。
///
/// 用法：挂在「五轴机床 / Y / Y轴 / X / X轴 / Z / Z轴 / 主轴」任意一个都可（本项目挂在 X轴），
/// 按 ▶ 即可。文件末尾的 SpindleReciprocateBootstrap 会在演示模式里自动补挂到 X轴（不会重复添加）。
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("DemoMachine/主轴往返平动 (Spindle Reciprocate)")]
public sealed class SpindleReciprocate : MonoBehaviour
{
    public enum MotionSpace
    {
        Local, // 沿父物体坐标系（推荐：主轴用 Local +Y 就是左右）
        World  // 沿世界坐标轴
    }

    [Header("运动方向")]
    [Tooltip("移动方向（会自动归一化）。主轴节点上：Y = 左右，X = 前后，Z = 上下。")]
    public Vector3 axisDirection = Vector3.up;

    [Tooltip("Local = 沿父物体坐标系；World = 沿世界坐标轴。")]
    public MotionSpace space = MotionSpace.Local;

    [Header("行程（米，相对初始位置）")]
    // 想做成左右不对称，只改这两个数字就行 —— 它们各自独立控制一侧的行程：
    //   负值 = 屏幕上往左，正值 = 屏幕上往右（若实测方向相反，把两个值同时取反即可）
    [Tooltip("负方向（往左）最远位置。想左行程更大就把这里改得更负，例如 -0.28。")]
    public float travelMin = -0.32f;

    [Tooltip("正方向（往右）最远位置。保持 +0.06 就等于右侧行程不变。")]
    public float travelMax = 0.06f;

    [Min(0.1f)]
    [Tooltip("一个完整来回（min → max → min）的秒数。也可以直接用面板上的速度滑块改它。")]
    public float cycleSeconds = 2.4f;

    [Range(0f, 1f)]
    [Tooltip("起始相位。0 = 从行程中点开始，0.25 = 从正方向端点开始。")]
    public float startPhase = 0f;

    [Header("范围限制")]
    [Tooltip("勾上后会用机床整机的包围盒再收一次行程，保证不会跑出模型范围。")]
    public bool clampToMachineBounds = true;

    [Tooltip("机床根物体名称（machineRoot 留空时按这个名字找）。")]
    public string machineRootName = "五轴机床";

    [Tooltip("可选：直接指定机床根节点；留空则按名字查找。")]
    public Transform machineRoot;

    [Min(0f)]
    [Tooltip("贴住包围盒时留出的余量（米）。")]
    public float boundsMargin = 0.02f;

    [Header("开关")]
    [Tooltip("进入 Play（演示模式）后自动开始平动。")]
    public bool playOnStart = true;

    [Header("点击弹面板")]
    [Tooltip("勾上后：点击主轴这一整块 → 镜头推近 + 右侧弹出速度面板（点钻头仍然是钻头自己的转速面板）。")]
    public bool enableClickPanel = true;

    [Tooltip("面板标题。")]
    public string panelTitle = "主轴往复速度";

    [Min(0f)]
    [Tooltip("点击判定时包围盒向外扩展的尺寸（米），方便点中细小零件。")]
    public float clickPadding = 0f;

    [Min(1f)]
    [Tooltip("面板速度滑块的左端（mm/s，平均速度）。")]
    public float minSpeedMms = 10f;

    [Min(2f)]
    [Tooltip("面板速度滑块的右端（mm/s，平均速度）。")]
    public float maxSpeedMms = 800f;

    // 面板尺寸与 DrillController 完全一致（同一套 GetPanelRect 公式、同一套字号），
    // 这样两个面板在同一个 Game 视图里大小、位置、字体都一模一样。

    [Header("排查")]
    [Tooltip("键盘快捷键：按下即可开关面板（不依赖鼠标，用来判断面板本身是否正常）。")]
    public Key togglePanelKey = Key.P;

    [Tooltip("调试：每次点击都在 Console 打印 self / drill 的命中距离。")]
    public bool debugClicks = false;

    // ───────────────────────── 运行时状态 ─────────────────────────

    Vector3 _origin;
    Vector3 _selfCenter;
    Vector3 _selfExtents;
    float _time;
    float _effectiveMin;
    float _effectiveMax;
    bool _running;
    bool _ready;

    Camera _camera;
    MachineViewController _machineView;
    bool _panelOpen;
    bool _loggedFirstDraw;

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

    /// <summary>当前是否在运动。</summary>
    public bool IsRunning => _running;

    /// <summary>经过包围盒收口后，实际生效的行程下限（米）。</summary>
    public float EffectiveMin => _effectiveMin;

    /// <summary>经过包围盒收口后，实际生效的行程上限（米）。</summary>
    public float EffectiveMax => _effectiveMax;

    /// <summary>左右两侧加起来的行程（米）。</summary>
    public float TravelMeters => Mathf.Abs(_effectiveMax - _effectiveMin);

    /// <summary>平均速度（mm/s）= 2 × 行程 ÷ 周期。</summary>
    public float AverageSpeedMms =>
        TravelMeters > 1e-5f && cycleSeconds > 0.01f
            ? (2f * TravelMeters / cycleSeconds) * 1000f
            : 0f;

    /// <summary>正弦运动的峰值速度（mm/s）。</summary>
    public float PeakSpeedMms =>
        TravelMeters > 1e-5f && cycleSeconds > 0.01f
            ? (Mathf.PI * TravelMeters / cycleSeconds) * 1000f
            : 0f;

    /// <summary>面板是否打开。</summary>
    public bool IsPanelOpen => _panelOpen;

    void Awake()
    {
        _origin = CurrentPosition;
        CacheSelfBounds();

        _camera = Camera.main;
        _machineView = _camera != null ? _camera.GetComponent<MachineViewController>() : null;
    }

    void Start()
    {
        ComputeRange();
        _running = playOnStart;
        _time = Mathf.Clamp01(startPhase) * Mathf.Max(cycleSeconds, 0.1f);
        _ready = true;

        Debug.Log($"[SpindleReciprocate] 轴向 {space} {AxisDirection}，" +
                  $"实际行程 {_effectiveMin:F3} ~ {_effectiveMax:F3} m，" +
                  $"周期 {cycleSeconds:F2} s，平均速度 ≈ {AverageSpeedMms:F0} mm/s" +
                  $"（点击面板 {(enableClickPanel ? "已开启" : "已关闭")}，开关快捷键 {togglePanelKey}）", this);
    }

    void Update()
    {
        HandleClickAndKeys();

        if (!_running || !_ready)
            return;

        _time += Time.deltaTime;

        float omega = 2f * Mathf.PI / Mathf.Max(cycleSeconds, 0.1f);
        float t01 = (Mathf.Sin(omega * _time) + 1f) * 0.5f; // 0 ~ 1 平滑往返
        float offset = Mathf.Lerp(_effectiveMin, _effectiveMax, t01);

        SetPosition(_origin + AxisDirection * offset);
    }

    void OnDisable()
    {
        // 组件被关掉 / 退出 Play 时把位置还原，避免留在半路上
        if (_ready)
            SetPosition(_origin);

        _running = false;
    }

    void OnDestroy()
    {
        if (_panelTexture != null)
            Destroy(_panelTexture);
    }

    // ───────────────────────── 对外接口 ─────────────────────────

    /// <summary>开 / 关平动；关闭时回到初始位置。</summary>
    public void SetRunning(bool run)
    {
        _running = run;
        if (!run)
            SetPosition(_origin);
    }

    /// <summary>用“平均速度（mm/s）”反推周期。面板滑块走的就是这里。</summary>
    public void SetAverageSpeedMms(float speedMms)
    {
        float travel = TravelMeters;
        if (travel <= 1e-5f || speedMms <= 0.01f)
            return;

        cycleSeconds = Mathf.Clamp(2f * travel / (speedMms / 1000f), 0.1f, 600f);
    }

    /// <summary>把当前位置当作运动的中心，并重新计算可用行程。</summary>
    [ContextMenu("把当前位置设为运动中心")]
    public void RebaseOrigin()
    {
        _origin = CurrentPosition;
        CacheSelfBounds();
        ComputeRange();
    }

    /// <summary>重新按机床包围盒计算行程（改过 travelMin / travelMax 后可手动调用）。</summary>
    [ContextMenu("重新计算行程范围")]
    public void ComputeRange()
    {
        float low = Mathf.Min(travelMin, travelMax);
        float high = Mathf.Max(travelMin, travelMax);

        if (clampToMachineBounds && TryGetMachineBounds(out Bounds machineBounds))
        {
            Vector3 worldDirection = WorldDirection;
            float halfAlongPart = ProjectExtent(_selfExtents, worldDirection);
            float halfAlongMachine = ProjectExtent(machineBounds.extents, worldDirection);

            float partCenterAxis = Vector3.Dot(_selfCenter, worldDirection);
            float machineCenterAxis = Vector3.Dot(machineBounds.center, worldDirection);

            // 物体中心允许的绝对区间（保证整个人在机床包围盒里，留出 margin）
            float centerLow = machineCenterAxis - halfAlongMachine + halfAlongPart + boundsMargin;
            float centerHigh = machineCenterAxis + halfAlongMachine - halfAlongPart - boundsMargin;

            // 换成“相对初始位置的偏移量”区间
            float offsetLow = centerLow - partCenterAxis;
            float offsetHigh = centerHigh - partCenterAxis;

            low = Mathf.Max(low, offsetLow);
            high = Mathf.Min(high, offsetHigh);
        }

        if (high < low)
        {
            // 行程区间被收成负值（理论上不该发生）：退化成“停在能站的位置”
            float middle = Mathf.Clamp(0f, Mathf.Min(travelMin, travelMax), Mathf.Max(travelMin, travelMax));
            low = middle;
            high = middle;
            Debug.LogWarning("[SpindleReciprocate] 行程区间被包围盒收爆了，已退化为原地不动。请检查 travelMin / travelMax。", this);
        }

        _effectiveMin = low;
        _effectiveMax = high;
    }

    // ───────────────────────── 点击 / 按键 ─────────────────────────

    void HandleClickAndKeys()
    {
        if (!enableClickPanel)
            return;

        // 组件是运行时挂上的，兜底再找一次相机（避免 Awake 时 Camera.main 还没就绪）
        if (_camera == null)
            _camera = Camera.main;

        if (_machineView == null && _camera != null)
            _machineView = _camera.GetComponent<MachineViewController>();

        var keyboard = Keyboard.current;

        // 排查用快捷键：不依赖鼠标，直接开关面板
        if (keyboard != null && keyboard[togglePanelKey].wasPressedThisFrame)
        {
            SetPanelOpen(!_panelOpen);
            return;
        }

        if (_panelOpen && keyboard != null)
        {
            Key closeKey = _machineView != null ? _machineView.resetKey : Key.R;
            if (keyboard[closeKey].wasPressedThisFrame || keyboard[Key.Escape].wasPressedThisFrame)
            {
                SetPanelOpen(false);
                return;
            }
        }

        var mouse = Mouse.current;
        if (mouse == null || _camera == null)
            return;

        if (!mouse.leftButton.wasPressedThisFrame)
            return;

        Vector2 screenPosition = mouse.position.ReadValue();
        Vector2 guiPosition = new Vector2(screenPosition.x, Screen.height - screenPosition.y);

        // 点在面板上（拖滑块、点 ×）时不要切换面板状态
        if (_panelOpen && GetPanelRect().Contains(guiPosition))
            return;

        Ray ray = _camera.ScreenPointToRay(new Vector3(screenPosition.x, screenPosition.y, 0f));

        float selfDistance = NearestHitDistance(ray, false);   // 主轴这一整块（不含钻头）
        float drillDistance = NearestHitDistance(ray, true);   // 只算钻头

        if (debugClicks)
            Debug.Log($"[SpindleReciprocate] 点击 self={FormatDistance(selfDistance)}，" +
                      $"drill={FormatDistance(drillDistance)}", this);

        // 钻头比主轴更近 → 这一下归 DrillController，同时收起自己的面板
        if (drillDistance < float.MaxValue && drillDistance <= selfDistance)
        {
            if (_panelOpen)
                SetPanelOpen(false);

            return;
        }

        if (selfDistance == float.MaxValue)
            return;

        SetPanelOpen(!_panelOpen);
    }

    static string FormatDistance(float distance)
    {
        return distance == float.MaxValue ? "未命中" : distance.ToString("F3");
    }

    /// <summary>返回射线与本节点子树里渲染器的最近命中距离；只在 drillOnly 指定的那一类里找。</summary>
    float NearestHitDistance(Ray ray, bool drillOnly)
    {
        float nearest = float.MaxValue;

        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !renderer.enabled)
                continue;

            if (IsDrillRenderer(renderer) != drillOnly)
                continue;

            Bounds bounds = renderer.bounds;
            if (bounds.size.sqrMagnitude < 1e-10f)
                continue; // 网格丢失时 bounds 是 0，跳过

            bounds.Expand(clickPadding);

            if (bounds.IntersectRay(ray, out float distance) && distance < nearest)
                nearest = distance;
        }

        return nearest;
    }

    static bool IsDrillRenderer(Renderer renderer)
    {
        return renderer.GetComponentInParent<DrillController>(true) != null;
    }

    void SetPanelOpen(bool open)
    {
        _panelOpen = open;

        if (open)
        {
            // 开自己的面板前，先把钻头的面板关掉（两边共用一个右侧位置）
            foreach (var drill in GetComponentsInChildren<DrillController>(true))
                drill.ClosePanel();

            if (_machineView != null)
                _machineView.FocusOn(transform);
        }
        else
        {
            if (_machineView != null)
                _machineView.ClearFocus();
        }

        if (debugClicks)
            Debug.Log($"[SpindleReciprocate] 面板 {(open ? "打开" : "关闭")}", this);
    }

    // ───────────────────────── 面板绘制 ─────────────────────────

    // 与 DrillController.GetPanelRect() 逐行一致：右侧、宽 300~400、高 250、右边距 24、垂直居中
    Rect GetPanelRect()
    {
        float width = Mathf.Clamp(Screen.width * 0.38f, 300f, PanelWidth);
        float x = Screen.width - width - 24f;
        float y = (Screen.height - PanelHeight) * 0.5f;
        return new Rect(x, y, width, PanelHeight);
    }

    // 与 DrillController.OnGUI 完全一致（同样的坐标、同样的字号、同样的颜色），
    // 只是把 RPM 换成了往复速度 mm/s —— 所以两个面板在 Game 视图里大小完全一样。
    void OnGUI()
    {
        if (!_panelOpen)
            return;

        if (!_loggedFirstDraw)
        {
            _loggedFirstDraw = true;
            Debug.Log($"[SpindleReciprocate] OnGUI 正在绘制面板（屏幕 {Screen.width}×{Screen.height}）", this);
        }

        EnsureStyles();

        Rect panel = GetPanelRect();
        GUI.Box(panel, GUIContent.none, _panelStyle);

        float left = panel.x + 18f;
        float top = panel.y + 12f;
        float contentWidth = panel.width - 36f;

        GUI.Label(new Rect(left, top, contentWidth - 40f, 30f), panelTitle, _titleStyle);
        if (GUI.Button(new Rect(panel.xMax - 42f, top, 26f, 26f), "×", _closeStyle))
            SetPanelOpen(false);

        // 主数值：平均往复速度
        GUI.Label(new Rect(left, top + 38f, contentWidth, 52f), $"{AverageSpeedMms:F1} mm/s", _valueStyle);

        GUI.Label(new Rect(left, top + 92f, contentWidth, 22f),
            $"≈ {cycleSeconds:F2} 秒 / 往复 ｜ 单程 {TravelMeters * 1000f:F0} mm", _smallStyle);

        float sliderY = panel.yMax - 76f;
        GUI.Label(new Rect(left, sliderY - 26f, contentWidth, 22f), "拖动滑块调节速度", _labelStyle);

        float current = Mathf.Clamp(AverageSpeedMms, minSpeedMms, maxSpeedMms);
        float value = GUI.HorizontalSlider(new Rect(left, sliderY, contentWidth, 20f), current, minSpeedMms, maxSpeedMms);
        if (Mathf.Abs(value - current) > 0.5f)
            SetAverageSpeedMms(value);

        GUI.Label(new Rect(left, sliderY + 22f, contentWidth, 20f), $"{minSpeedMms:F0} mm/s", _smallStyle);
        GUI.Label(new Rect(left, sliderY + 22f, contentWidth, 20f), $"{maxSpeedMms:F0} mm/s", _smallRightStyle);
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

    // ───────────────────────── 位置 / 包围盒 ─────────────────────────

    Vector3 AxisDirection =>
        axisDirection.sqrMagnitude > 1e-8f ? axisDirection.normalized : Vector3.up;

    Vector3 WorldDirection
    {
        get
        {
            if (space == MotionSpace.World)
                return AxisDirection;

            return transform.parent != null
                ? transform.parent.TransformDirection(AxisDirection)
                : AxisDirection;
        }
    }

    Vector3 CurrentPosition =>
        space == MotionSpace.Local ? transform.localPosition : transform.position;

    void SetPosition(Vector3 value)
    {
        if (space == MotionSpace.Local)
            transform.localPosition = value;
        else
            transform.position = value;
    }

    void CacheSelfBounds()
    {
        _selfCenter = transform.position;
        _selfExtents = Vector3.zero;

        if (!TryGetRendererBounds(transform, out Bounds bounds))
            return;

        _selfCenter = bounds.center;
        _selfExtents = bounds.extents;
    }

    static bool TryGetRendererBounds(Transform root, out Bounds bounds)
    {
        bounds = new Bounds(root.position, Vector3.zero);
        bool found = false;

        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !renderer.enabled)
                continue;

            Bounds b = renderer.bounds;
            if (b.size.sqrMagnitude < 1e-10f)
                continue; // 网格丢失时 bounds 是 0，跳过，避免污染结果

            if (!found)
            {
                bounds = b;
                found = true;
            }
            else
            {
                bounds.Encapsulate(b);
            }
        }

        return found;
    }

    bool TryGetMachineBounds(out Bounds bounds)
    {
        Transform root = machineRoot;

        if (root == null && !string.IsNullOrEmpty(machineRootName))
        {
            var go = GameObject.Find(machineRootName);
            if (go != null)
                root = go.transform;
        }

        if (root == null)
        {
            bounds = default;
            return false;
        }

        return TryGetRendererBounds(root, out bounds);
    }

    static float ProjectExtent(Vector3 extents, Vector3 direction)
    {
        return Mathf.Abs(direction.x) * extents.x
             + Mathf.Abs(direction.y) * extents.y
             + Mathf.Abs(direction.z) * extents.z;
    }
}

/// <summary>
/// 演示模式（Play）下自动把 SpindleReciprocate 挂到「五轴机床/…/X轴」上，
/// 这样即使场景/预制体被重置，也依然默认有平动 + 点击面板。
/// 如果已经在编辑模式里手动挂过，这里不会重复添加（以你 Inspector 里的参数为准）；
/// 想换成别的节点，改下面 FindDeep 里的名字即可（例如 "主轴"）。
/// 不想要自动挂载：把下面这个 static class 整段删掉即可。
/// </summary>
public static class SpindleReciprocateBootstrap
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

        var spindle = FindDeep(machine.transform, "X轴");
        if (spindle == null)
            return;

        if (spindle.GetComponent<SpindleReciprocate>() == null)
            spindle.gameObject.AddComponent<SpindleReciprocate>();
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
