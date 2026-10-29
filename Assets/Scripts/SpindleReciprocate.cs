using UnityEngine;

/// <summary>
/// 主轴（蓝色滑块 + 钻头）在演示模式下的左右往返平动。
///
/// 设计要点：
/// 1) 只在自己身上做一段“有界限”的局部位移，不改父级、不改模型、不碰 Ship 里的其它脚本；
/// 2) 行程由 travelMin / travelMax 定义（米，相对进入 Play 时的位置），
///    并额外用机床整机的包围盒做一次收口，保证不会跑出模型范围；
/// 3) 位移曲线是正弦的，两端自然减速，看起来像真机轴的软反向，没有生硬折返。
///
/// 这台机床上轴链里的节点（X轴 / X / Z轴 / Z / 主轴）Local +Y ≈ 屏幕左右方向：
/// 相机沿机床 -X 看（MachineViewController.frontLocalDirection = Vector3.right），
/// 屏幕横向 = 机床 local ±Z；而 FBX 里名为 "Y" 的节点带 180° 旋转，
/// 把子级的 local Y 映射到了机床 +Z，所以 Local Y 就是左右。
///
/// 左右行程是**各自独立**的：travelMin 管一侧、travelMax 管另一侧，改哪个就只动哪一侧。
///
/// 用法：挂在「五轴机床 / Y / Y轴 / X / X轴 / Z / Z轴 / 主轴」任意一个都可（本项目实际挂在 X轴），
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
    [Tooltip("负方向（往左）最远位置。想左行程更大就把这里改得更负，例如 -0.12。")]
    public float travelMin = -0.28f;

    [Tooltip("正方向（往右）最远位置。保持 +0.06 就等于右侧行程不变。")]
    public float travelMax = 0.06f;

    [Min(0.1f)]
    [Tooltip("一个完整来回（min → max → min）的秒数。")]
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

    Vector3 _origin;
    Vector3 _selfCenter;
    Vector3 _selfExtents;
    float _time;
    float _effectiveMin;
    float _effectiveMax;
    bool _running;
    bool _ready;

    /// <summary>当前是否在运动。</summary>
    public bool IsRunning => _running;

    /// <summary>经过包围盒收口后，实际生效的行程下限（米）。</summary>
    public float EffectiveMin => _effectiveMin;

    /// <summary>经过包围盒收口后，实际生效的行程上限（米）。</summary>
    public float EffectiveMax => _effectiveMax;

    void Awake()
    {
        _origin = CurrentPosition;
        CacheSelfBounds();
    }

    void Start()
    {
        ComputeRange();
        _running = playOnStart;
        _time = Mathf.Clamp01(startPhase) * Mathf.Max(cycleSeconds, 0.1f);
        _ready = true;

        Debug.Log($"[SpindleReciprocate] 轴向 {space} {AxisDirection}，" +
                  $"实际行程 {_effectiveMin:F3} ~ {_effectiveMax:F3} m，" +
                  $"周期 {cycleSeconds:F2} s，(min→max→min 算一个周期)", this);
    }

    void Update()
    {
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

    /// <summary>开 / 关平动；关闭时回到初始位置。</summary>
    public void SetRunning(bool run)
    {
        _running = run;
        if (!run)
            SetPosition(_origin);
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
/// 这样即使场景/预制体被重置，也依然默认有平动效果。
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
