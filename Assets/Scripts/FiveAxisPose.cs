using UnityEngine;

/// <summary>
/// 五轴姿态：驱动 A / C 回转轴，并给出刀尖点与刀具矢量的实时读数。
///
/// - A 轴转角绕自身局部 X 轴，C 轴转角绕自身局部 Y 轴（与机床模型的枢轴方向一致）；
/// - 角度由 MachineTimeController 按数据库时段下发，面板滑块可手动覆盖 8 秒；
/// - 托盘安装效果正在自转 C 轴时，本组件让出 C 轴控制权，避免两边打架。
/// </summary>
[DisallowMultipleComponent]
public sealed class FiveAxisPose : MonoBehaviour
{
    [Header("节点路径（相对机床根节点）")]
    public string machineRootName = "五轴机床";
    public string aAxisPath = "A轴";
    public string cAxisPath = "A轴/C轴";
    public string spindlePath = "Y/Y轴/X/X轴/Z/Z轴/主轴";

    [Header("轴向")]
    public Vector3 aAxisLocalAxis = Vector3.right;
    public Vector3 cAxisLocalAxis = Vector3.up;

    [Header("运动")]
    [Tooltip("回转轴最大角速度（度/秒），用于平滑跟随目标角度。")]
    [Min(1f)] public float maxAngularSpeedDegPerSecond = 90f;

    [Tooltip("机床单位换算：1 模型单位 = 多少毫米。")]
    public float unitsPerMillimeter = 0.001f;

    [Tooltip("A 轴行程（度）。")]
    public Vector2 aRange = new Vector2(-120f, 120f);

    [Tooltip("C 轴行程（度）。")]
    public Vector2 cRange = new Vector2(-3600f, 3600f);

    Transform _aAxis;
    Transform _cAxis;
    Transform _spindle;
    Transform _machineRoot;

    Quaternion _restA = Quaternion.identity;
    Quaternion _restC = Quaternion.identity;

    float _targetA;
    float _targetC;
    float _currentA;
    float _currentC;

    float _manualUntil;
    PalletInstallEffect _pallet;

    public float CurrentADeg => _currentA;
    public float CurrentCDeg => _currentC;
    public float TargetADeg => _targetA;
    public float TargetCDeg => _targetC;

    /// <summary>面板刚拖过滑块时为 true，数据库暂时让位。</summary>
    public bool IsManual => Time.time < _manualUntil;

    /// <summary>托盘效果正在驱动 C 轴时为 true，此时本组件不写 C 轴。</summary>
    public bool CTableOwnedByOther => _pallet != null && _pallet.IsTableRunning;

    public bool IsReady => _aAxis != null && _cAxis != null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<FiveAxisPose>() != null)
            return;

        GameObject machine = GameObject.Find("五轴机床");
        if (machine == null)
            return;

        machine.AddComponent<FiveAxisPose>();
    }

    void Awake()
    {
        ResolveNodes();
    }

    void Start()
    {
        if (!IsReady)
            Debug.LogError("[FiveAxisPose] 未找到 A/C 轴节点，五轴姿态不可用。", this);
        else
            Debug.Log($"[FiveAxisPose] A轴={_aAxis.name} C轴={_cAxis.name} 主轴={(_spindle != null ? _spindle.name : "未找到")}", this);
    }

    void ResolveNodes()
    {
        if (_machineRoot == null)
        {
            GameObject machine = GameObject.Find(machineRootName);
            if (machine == null)
            {
                var pose = FindAnyObjectByType<FiveAxisPose>();
                if (pose != null && pose != this)
                    machine = pose.gameObject;
            }

            if (machine != null)
                _machineRoot = machine.transform;
        }

        if (_machineRoot == null)
        {
            Debug.LogError($"[FiveAxisPose] 场景里找不到机床根节点 {machineRootName}。", this);
            return;
        }

        if (_aAxis == null)
            _aAxis = _machineRoot.Find(aAxisPath);
        if (_cAxis == null)
            _cAxis = _machineRoot.Find(cAxisPath);
        if (_spindle == null)
            _spindle = _machineRoot.Find(spindlePath);

        if (_pallet == null)
            _pallet = FindAnyObjectByType<PalletInstallEffect>();

        if (_aAxis != null && !_restCaptured)
        {
            _restA = _aAxis.localRotation;
            _restC = _cAxis != null ? _cAxis.localRotation : Quaternion.identity;
            _restCaptured = true;

            // 初始姿态就是 CSV 的 0 度，先把当前实际角度对齐，避免第一帧跳变。
            _currentA = 0f;
            _currentC = 0f;
        }
    }

    bool _restCaptured;

    /// <summary>数据库下发目标角度（手动优先期间忽略）。</summary>
    public void SetExternalPose(float aDeg, float cDeg)
    {
        if (IsManual)
            return;

        _targetA = Mathf.Clamp(aDeg, aRange.x, aRange.y);
        _targetC = Mathf.Clamp(cDeg, cRange.x, cRange.y);
    }

    /// <summary>面板手动设定角度，8 秒内优先于数据库。</summary>
    public void SetManualPose(float aDeg, float cDeg)
    {
        _targetA = Mathf.Clamp(aDeg, aRange.x, aRange.y);
        _targetC = Mathf.Clamp(cDeg, cRange.x, cRange.y);
        _manualUntil = Time.time + MachineTimeData.ManualHoldSeconds;
    }

    void Update()
    {
        if (!IsReady)
        {
            ResolveNodes();
            return;
        }

        float maxStep = maxAngularSpeedDegPerSecond * Time.deltaTime;
        _currentA = Mathf.MoveTowards(_currentA, _targetA, maxStep);
        _currentC = Mathf.MoveTowards(_currentC, _targetC, maxStep);

        _aAxis.localRotation = Quaternion.AngleAxis(_currentA, aAxisLocalAxis.normalized) * _restA;

        if (!CTableOwnedByOther)
            _cAxis.localRotation = Quaternion.AngleAxis(_currentC, cAxisLocalAxis.normalized) * _restC;
    }

    /// <summary>读取刀尖点与刀具矢量。table 坐标以 C 轴回转中心为原点。</summary>
    public void ReadPose(out Vector3 tcpMachineMm, out Vector3 tcpTableMm, out Vector3 toolVectorInTable)
    {
        tcpMachineMm = Vector3.zero;
        tcpTableMm = Vector3.zero;
        toolVectorInTable = Vector3.down;

        if (!IsReady)
            return;

        float scale = Mathf.Abs(unitsPerMillimeter) < 1e-9f ? 1f : unitsPerMillimeter;

        if (TryGetSpindleTip(out Vector3 worldTip))
        {
            tcpMachineMm = _machineRoot.InverseTransformPoint(worldTip) / scale;
            tcpTableMm = _cAxis.InverseTransformPoint(worldTip) / scale;
        }

        Vector3 worldToolDir = _spindle != null ? -_spindle.up : Vector3.down;
        toolVectorInTable = _cAxis.InverseTransformDirection(worldToolDir).normalized;
    }

    bool TryGetSpindleTip(out Vector3 worldTip)
    {
        worldTip = Vector3.zero;

        if (_spindle == null)
            return false;

        bool hasBounds = false;
        var bounds = new Bounds(_spindle.position, Vector3.zero);

        foreach (var renderer in _spindle.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (renderer == null)
                continue;

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        if (!hasBounds)
        {
            worldTip = _spindle.position;
            return true;
        }

        // 刀尖 = 主轴包围盒底面中心。
        worldTip = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        return true;
    }
}
