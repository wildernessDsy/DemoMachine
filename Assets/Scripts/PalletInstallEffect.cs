using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 托盘安装 + 转台运行演示。
/// 1) 用代码生成一块方块托盘，从上方降下、转正对位、落位压实；
/// 2) 装好后 C 轴转台开始回转，托盘随台面一起转；
/// 3) 可选循环：转完 → 拆下 → 再装。
/// 托盘挂在 C 轴枢轴下，落位高度由向下射线探测转台真实几何得到，无需手填坐标。
/// </summary>
[DisallowMultipleComponent]
public sealed class PalletInstallEffect : MonoBehaviour
{
    enum Phase { Idle, Approach, Settle, Installed, Withdraw }

    const float UnitsPerMillimeter = 0.001f;

    [Header("挂载目标")]
    [Tooltip("转台枢轴，留空时按 machineRootName + tablePath 自动查找")]
    public Transform tableRoot;

    [Tooltip("相对机床根节点的层级路径")]
    public string tablePath = "A轴/C轴";

    [Tooltip("场景中的机床根节点名称")]
    public string machineRootName = "五轴机床";

    [Header("托盘尺寸 (mm)")]
    [Min(20f)] public float palletLengthMm = 320f;
    [Min(20f)] public float palletWidthMm = 320f;
    [Min(4f)] public float palletThicknessMm = 55f;

    [Tooltip("托盘超出转台时自动缩小，避免边缘悬空")]
    public bool autoClampToTable = true;

    [Tooltip("落位后托盘底面与支撑面之间保留的间隙（mm）")]
    public float seatClearanceMm = 1.5f;

    [Header("安装动画")]
    [Tooltip("起始时托盘底面高于落位面的距离（mm）")]
    [Min(20f)] public float startHeightMm = 250f;

    [Tooltip("下降 + 转正所用时间（秒）")]
    [Min(0.05f)] public float approachDuration = 1.8f;

    [Tooltip("落位后的压实动作时长（秒），0 = 不要压实")]
    [Min(0f)] public float settleDuration = 0.45f;

    [Tooltip("压实动作的下沉量（mm）")]
    [Min(0f)] public float settleDepthMm = 4f;

    [Tooltip("起始偏转角（度），落下过程中转到 0 完成对位")]
    public float startYawDegrees = -32f;

    [Tooltip("进入 Play 后自动播放一次")]
    public bool autoPlayOnStart = true;

    [Header("转台运行")]
    [Tooltip("安装完成后自动转动 C 轴转台")]
    public bool runTableAfterInstall = true;

    [Tooltip("转台角速度（度/秒）")]
    public float tableDegreesPerSecond = 40f;

    [Tooltip("转台连续运行时长（秒），0 = 一直转")]
    [Min(0f)] public float tableRunSeconds = 8f;

    [Header("循环")]
    [Tooltip("循环演示：装上 → 转台运行 → 拆下 → 重复")]
    public bool loop;

    [Min(0f)] public float loopHoldSeconds = 1.2f;

    [Header("外观")]
    public Color palletColor = new Color(0.76f, 0.77f, 0.81f, 1f);
    public Color padColor = new Color(0.28f, 0.3f, 0.34f, 1f);
    public bool addCollider;

    [Header("调试")]
    public bool logSeatProbe = true;

    /// <summary>托盘落位完成时触发。</summary>
    public event Action OnLanded;

    /// <summary>托盘拆下完成时触发。</summary>
    public event Action OnRemoved;

    Transform _table;
    GameObject _palletRoot;
    Material _palletMaterial;
    Material _padMaterial;

    Vector3 _restLocalPosition;
    float _startLocalY;
    float _palletThickness;
    float _resolvedLengthMm;
    float _resolvedWidthMm;
    float _seatLocalY;

    Quaternion _tableRestRotation = Quaternion.identity;
    float _tableAngle;
    float _tableRunElapsed;

    Phase _phase = Phase.Idle;
    float _timer;
    bool _ready;

    readonly List<MeshRenderer> _tableRenderers = new List<MeshRenderer>();
    readonly List<Vector3> _vertBuffer = new List<Vector3>();
    readonly List<int> _indexBuffer = new List<int>();

