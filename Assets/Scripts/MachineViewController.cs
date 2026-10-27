using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 机床演示相机：
/// 1) 启动时把相机放到机床正面（有门和右侧操作面板的一侧），让机床可见轮廓的高度占据画面指定比例；
/// 2) 按一下 F 进入视角改变模式，模式内 ↑ / ↓ 让相机沿视线前后移动，产生“人在走动”的透视变化；
/// 3) 按一下 R 退出视角改变模式；移动限制在机床正面之外，避免穿模。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class MachineViewController : MonoBehaviour
{
    [Header("机床")]
    [Tooltip("机床模型根节点，留空时会在场景中按名称查找。")]
    public Transform machineRoot;

    [Tooltip("机床正面的本地方向。门 + 右侧操作面板所在的一侧是模型的本地 +X。")]
    public Vector3 frontLocalDirection = Vector3.right;

    [Header("构图")]
    [Range(0.05f, 1f)]
    [Tooltip("启动时机床可见轮廓占据的屏幕高度比例。0.5 = 50%。")]
    public float screenHeightFraction = 0.5f;

    [Range(0.1f, 1f)]
    [Tooltip("同时保证机床宽度不超过该比例，避免窄屏时被裁切。")]
    public float maxScreenWidthFraction = 0.9f;

    [Tooltip("编辑器校准出的机床可见轮廓真实高度（米）。大于 0 时用它精确构图；为 0 时按包围盒自动估算。")]
    public float calibratedSilhouetteHeight = 0f;

    [Header("视角改变模式")]
    [Tooltip("按一下进入视角改变模式；模式内 ↑/↓（或 W/S）前后移动。")]
    public Key walkKey = Key.F;

    [Tooltip("按一下退出视角改变模式。")]
    public Key resetKey = Key.R;

    [Min(0f)]
    [Tooltip("步行速度，米/秒。")]
    public float walkSpeed = 2.2f;

    [Min(0f)]
    public float acceleration = 7f;

    [Min(0f)]
    public float deceleration = 12f;

    [Tooltip("相机与机床正面的最小距离（米）。正数 = 停在正面外；负数 = 允许进入机床内部。默认 -0.7 表示可越过正面 0.7 米，便于近看主轴/钻头。")]
    public float closestStandoff = -0.7f;

    [Min(0f)]
    [Tooltip("从初始位置最多可以后退的距离。")]
    public float maxRetreat = 4f;

    [Header("视角移动范围")]
    [Min(0.5f)]
    [Tooltip("左右平移的最大距离（米）。")]
    public float maxStrafe = 3f;

    [Min(0.2f)]
    [Tooltip("上下移动的最大距离（米）。")]
    public float maxVertical = 1.2f;

    [Min(10f)]
    [Tooltip("Z / X 左右转向的速度（度/秒）。")]
    public float turnSpeed = 90f;

    [Min(30f)]
    [Tooltip("转向的加减速度（度/秒²）。")]
    public float turnAcceleration = 360f;

    [Header("钻头特写")]
    [Range(0.15f, 0.8f)]
    [Tooltip("特写时钻头在画面高度方向最多占据的比例。")]
    public float focusHeightFraction = 0.45f;

    [Range(0.15f, 0.8f)]
    [Tooltip("特写时钻头在画面宽度方向最多占据的比例（横放的钻头由宽度决定距离）。")]
    public float focusWidthFraction = 0.4f;

    [Range(0.15f, 0.6f)]
    [Tooltip("特写时钻头中心在画面水平方向的位置（0 = 最左，1 = 最右）。0.33 给右侧面板留空间。")]
    public float focusScreenX = 0.33f;

    [Min(0.1f)]
    [Tooltip("镜头推进 / 退回的过渡时间（秒）。")]
    public float focusTransitionTime = 0.45f;

    [Min(0.03f)]
    [Tooltip("特写时相机离钻头表面的最小距离（米）。")]
    public float focusMinDistance = 0.12f;

    [Min(0.2f)]
    public float focusMaxDistance = 1.2f;

    [Header("界面")]
    public bool showHints = true;

    Camera _camera;
    Renderer[] _renderers;
    Vector3 _homePosition;
    Quaternion _homeRotation;
    Vector3 _walkForward;      // 初始正前方（= -front）
    Vector3 _baseRight;        // 初始右方向
    Vector3 _offset;           // 相对初始位置的位移
    Vector3 _velocity;
    float _yaw;                // 相对初始朝向的水平转角（度）
    float _yawVelocity;
    float _maxApproach;
    bool _ready;
    bool _viewMode;

    bool _focusActive;
    bool _focusReturning;
    float _focusT;
    Vector3 _focusFromPosition;
    Quaternion _focusFromRotation;
    Vector3 _focusToPosition;
    Quaternion _focusToRotation;
    Vector3 _savedPosition;
    Quaternion _savedRotation;

    GUIStyle _hintStyle;
    static Font _cjkFont;
    static bool _cjkFontTried;

    void Awake()
    {
        _camera = GetComponent<Camera>();
        ResolveMachineRoot();
    }

    void Start()
    {
        FrameMachine();

        // 双保险：即使 RuntimeInitializeOnLoadMethod 在“关闭域重载”的播放设置下没触发，
        // 也能保证钻头控制器被挂上。
        DrillDemoBootstrap.Setup();
    }

    void Update()
    {
        if (!_ready)
            return;

        var keyboard = Keyboard.current;

        if (keyboard != null)
        {
            if (keyboard[walkKey].wasPressedThisFrame)
                _viewMode = true;

            if (_viewMode && keyboard[resetKey].wasPressedThisFrame)
            {
                _viewMode = false;
                _velocity = Vector3.zero;
                _yawVelocity = 0f;
            }
        }

        if (_focusActive)
        {
            UpdateFocusTransition();
            return;
        }

        if (keyboard == null)
            return;

        float forwardInput = 0f;
        float strafeInput = 0f;
        float verticalInput = 0f;
        float turnInput = 0f;
        if (_viewMode)
        {
            forwardInput = (keyboard[Key.UpArrow].isPressed ? 1f : 0f) - (keyboard[Key.DownArrow].isPressed ? 1f : 0f);
            strafeInput = (keyboard[Key.RightArrow].isPressed ? 1f : 0f) - (keyboard[Key.LeftArrow].isPressed ? 1f : 0f);
            verticalInput = (keyboard[Key.W].isPressed ? 1f : 0f) - (keyboard[Key.S].isPressed ? 1f : 0f);
            turnInput = (keyboard[Key.X].isPressed ? 1f : 0f) - (keyboard[Key.Z].isPressed ? 1f : 0f);
        }

        // Z / X：水平左右转向
        float targetYawSpeed = turnInput * turnSpeed;
        _yawVelocity = Mathf.MoveTowards(_yawVelocity, targetYawSpeed, turnAcceleration * Time.deltaTime);
        _yaw += _yawVelocity * Time.deltaTime;

        Quaternion yawRotation = Quaternion.Euler(0f, _yaw, 0f);
        Vector3 forward = yawRotation * _walkForward;
        Vector3 right = yawRotation * _baseRight;

        Vector3 inputDirection = forward * forwardInput + right * strafeInput + Vector3.up * verticalInput;
        if (inputDirection.sqrMagnitude > 1f)
            inputDirection.Normalize();

        Vector3 targetVelocity = inputDirection * walkSpeed;
        float rate = inputDirection.sqrMagnitude < 1e-6f ? deceleration : acceleration;
        _velocity = Vector3.MoveTowards(_velocity, targetVelocity, rate * Time.deltaTime);

        Vector3 nextOffset = _offset + _velocity * Time.deltaTime;
        float clampedX = Mathf.Clamp(nextOffset.x, -maxStrafe, maxStrafe);
        float clampedY = Mathf.Clamp(nextOffset.y, -maxVertical, maxVertical);
        float clampedZ = Mathf.Clamp(nextOffset.z, -maxRetreat, _maxApproach);
        if (!Mathf.Approximately(clampedX, nextOffset.x))
            _velocity.x = 0f;
        if (!Mathf.Approximately(clampedY, nextOffset.y))
            _velocity.y = 0f;
        if (!Mathf.Approximately(clampedZ, nextOffset.z))
            _velocity.z = 0f;
        _offset = new Vector3(clampedX, clampedY, clampedZ);

        _camera.transform.position = _homePosition + _offset;
        _camera.transform.rotation = yawRotation * _homeRotation;
    }

    /// <summary>
    /// 平滑推到 target 的特写。目标物体放在画面左侧（focusScreenX），右侧留给转速面板。
    /// </summary>
    public void FocusOn(Transform target)
    {
        if (!_ready || target == null)
            return;

        var targetRenderer = target.GetComponentInChildren<Renderer>();
        if (targetRenderer == null)
            return;

        Bounds targetBounds = targetRenderer.bounds;
        Vector3 center = targetBounds.center;
        Vector3 size = targetBounds.size;

        Vector3 viewDirection = _camera.transform.forward;
        viewDirection.y = 0f;
        if (viewDirection.sqrMagnitude < 1e-4f)
            viewDirection = _walkForward;
        viewDirection.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, viewDirection).normalized;
        Vector3 cameraUp = Vector3.Cross(viewDirection, right).normalized;

        float halfVertical = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float halfHorizontal = halfVertical * Mathf.Max(_camera.aspect, 0.01f);

        // 按当前视线方向把包围盒投影成屏幕上的宽 / 高 / 深度，而不是简单取最大边长。
        float widthExtent = 0.5f * (
            Mathf.Abs(right.x) * size.x + Mathf.Abs(right.y) * size.y + Mathf.Abs(right.z) * size.z);
        float heightExtent = 0.5f * (
            Mathf.Abs(cameraUp.x) * size.x + Mathf.Abs(cameraUp.y) * size.y + Mathf.Abs(cameraUp.z) * size.z);
        float depthExtent = 0.5f * (
            Mathf.Abs(viewDirection.x) * size.x + Mathf.Abs(viewDirection.y) * size.y + Mathf.Abs(viewDirection.z) * size.z);

        float minSurface = Mathf.Max(focusMinDistance, _camera.nearClipPlane + 0.03f);
        float surfaceDistance = Mathf.Max(
            widthExtent / (Mathf.Clamp(focusWidthFraction, 0.15f, 0.8f) * halfHorizontal),
            heightExtent / (Mathf.Clamp(focusHeightFraction, 0.15f, 0.8f) * halfVertical));
        surfaceDistance = Mathf.Max(surfaceDistance, minSurface);

        float distance = Mathf.Min(depthExtent + surfaceDistance, focusMaxDistance);
        Vector3 position = center - viewDirection * distance;

        // 把目标放到画面左侧，给右侧面板让出空间。
        float ndcX = Mathf.Clamp(focusScreenX, 0.15f, 0.6f) * 2f - 1f;
        Vector3 lookTarget = center + right * (-ndcX * distance * halfHorizontal);
        Quaternion rotation = Quaternion.LookRotation(lookTarget - position, Vector3.up);

        if (!_focusActive)
        {
            _savedPosition = _camera.transform.position;
            _savedRotation = _camera.transform.rotation;
        }

        _velocity = Vector3.zero;
        _yawVelocity = 0f;
        StartFocus(position, rotation, false);
    }

    /// <summary>结束特写，平滑回到点击前的视角。</summary>
    public void ClearFocus()
    {
        if (!_focusActive)
            return;

        StartFocus(_savedPosition, _savedRotation, true);
    }

    void StartFocus(Vector3 position, Quaternion rotation, bool returning)
    {
        _focusFromPosition = _camera.transform.position;
        _focusFromRotation = _camera.transform.rotation;
        _focusToPosition = position;
        _focusToRotation = rotation;
        _focusT = 0f;
        _focusReturning = returning;
        _focusActive = true;
    }

    void UpdateFocusTransition()
    {
        float duration = Mathf.Max(0.05f, focusTransitionTime);
        _focusT = Mathf.MoveTowards(_focusT, 1f, Time.deltaTime / duration);
        float t = Mathf.SmoothStep(0f, 1f, _focusT);

        _camera.transform.position = Vector3.Lerp(_focusFromPosition, _focusToPosition, t);
        _camera.transform.rotation = Quaternion.Slerp(_focusFromRotation, _focusToRotation, t);

        if (_focusT >= 1f && _focusReturning)
            _focusActive = false;
    }

    float ComputeDistance(Bounds bounds, Vector3 front)
    {
        float halfVertical = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float halfHorizontal = halfVertical * Mathf.Max(_camera.aspect, 0.01f);
        float frontExtent = Mathf.Abs(front.x) * bounds.extents.x + Mathf.Abs(front.z) * bounds.extents.z;
        float minDistance = frontExtent + Mathf.Max(_camera.nearClipPlane + 0.1f, 0.2f);

        if (calibratedSilhouetteHeight > 0.01f)
        {
            float distance = frontExtent + calibratedSilhouetteHeight /
                (2f * Mathf.Clamp(screenHeightFraction, 0.02f, 1f) * halfVertical);

            // 宽度兜底：极窄的画面里宁可再退远一点，也不让机床被裁边。
            Vector3 right = Vector3.Cross(front, Vector3.up).normalized;
            float width = 2f * (Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z);
            distance = Mathf.Max(distance,
                frontExtent + width / (2f * Mathf.Clamp(maxScreenWidthFraction, 0.1f, 1f) * halfHorizontal));

            return Mathf.Max(distance, minDistance);
        }

        return SolveDistance(_camera, _renderers, bounds, front, screenHeightFraction, maxScreenWidthFraction);
    }

    /// <summary>
    /// 重新计算构图并把相机放到正面视角。编辑器和运行时都可以调用。
    /// </summary>
    [ContextMenu("重新构图（正面 50%）")]
    public void FrameMachine()
    {
        if (_camera == null)
            _camera = GetComponent<Camera>();

        ResolveMachineRoot();

        if (machineRoot == null || !TryGetBounds(machineRoot, out var bounds))
        {
            Debug.LogWarning("[MachineViewController] 没有找到机床，或机床上没有可渲染的网格。", this);
            _ready = false;
            return;
        }

        _renderers = CollectRenderers(machineRoot);

        Vector3 front = machineRoot.TransformDirection(frontLocalDirection.normalized);
        front.y = 0f;
        if (front.sqrMagnitude < 1e-6f)
            front = Vector3.forward;
        front.Normalize();

        float distance = ComputeDistance(bounds, front);

        _homePosition = bounds.center + front * distance;
        _homeRotation = Quaternion.LookRotation(-front, Vector3.up);

        _camera.transform.SetPositionAndRotation(_homePosition, _homeRotation);
        _camera.nearClipPlane = Mathf.Min(_camera.nearClipPlane, 0.05f);

        _walkForward = -front;
        _baseRight = Vector3.Cross(Vector3.up, _walkForward).normalized;

        // 相机沿 front 方向到机床正面的水平距离
        float frontExtent = Mathf.Abs(front.x) * bounds.extents.x + Mathf.Abs(front.z) * bounds.extents.z;
        float closest = Mathf.Clamp(closestStandoff, -0.9f, 2f);
        if (closest >= 0f)
            closest = Mathf.Max(closest, _camera.nearClipPlane + 0.03f);
        _maxApproach = Mathf.Max(0f, distance - frontExtent - closest);

        _offset = Vector3.zero;
        _velocity = Vector3.zero;
        _yaw = 0f;
        _yawVelocity = 0f;
        _ready = true;
    }

    /// <summary>回到启动时的构图。</summary>
    [ContextMenu("复位视角")]
    public void ResetView()
    {
        if (!_ready)
            return;

        _offset = Vector3.zero;
        _velocity = Vector3.zero;
        _yaw = 0f;
        _yawVelocity = 0f;
        _camera.transform.SetPositionAndRotation(_homePosition, _homeRotation);
    }

    void ResolveMachineRoot()
    {
        if (machineRoot != null)
            return;

        var go = GameObject.Find("五轴机床");
        if (go != null)
            machineRoot = go.transform;
    }

    /// <summary>测量当前相机视角下机床可见轮廓占屏幕的高度、宽度比例。</summary>
    public void MeasureCurrentView(out float heightFraction, out float widthFraction)
    {
        MeasureScreenFractions(_camera, _renderers, out heightFraction, out widthFraction);
    }

    /// <summary>
    /// 求解相机距离：让机床可见轮廓高度 ≈ targetHeight，且宽度不超过 maxWidth。
    /// 使用每个 Renderer 的世界包围盒八个角做投影，比整体包围盒更贴近真实轮廓。
    /// </summary>
    public static float SolveDistance(Camera camera, Renderer[] renderers, Bounds bounds, Vector3 front, float targetHeight, float maxWidth)
    {
        float halfVertical = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float frontExtent = Mathf.Abs(front.x) * bounds.extents.x + Mathf.Abs(front.z) * bounds.extents.z;
        float minDistance = frontExtent + Mathf.Max(camera.nearClipPlane + 0.1f, 0.2f);

        float distance = Mathf.Max(
            minDistance,
            frontExtent + bounds.size.y / (2f * Mathf.Clamp(targetHeight, 0.02f, 1f) * halfVertical));

        var rotation = Quaternion.LookRotation(-front, Vector3.up);

        for (int i = 0; i < 24; i++)
        {
            camera.transform.SetPositionAndRotation(bounds.center + front * distance, rotation);
            MeasureScreenFractions(camera, renderers, out float heightFraction, out float widthFraction);

            bool heightOk = Mathf.Abs(heightFraction - targetHeight) <= 0.002f;
            bool widthOk = widthFraction <= maxWidth + 0.002f;
            if (heightOk && widthOk)
                break;

            float scale;
            if (!heightOk)
                scale = Mathf.Clamp(heightFraction / Mathf.Max(targetHeight, 1e-4f), 0.6f, 1.6f);
            else
                scale = Mathf.Clamp(widthFraction / Mathf.Max(maxWidth, 1e-4f), 1f, 1.4f);

            float next = frontExtent + (distance - frontExtent) * scale;
            next = Mathf.Max(next, minDistance);
            if (Mathf.Abs(next - distance) < 1e-4f)
                break;
            distance = next;
        }

        return distance;
    }

    /// <summary>
    /// 把所有 Renderer 包围盒的角点投影到当前相机，得到可见轮廓的屏幕占比。
    /// 返回值 0.5 表示高度（或宽度）占屏幕一半。
    /// </summary>
    public static void MeasureScreenFractions(Camera camera, Renderer[] renderers, out float heightFraction, out float widthFraction)
    {
        heightFraction = 0f;
        widthFraction = 0f;
        if (camera == null || renderers == null || renderers.Length == 0)
            return;

        float halfVertical = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float halfHorizontal = halfVertical * Mathf.Max(camera.aspect, 0.01f);

        Vector3 cameraPosition = camera.transform.position;
        Vector3 forward = camera.transform.forward;
        Vector3 right = camera.transform.right;
        Vector3 up = camera.transform.up;

        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        bool any = false;

        foreach (var renderer in renderers)
        {
            if (renderer == null)
                continue;

            Bounds b = renderer.bounds;
            Vector3 center = b.center;
            Vector3 extent = b.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    center.x + ((i & 1) == 0 ? -extent.x : extent.x),
                    center.y + ((i & 2) == 0 ? -extent.y : extent.y),
                    center.z + ((i & 4) == 0 ? -extent.z : extent.z));

                Vector3 v = corner - cameraPosition;
                float z = Vector3.Dot(v, forward);
                if (z <= 0.001f)
                    continue;

                float x = Vector3.Dot(v, right) / (z * halfHorizontal);
                float y = Vector3.Dot(v, up) / (z * halfVertical);

                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
                any = true;
            }
        }

        if (!any)
            return;

        // NDC 范围是 [-1, 1]，所以屏幕占比 = 跨度 / 2。
        heightFraction = (maxY - minY) * 0.5f;
        widthFraction = (maxX - minX) * 0.5f;
    }

    public static Renderer[] CollectRenderers(Transform root)
    {
        if (root == null)
            return new Renderer[0];

        var all = root.GetComponentsInChildren<Renderer>(true);
        var list = new System.Collections.Generic.List<Renderer>(all.Length);
        foreach (var r in all)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                continue;
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || r is SpriteRenderer)
                continue;
            list.Add(r);
        }
        return list.ToArray();
    }

    /// <summary>把机床所有启用中的 Renderer 包成一个世界空间包围盒。</summary>
    public static bool TryGetBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        if (root == null)
            return false;

        bool has = false;
        foreach (var r in CollectRenderers(root))
        {
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

        return has;
    }

    void OnGUI()
    {
        if (!showHints)
            return;

        EnsureHintStyle();

        string hint = _viewMode
            ? "视角模式（R 退出）\n↑/↓ 前后    ←/→ 左右\nW/S 上下    Z/X 左转/右转"
            : "按 F 进入视角模式";

        var content = new GUIContent(hint);
        Vector2 textSize = _hintStyle.CalcSize(content);
        const float padX = 10f;
        const float padY = 6f;
        var rect = new Rect(16f, 12f, textSize.x + padX * 2f, textSize.y + padY * 2f);

        var oldColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.4f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = oldColor;

        _hintStyle.normal.textColor = _viewMode ? new Color(0.55f, 1f, 0.65f) : new Color(1f, 1f, 1f, 0.9f);

        var textRect = new Rect(rect.x + padX, rect.y + padY, textSize.x, textSize.y);
        GUI.Label(textRect, content, _hintStyle);
    }

    void EnsureHintStyle()
    {
        if (_hintStyle != null)
            return;

        if (!_cjkFontTried)
        {
            _cjkFontTried = true;
            try
            {
                // 默认字体没有中文字形，挑一个系统中文字体。
                _cjkFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial Unicode MS" }, 20);
            }
            catch
            {
                _cjkFont = null;
            }
        }

        _hintStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.016f), 11, 16),
            richText = false,
            wordWrap = false
        };
        if (_cjkFont != null)
            _hintStyle.font = _cjkFont;
    }
}
