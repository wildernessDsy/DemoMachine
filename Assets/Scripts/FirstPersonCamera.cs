using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 第一人称漫游：WASD 移动、空格跳、按住鼠标中键转头、Shift 加速、Esc 暂停。
/// WebGL 下没有系统字体，文字统一走 CjkFont。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class FirstPersonCamera : MonoBehaviour
{
    [Header("移动")]
    [Tooltip("行走速度（米/秒）。")]
    public float moveSpeed = 3.2f;

    [Tooltip("按住 Shift 时的加速倍数。")]
    public float sprintMultiplier = 1.8f;

    [Tooltip("水平加速度（米/秒²）。")]
    public float acceleration = 30f;

    [Header("升降 / 重力")]
    [Tooltip("按 X / Z 时视角升降的速度（米/秒）。")]
    public float verticalSpeed = 2.2f;

    [Tooltip("重力：0 表示悬停，不按 X/Z 时高度保持在当前位置。")]
    public float gravity = 0f;

    [Header("鼠标")]
    [Tooltip("灵敏度倍率，1 = 每像素 0.05 度。")]
    [Range(0.2f, 3f)] public float mouseSensitivity = 1f;

    [Tooltip("每像素转多少度。")]
    public float degreesPerPixel = 0.05f;

    [Tooltip("转头平滑：越大越跟手，越小越顺滑。")]
    [Range(1f, 40f)] public float lookSmoothing = 14f;

    [Tooltip("上下反转。")]
    public bool invertY = false;

    [Tooltip("按住鼠标中键才转动视角，松开放出光标方便点界面。")]
    public bool holdToLook = true;

    [Tooltip("非 holdToLook 模式下，进 Play 就锁定鼠标。")]
    public bool lockCursorOnStart = true;

    [Header("身高")]
    [Tooltip("眼睛离地高度（米）。")]
    public float eyeHeight = 1.75f;

    [Header("外部控制")]
    [Tooltip("关掉后不响应键盘鼠标，并自动释放光标。")]
    public bool controlsEnabled = true;

    [Header("碰撞")]
    [Tooltip("给机床、机械臂、实验室自动补碰撞体，防止穿模穿墙。")]
    public bool addMachineCollider = true;

    [Header("界面")]
    public bool showHints = true;

    [Tooltip("是否显示准星。")]
    public bool showCrosshair = false;

    [Header("走路感")]
    [Tooltip("走路时头部上下起伏的幅度（米）。0 关闭。")]
    public float headBob = 0f;

    [Tooltip("走路时视线的轻微左右倾斜（度）。")]
    public float headBobRoll = 0f;

    [Tooltip("步频（越大走得越快越急）。")]
    public float bobFrequency = 9f;

    float _bobPhase;
    float _appliedBob;
    float _landDip;
    bool _wasGrounded;
    bool _pendingReset;
    Vector3 _spawnPosition;
    float _spawnYaw;
    float _spawnPitch;

    float _yaw;
    float _pitch;
    float _targetYaw;
    float _targetPitch;
    Vector3 _horizontalVelocity;
    float _verticalVelocity;
    float _groundY;
    bool _ready;

    CharacterController _cc;
    GUIStyle _hintStyle;
    static Font _cjkFont;
    static bool _fontTried;

    void Awake()
    {
        _cc = GetComponent<CharacterController>();
        if (_cc == null)
            _cc = gameObject.AddComponent<CharacterController>();

        _cc.height = Mathf.Max(eyeHeight, 1.2f);
        _cc.radius = Mathf.Clamp(_cc.radius, 0.2f, 0.4f);
        _cc.center = new Vector3(0f, -_cc.height * 0.5f, 0f);
        _cc.stepOffset = 0.35f;
        _cc.slopeLimit = 50f;
    }

    void Start()
    {
        if (addMachineCollider)
            EnsureColliders();
    }

    void EnsureReady()
    {
        if (_ready)
            return;

        gameObject.layer = 2;

        float floorY = float.NaN;
        if (Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 100f))
            floorY = hit.point.y;

        if (!float.IsNaN(floorY))
        {
            _groundY = floorY + eyeHeight;
            transform.position = new Vector3(transform.position.x, _groundY, transform.position.z);
        }
        else
        {
            _groundY = transform.position.y;
        }

        Vector3 euler = transform.rotation.eulerAngles;
        _yaw = euler.y;
        _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        _targetYaw = _yaw;
        _targetPitch = _pitch;

        _spawnPosition = transform.position;
        _spawnYaw = _yaw;
        _spawnPitch = _pitch;

        var oldController = GetComponent<MachineViewController>();
        if (oldController != null)
            oldController.enabled = false;

        EnsureFloorCollider(floorY);
        _ready = true;

        if (_pendingReset)
        {
            _pendingReset = false;
            ResetView();
        }
    }

    void EnsureColliders()
    {
        int added = 0;
        foreach (var rootName in new[] { "五轴机床", "RobotArm", "LabEnvironment" })
        {
            var root = GameObject.Find(rootName);
            if (root == null)
                continue;

            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (renderer == null || renderer.GetComponent<Collider>() != null)
                    continue;
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
                    continue;

                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;

                var box = renderer.gameObject.AddComponent<BoxCollider>();
                Bounds local = meshFilter.sharedMesh.bounds;
                box.center = local.center;
                box.size = local.size;
                added++;
            }
        }

        if (added > 0)
            Debug.Log($"[第一人称] 补了 {added} 个碰撞体");
    }

    void EnsureFloorCollider(float groundY)
    {
        float floorY = float.IsNaN(groundY) ? _groundY - 2.2f : groundY - 0.05f;
        var floor = new GameObject("AutoFloorCollider");
        floor.transform.position = new Vector3(transform.position.x, floorY, transform.position.z);
        var box = floor.AddComponent<BoxCollider>();
        box.size = new Vector3(400f, 0.4f, 400f);
    }

    void LateUpdate()
    {
        if (!_ready)
        {
            EnsureReady();
            if (!_ready)
                return;
        }

        if (!controlsEnabled)
        {
            if (Cursor.lockState != CursorLockMode.None)
                UnlockCursor();
            return;
        }

        HandleCursor();
        HandleLook();
        SmoothLook(Time.deltaTime);

        var keyboard = Keyboard.current;
        Vector3 wish = Vector3.zero;
        bool sprint = false;
        float verticalInput = 0f;

        if (keyboard != null)
        {
            float forward = (keyboard[Key.W].isPressed ? 1f : 0f) - (keyboard[Key.S].isPressed ? 1f : 0f);
            float strafe = (keyboard[Key.D].isPressed ? 1f : 0f) - (keyboard[Key.A].isPressed ? 1f : 0f);

            Quaternion yawOnly = Quaternion.Euler(0f, _yaw, 0f);
            wish = yawOnly * Vector3.forward * forward + yawOnly * Vector3.right * strafe;
            if (wish.sqrMagnitude > 1f)
                wish.Normalize();

            sprint = keyboard[Key.LeftShift].isPressed || keyboard[Key.RightShift].isPressed;
            verticalInput = (keyboard[Key.X].isPressed ? 1f : 0f) - (keyboard[Key.Z].isPressed ? 1f : 0f);
        }

        float speed = moveSpeed * (sprint ? sprintMultiplier : 1f);
        _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, wish * speed, acceleration * Time.deltaTime);

        bool grounded = _cc != null && _cc.isGrounded;

        if (Mathf.Abs(verticalInput) > 0.01f)
        {
            // X 升高、Z 降低，按住持续平移视角高度
            _verticalVelocity = verticalInput * verticalSpeed;
        }
        else
        {
            if (grounded && _verticalVelocity < 0f)
                _verticalVelocity = -1f;

            if (gravity <= 0.01f && !grounded)
                _verticalVelocity = Mathf.MoveTowards(_verticalVelocity, 0f, verticalSpeed * 6f * Time.deltaTime);
            else
                _verticalVelocity -= gravity * Time.deltaTime;
        }

        Vector3 delta = _horizontalVelocity * Time.deltaTime + Vector3.up * (_verticalVelocity * Time.deltaTime);

        if (_cc != null)
            _cc.Move(delta);
        else
            transform.position += delta;

        if (transform.position.y < _groundY - 5f)
        {
            transform.position = new Vector3(transform.position.x, _groundY, transform.position.z);
            _verticalVelocity = 0f;
        }

        // —— 第一人称“走路感” ——
        float planarSpeed = new Vector2(_horizontalVelocity.x, _horizontalVelocity.z).magnitude;
        float speedRatio = Mathf.Clamp01(planarSpeed / Mathf.Max(0.01f, moveSpeed));

        if (grounded && planarSpeed > 0.15f)
            _bobPhase += Time.deltaTime * bobFrequency * (0.6f + speedRatio);
        else
            _bobPhase = Mathf.MoveTowards(_bobPhase % (Mathf.PI * 2f), 0f, Time.deltaTime * 4f);

        // 落地瞬间的下沉
        if (grounded && !_wasGrounded && _verticalVelocity < -1.5f)
            _landDip = Mathf.Min(0.12f, Mathf.Abs(_verticalVelocity) * 0.02f);
        _wasGrounded = grounded;
        _landDip = Mathf.MoveTowards(_landDip, 0f, Time.deltaTime * 0.6f);

        float bob = Mathf.Sin(_bobPhase) * headBob * speedRatio - _landDip;
        float roll = Mathf.Sin(_bobPhase * 0.5f) * headBobRoll * speedRatio;

        // 只应用与上一帧的差值，避免视觉起伏累加成真实位移
        transform.position += Vector3.up * (bob - _appliedBob);
        _appliedBob = bob;
        transform.rotation = Quaternion.Euler(_pitch, _yaw, roll);
    }

    void HandleCursor()
    {
        // 不锁光标：避免浏览器弹出“按 Esc 显示光标”，也避免松开后焦点丢失。
    }

    void HandleLook()
    {
        var mouse = Mouse.current;
        if (mouse == null)
            return;

        // 只有按住中键（或非 holdToLook 模式下的左键）才转动视角，平时光标可自由点界面。
        bool looking = holdToLook ? mouse.middleButton.isPressed : mouse.leftButton.isPressed;
        if (!looking)
            return;

        Vector2 delta = mouse.delta.ReadValue();
        float degrees = degreesPerPixel * mouseSensitivity;
        float signY = invertY ? 1f : -1f;

        _targetYaw += delta.x * degrees;
        _targetPitch = Mathf.Clamp(_targetPitch + signY * delta.y * degrees, -88f, 88f);
    }

    void SmoothLook(float dt)
    {
        if (lookSmoothing <= 0.01f)
        {
            _yaw = _targetYaw;
            _pitch = _targetPitch;
            return;
        }

        float k = 1f - Mathf.Exp(-lookSmoothing * dt);
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, k);
        _pitch = Mathf.LerpAngle(_pitch, _targetPitch, k);
    }

    void DrawCrosshair()
    {
        float c = Mathf.Clamp(Screen.height / 900f, 0.9f, 1.8f);
        float len = 8f * c;
        float gap = 4f * c;
        float thick = 2f * c;

        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;

        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.75f);
        GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy - gap - len, thick, len), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy + gap, thick, len), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - gap - len, cy - thick * 0.5f, len, thick), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx + gap, cy - thick * 0.5f, len, thick), Texture2D.whiteTexture);
        GUI.color = old;
    }

    public void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    /// <summary>回到进入场景时的机位与朝向。</summary>
    public bool IsReady => _ready;

    public void ResetView()
    {
        if (!_ready)
        {
            // 场景还没初始化完，等 TakeOver 结束后再复位
            _pendingReset = true;
            return;
        }

        _spawnPosition.y = Mathf.Max(_spawnPosition.y, _groundY);

        if (_cc != null)
            _cc.enabled = false;

        transform.position = new Vector3(_spawnPosition.x, _groundY, _spawnPosition.z);

        if (_cc != null)
            _cc.enabled = true;

        _yaw = _spawnYaw;
        _pitch = _spawnPitch;
        _targetYaw = _spawnYaw;
        _targetPitch = _spawnPitch;
        _horizontalVelocity = Vector3.zero;
        _verticalVelocity = 0f;
        _appliedBob = 0f;
        _landDip = 0f;

        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    public void UnlockCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void OnGUI()
    {
        if (!showHints || !controlsEnabled)
            return;

        if (showCrosshair)
            DrawCrosshair();

        EnsureStyle();

        string hint = holdToLook
            ? "按住鼠标中键转头 · WASD 移动 · X/Z 升降视角 · Shift 加速 · Esc 暂停"
            : "点击画面锁定鼠标 · WASD 移动 · X/Z 升降视角 · Shift 加速";

        var content = new GUIContent(hint);
        Vector2 size = _hintStyle.CalcSize(content);
        const float padX = 10f;
        const float padY = 6f;
        // 动态字体测量会偏小，额外留出余量，避免最后几个字被裁掉
        float textWidth = size.x + 16f;
        var rect = new Rect(16f, 12f, textWidth + padX * 2f, size.y + padY * 2f);

        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.45f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;

        GUI.Label(new Rect(rect.x + padX, rect.y + padY, textWidth, size.y), content, _hintStyle);
    }

    void EnsureStyle()
    {
        if (_hintStyle != null)
            return;

        if (!_fontTried)
        {
            _fontTried = true;
            _cjkFont = CjkFont.Get();
        }

        _hintStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.016f), 11, 16),
            normal = { textColor = new Color(1f, 1f, 1f, 0.9f) }
        };
        if (_cjkFont != null)
            _hintStyle.font = _cjkFont;
    }
}