    public bool IsInstalled => _phase == Phase.Installed;
    public GameObject Pallet => _palletRoot;
    public float TableAngle => _tableAngle;

    void Awake()
    {
        ResolveTable();
    }

    void Start()
    {
        if (_table == null)
        {
            enabled = false;
            return;
        }

        BuildPallet();

        if (autoPlayOnStart)
            PlayInstall();
        else
            ResetToStartPose();
    }

    void OnDestroy()
    {
        DestroyRuntimeObject(_palletMaterial);
        DestroyRuntimeObject(_padMaterial);
    }

    static void DestroyRuntimeObject(UnityEngine.Object target)
    {
        if (target == null)
            return;

        if (Application.isPlaying)
            Destroy(target);
        else
            DestroyImmediate(target);
    }

    void ResolveTable()
    {
        if (tableRoot != null)
        {
            _table = tableRoot;
            CollectTableRenderers();
            return;
        }

        GameObject machine = GameObject.Find(machineRootName);

        if (machine == null)
        {
            foreach (Transform candidate in FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
            {
                if (candidate.name != machineRootName)
                    continue;
                machine = candidate.gameObject;
                break;
            }
        }

        if (machine == null)
        {
            Debug.LogError($"[托盘安装] 场景里找不到名为 {machineRootName} 的机床根节点。", this);
            return;
        }

        Transform table = machine.transform.Find(tablePath);
        if (table == null)
        {
            Debug.LogError($"[托盘安装] 在 {machineRootName} 下找不到 {tablePath}。", this);
            return;
        }

        _table = table;
        tableRoot = table;
        CollectTableRenderers();
    }

    void CollectTableRenderers()
    {
        _tableRenderers.Clear();
        _tableRenderers.AddRange(_table.GetComponentsInChildren<MeshRenderer>(true));
    }

    void BuildPallet()
    {
        _tableRestRotation = _table.localRotation;
        _palletThickness = palletThicknessMm * UnitsPerMillimeter;
        _resolvedLengthMm = palletLengthMm;
        _resolvedWidthMm = palletWidthMm;

        Bounds tableBounds;
        bool hasBounds = TryGetBounds(_table, out tableBounds);

        if (autoClampToTable && hasBounds)
            ClampToTable(tableBounds);

        ProbeSeat(tableBounds, hasBounds);

        Vector3 seatCenter = hasBounds
            ? _table.InverseTransformPoint(new Vector3(tableBounds.center.x, tableBounds.center.y, tableBounds.center.z))
            : Vector3.zero;

        _restLocalPosition = new Vector3(
            seatCenter.x,
            _seatLocalY + _palletThickness * 0.5f + seatClearanceMm * UnitsPerMillimeter,
            seatCenter.z);

        _startLocalY = _restLocalPosition.y + startHeightMm * UnitsPerMillimeter;

        _palletRoot = new GameObject("托盘");
        _palletRoot.transform.SetParent(_table, false);

        _palletMaterial = CreateMaterial(palletColor, 0.35f);
        _padMaterial = CreateMaterial(padColor, 0.2f);

        float length = _resolvedLengthMm * UnitsPerMillimeter;
        float width = _resolvedWidthMm * UnitsPerMillimeter;

        CreateBox("托盘底板", _palletRoot.transform, new Vector3(length, _palletThickness, width), _palletMaterial, Vector3.zero);

        float padHeight = Mathf.Max(0.004f, _palletThickness * 0.16f);
        CreateBox("托盘台面", _palletRoot.transform,
            new Vector3(length * 0.86f, padHeight, width * 0.86f),
            _padMaterial,
            new Vector3(0f, _palletThickness * 0.5f + padHeight * 0.5f, 0f));

        _ready = true;
        ResetToStartPose();
    }

    void ClampToTable(Bounds tableBounds)
    {
        // 留 15% 余量，避免托盘边缘悬在转台外面。
        float maxLength = tableBounds.size.x / UnitsPerMillimeter * 0.85f;
        float maxWidth = tableBounds.size.z / UnitsPerMillimeter * 0.85f;

        if (maxLength > 20f)
            _resolvedLengthMm = Mathf.Min(_resolvedLengthMm, maxLength);
        if (maxWidth > 20f)
            _resolvedWidthMm = Mathf.Min(_resolvedWidthMm, maxWidth);

        if (logSeatProbe && (_resolvedLengthMm < palletLengthMm - 0.5f || _resolvedWidthMm < palletWidthMm - 0.5f))
            Debug.Log($"[托盘安装] 托盘已自动缩小到 {_resolvedLengthMm:F0} × {_resolvedWidthMm:F0} mm 以适配转台尺寸。", this);
    }

    void ProbeSeat(Bounds tableBounds, bool hasBounds)
    {
        float halfLength = _resolvedLengthMm * 0.5f * UnitsPerMillimeter * 0.8f;
        float halfWidth = _resolvedWidthMm * 0.5f * UnitsPerMillimeter * 0.8f;

        Vector3 center = hasBounds ? tableBounds.center : _table.position;
        float startWorldY = (hasBounds ? tableBounds.max.y : _table.position.y) + 5f;

        var samples = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(-halfLength, -halfWidth),
            new Vector2(-halfLength, halfWidth),
            new Vector2(halfLength, -halfWidth),
            new Vector2(halfLength, halfWidth)
        };

        float bestLocalY = float.NegativeInfinity;

        foreach (Vector2 sample in samples)
        {
            var origin = new Vector3(center.x + sample.x, startWorldY, center.z + sample.y);

            if (!TryRaycastDown(origin, out Vector3 hitPoint))
                continue;

            float localY = _table.InverseTransformPoint(hitPoint).y;
            if (localY > bestLocalY)
                bestLocalY = localY;
        }

        if (float.IsNegativeInfinity(bestLocalY))
        {
            bestLocalY = hasBounds
                ? _table.InverseTransformPoint(new Vector3(center.x, tableBounds.max.y, center.z)).y
                : 0f;

            Debug.LogWarning($"[托盘安装] 射线未命中转台几何，改用包围盒顶面：局部 y = {bestLocalY:F4}", this);
        }
        else if (logSeatProbe)
        {
            Vector3 worldSeat = _table.TransformPoint(new Vector3(0f, bestLocalY, 0f));
            Debug.Log($"[托盘安装] 落位面探测完成：局部 y = {bestLocalY:F4}（世界 y = {worldSeat.y:F4}），" +
                      $"托盘 {_resolvedLengthMm:F0} × {_resolvedWidthMm:F0} × {palletThicknessMm:F0} mm。", this);
        }

        _seatLocalY = bestLocalY;
    }

