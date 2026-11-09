using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 三台机器的会话状态：共用同一份模型，只保存各自的参数。
/// HTML 选择界面通过 SendMessage 调用 SelectMachine / ReturnToSelect。
/// </summary>
[DisallowMultipleComponent]
public sealed class MachineSession : MonoBehaviour
{
    [Serializable]
    public class MachineState
    {
        public string name = "机器";
        public float drillRpm = 30f;
        public float spindleSpeedMms = 316f;
        public float platformSpeed = 0.06f;
        public Vector3 platformOffset;
    }

    [Header("机器")]
    public MachineState[] machines = new MachineState[3];

    [Header("场景引用（留空自动查找）")]
    public DrillController drill;
    public SpindleReciprocate spindle;
    public PlatformController platform;
    public FirstPersonCamera playerCamera;

    [Header("行为")]
    [Tooltip("进入选择界面时冻结时间。")]
    public bool freezeTimeInSelect = true;

    int _current = -1;
    bool _inSelect = true;

    public int CurrentIndex => _current;
    public bool InSelect => _inSelect;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<MachineSession>() != null)
            return;

        var go = new GameObject("MachineSession");
        go.AddComponent<MachineSession>();
    }

    void Awake()
    {
        if (machines == null || machines.Length < 3)
            machines = new[] { new MachineState(), new MachineState(), new MachineState() };

        for (int i = 0; i < machines.Length; i++)
        {
            if (machines[i] == null)
                machines[i] = new MachineState();
            if (string.IsNullOrEmpty(machines[i].name))
                machines[i].name = "机器 " + (i + 1);
        }

        if (drill == null) drill = FindAnyObjectByType<DrillController>();
        if (spindle == null) spindle = FindAnyObjectByType<SpindleReciprocate>();
        if (platform == null) platform = FindAnyObjectByType<PlatformController>();
        if (playerCamera == null) playerCamera = FindAnyObjectByType<FirstPersonCamera>();
    }

    void Start()
    {
        // 编辑器里没有 HTML 选择界面，自动进入 1 号机方便调试
        if (Application.isEditor)
        {
            SelectMachine("0");
            return;
        }

        EnterSelectMode();
    }

    void Update()
    {
        if (!Application.isEditor)
            return;

        var kb = Keyboard.current;
        if (kb == null)
            return;

        if (kb[Key.Digit1].wasPressedThisFrame) SelectMachine("0");
        if (kb[Key.Digit2].wasPressedThisFrame) SelectMachine("1");
        if (kb[Key.Digit3].wasPressedThisFrame) SelectMachine("2");
        if (kb[Key.Digit0].wasPressedThisFrame) ReturnToSelect();
    }

    /// <summary>HTML 调用：unityInstance.SendMessage("MachineSession", "SelectMachine", "0")</summary>
    public void SelectMachine(string index)
    {
        if (!int.TryParse(index, out int idx))
            idx = 0;

        if (idx < 0 || idx >= machines.Length)
            return;

        if (_current >= 0 && _current < machines.Length)
            Capture(_current, machines[_current]);

        _current = idx;
        _inSelect = false;

        Apply(machines[idx]);

        if (freezeTimeInSelect)
            Time.timeScale = 1f;

        if (playerCamera != null)
        {
            playerCamera.controlsEnabled = true;
            if (playerCamera.IsReady)
                playerCamera.ResetView();
            else
                StartCoroutine(ResetWhenReady());
        }
    }

    /// <summary>HTML 调用：unityInstance.SendMessage("MachineSession", "ReturnToSelect")</summary>
    [ContextMenu("回到选择界面")]
    public void ReturnToSelect()
    {
        if (_current >= 0 && _current < machines.Length)
            Capture(_current, machines[_current]);

        _current = -1;
        EnterSelectMode();
    }

    System.Collections.IEnumerator ResetWhenReady()
    {
        // 等待相机完成场景初始化（最多 120 帧）
        for (int i = 0; i < 120 && playerCamera != null && !playerCamera.IsReady; i++)
            yield return null;

        if (playerCamera != null)
        {
            playerCamera.controlsEnabled = true;
            playerCamera.ResetView();
        }
    }

    void EnterSelectMode()
    {
        _inSelect = true;

        if (freezeTimeInSelect)
            Time.timeScale = 0f;

        if (playerCamera != null)
            playerCamera.controlsEnabled = false;
    }

    void Capture(int index, MachineState state)
    {
        if (state == null) return;

        if (drill != null) state.drillRpm = drill.CurrentRpm;
        if (spindle != null) state.spindleSpeedMms = spindle.AverageSpeedMms;
        if (platform != null)
        {
            state.platformSpeed = platform.speed;
            state.platformOffset = platform.CurrentOffset;
        }
    }

    void Apply(MachineState state)
    {
        if (state == null) return;

        if (drill != null) drill.SetExternalRpm(state.drillRpm);
        if (spindle != null)
        {
            spindle.SetAverageSpeedMms(state.spindleSpeedMms);
            spindle.SetRunning(true);
        }
        if (platform != null)
        {
            platform.speed = state.platformSpeed;
            platform.SetOffset(state.platformOffset);

            // 进入后立刻自动演示，不用手动点按钮
            if (platform.playAutoDemoOnStart)
                platform.autoDemo = true;

        }
    }
}