    void CreateBox(string objectName, Transform parent, Vector3 size, Material material, Vector3 localPosition)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = objectName;
        box.transform.SetParent(parent, false);
        box.transform.localPosition = localPosition;
        box.transform.localRotation = Quaternion.identity;
        box.transform.localScale = size;

        var collider = box.GetComponent<Collider>();
        if (collider != null && !addCollider)
            DestroyRuntimeObject(collider);

        var renderer = box.GetComponent<MeshRenderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;
    }

    static Material CreateMaterial(Color color, float smoothness)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");

        var material = new Material(shader) { name = "PalletRuntime" };

        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
        if (material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Glossiness"))
            material.SetFloat("_Glossiness", smoothness);
        if (material.HasProperty("_Metallic"))
            material.SetFloat("_Metallic", 0.1f);

        return material;
    }

    bool TryGetBounds(Transform root, out Bounds bounds)
    {
        bool hasBounds = false;
        bounds = new Bounds(root.position, Vector3.zero);

        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
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

        return hasBounds;
    }

    bool TryRaycastDown(Vector3 worldOrigin, out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;
        float nearest = float.PositiveInfinity;
        var worldRay = new Ray(worldOrigin, Vector3.down);

        foreach (MeshRenderer renderer in _tableRenderers)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;
            if (!renderer.bounds.IntersectRay(worldRay, out _))
                continue;

            var filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
                continue;

            Transform t = renderer.transform;
            Vector3 origin = t.InverseTransformPoint(worldOrigin);
            Vector3 direction = t.InverseTransformDirection(Vector3.down).normalized;

            if (!TryIntersectMesh(mesh, origin, direction, out float localDistance))
                continue;

            Vector3 worldHit = t.TransformPoint(origin + direction * localDistance);
            float distance = Vector3.Distance(worldOrigin, worldHit);
            if (distance < nearest)
            {
                nearest = distance;
                hitPoint = worldHit;
            }
        }

        return !float.IsPositiveInfinity(nearest);
    }

    bool TryIntersectMesh(Mesh mesh, Vector3 origin, Vector3 direction, out float nearest)
    {
        nearest = float.PositiveInfinity;

        try
        {
            if (mesh.isReadable)
            {
                mesh.GetVertices(_vertBuffer);
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    mesh.GetTriangles(_indexBuffer, subMesh, true);
                    for (int i = 0; i + 2 < _indexBuffer.Count; i += 3)
                    {
                        if (RayTriangle(origin, direction, _vertBuffer[_indexBuffer[i]], _vertBuffer[_indexBuffer[i + 1]], _vertBuffer[_indexBuffer[i + 2]], out float hit) && hit < nearest)
                            nearest = hit;
                    }
                }

                return true;
            }

            using (Mesh.MeshDataArray array = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                Mesh.MeshData data = array[0];
                var vertices = data.GetVertexData<Vector3>();

                if (data.indexFormat == IndexFormat.UInt16)
                {
                    var indices = data.GetIndexData<ushort>();
                    for (int subMesh = 0; subMesh < data.subMeshCount; subMesh++)
                    {
                        SubMeshDescriptor descriptor = data.GetSubMesh(subMesh);
                        int end = descriptor.indexStart + descriptor.indexCount;

                        for (int i = descriptor.indexStart; i + 2 < end; i += 3)
                        {
                            if (RayTriangle(origin, direction, vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], out float hit) && hit < nearest)
                                nearest = hit;
                        }
                    }
                }
                else
                {
                    var indices = data.GetIndexData<int>();
                    for (int subMesh = 0; subMesh < data.subMeshCount; subMesh++)
                    {
                        SubMeshDescriptor descriptor = data.GetSubMesh(subMesh);
                        int end = descriptor.indexStart + descriptor.indexCount;

                        for (int i = descriptor.indexStart; i + 2 < end; i += 3)
                        {
                            if (RayTriangle(origin, direction, vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], out float hit) && hit < nearest)
                                nearest = hit;
                        }
                    }
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[托盘安装] 网格 {mesh.name} 无法读取，落位面探测跳过它：{exception.Message}", this);
            return false;
        }
    }

    static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 v0, Vector3 v1, Vector3 v2, out float distance)
    {
        distance = 0f;

        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);

        if (Mathf.Abs(determinant) < 1e-9f)
            return false;

        float inverse = 1f / determinant;
        Vector3 fromV0 = origin - v0;

        float u = Vector3.Dot(fromV0, p) * inverse;
        if (u < 0f || u > 1f)
            return false;

        Vector3 q = Vector3.Cross(fromV0, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f)
            return false;

        float t = Vector3.Dot(edge2, q) * inverse;
        if (t < 0f)
            return false;

        distance = t;
        return true;
    }

    /// <summary>播放安装动画：从上方降下并转正，然后落位压实。</summary>
    public void PlayInstall()
    {
        if (!_ready)
            return;

        StopTable();
        _palletRoot.transform.localPosition = new Vector3(_restLocalPosition.x, _startLocalY, _restLocalPosition.z);
        _palletRoot.transform.localRotation = Quaternion.Euler(0f, startYawDegrees, 0f);

        _phase = Phase.Approach;
        _timer = 0f;
    }

    /// <summary>播放拆下动画：抬回起始高度并转回起始角度。</summary>
    public void PlayRemove()
    {
        if (!_ready)
            return;

        _phase = Phase.Withdraw;
        _timer = 0f;
    }

    /// <summary>立即回到起始悬空姿态。</summary>
    public void ResetToStartPose()
    {
        if (!_ready)
            return;

        _phase = Phase.Idle;
        _timer = 0f;
        _palletRoot.transform.localPosition = new Vector3(_restLocalPosition.x, _startLocalY, _restLocalPosition.z);
        _palletRoot.transform.localRotation = Quaternion.Euler(0f, startYawDegrees, 0f);
    }

    /// <summary>立即停在当前转台角度。</summary>
    public void StopTable()
    {
        _tableRunElapsed = 0f;
        _tableRunning = false;
    }

    bool _tableRunning;

    void Update()
    {
        if (!_ready)
            return;

        UpdateInstallAnimation();
        UpdateTableMotion();
    }

    void UpdateInstallAnimation()
    {
        switch (_phase)
        {
            case Phase.Approach:
            {
                _timer += Time.deltaTime;
                float t = approachDuration > 0f ? Mathf.Clamp01(_timer / approachDuration) : 1f;
                float eased = 1f - Mathf.Pow(1f - t, 3f);

                Vector3 position = _restLocalPosition;
                position.y = Mathf.Lerp(_startLocalY, _restLocalPosition.y, eased);
                _palletRoot.transform.localPosition = position;
                _palletRoot.transform.localRotation = Quaternion.Euler(0f, Mathf.Lerp(startYawDegrees, 0f, eased), 0f);

                if (t < 1f)
                    break;

                _timer = 0f;

                if (settleDuration > 0f && settleDepthMm > 0f)
                {
                    _phase = Phase.Settle;
                }
                else
                {
                    _palletRoot.transform.localPosition = _restLocalPosition;
                    _palletRoot.transform.localRotation = Quaternion.identity;
                    MarkInstalled();
                }

                break;
            }

            case Phase.Settle:
            {
                _timer += Time.deltaTime;
                float t = settleDuration > 0f ? Mathf.Clamp01(_timer / settleDuration) : 1f;
                float sink = Mathf.Sin(t * Mathf.PI) * settleDepthMm * UnitsPerMillimeter;

                Vector3 position = _restLocalPosition;
                position.y -= sink;
                _palletRoot.transform.localPosition = position;

                if (t < 1f)
                    break;

                _palletRoot.transform.localPosition = _restLocalPosition;
                _palletRoot.transform.localRotation = Quaternion.identity;
                MarkInstalled();

                break;
            }

            case Phase.Installed:
            {
                if (!loop)
                    break;

                _timer += Time.deltaTime;
                bool tableFinished = !runTableAfterInstall || tableRunSeconds > 0f && !_tableRunning;

                if (_timer >= loopHoldSeconds && tableFinished)
                    PlayRemove();

                break;
            }

            case Phase.Withdraw:
            {
                _timer += Time.deltaTime;
                float t = approachDuration > 0f ? Mathf.Clamp01(_timer / approachDuration) : 1f;
                float eased = t * t * t;

                Vector3 position = _restLocalPosition;
                position.y = Mathf.Lerp(_restLocalPosition.y, _startLocalY, eased);
                _palletRoot.transform.localPosition = position;
                _palletRoot.transform.localRotation = Quaternion.Euler(0f, Mathf.Lerp(0f, startYawDegrees, eased), 0f);

                if (t < 1f)
                    break;

                _phase = Phase.Idle;
                _timer = 0f;
                OnRemoved?.Invoke();

                break;
            }

            case Phase.Idle:
            {
                if (!loop)
                    break;

                _timer += Time.deltaTime;
                if (_timer >= loopHoldSeconds)
                    PlayInstall();

                break;
            }
        }
    }

    void MarkInstalled()
    {
        _phase = Phase.Installed;
        _timer = 0f;

        if (runTableAfterInstall)
        {
            _tableRunning = true;
            _tableRunElapsed = 0f;
        }

        OnLanded?.Invoke();
    }

    void UpdateTableMotion()
    {
        if (!_tableRunning)
            return;

        float delta = tableDegreesPerSecond * Time.deltaTime;
        _tableAngle = Mathf.Repeat(_tableAngle + delta, 360f);
        _table.localRotation = Quaternion.AngleAxis(_tableAngle, Vector3.up) * _tableRestRotation;

        if (tableRunSeconds > 0f)
        {
            _tableRunElapsed += Time.deltaTime;
            if (_tableRunElapsed >= tableRunSeconds)
            {
                _tableRunning = false;
                if (logSeatProbe)
                    Debug.Log($"[托盘安装] 转台运行 {tableRunSeconds:F1} 秒完成，累计转过 {_tableAngle:F0}°。", this);
            }
        }
    }

    void OnValidate()
    {
        if (approachDuration < 0.05f)
            approachDuration = 0.05f;
    }
}
