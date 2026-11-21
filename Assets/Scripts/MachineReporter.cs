using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// 定时把当前机器的转速、主轴速度、工作台坐标推给网页（window.__pushMachine），
/// 网页再 POST 到服务器存进 MySQL。
/// </summary>
public sealed class MachineReporter : MonoBehaviour
{
    [Tooltip("上报间隔（秒）。")]
    public float interval = 2f;

    [Tooltip("未选择机器时是否也上报。")]
    public bool reportInSelect = false;

    MachineSession _session;
    DrillController _drill;
    SpindleReciprocate _spindle;
    PlatformController _platform;
    float _timer;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    static extern void MachineBridge_Push(int machine, float rpm, float spindle, float px, float py, float pz);
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindAnyObjectByType<MachineReporter>() != null)
            return;

        var go = new GameObject("MachineReporter");
        go.AddComponent<MachineReporter>();
    }

    void Awake()
    {
        _session = FindAnyObjectByType<MachineSession>();
        _drill = FindAnyObjectByType<DrillController>();
        _spindle = FindAnyObjectByType<SpindleReciprocate>();
        _platform = FindAnyObjectByType<PlatformController>();
    }

    void Update()
    {
        _timer += Time.unscaledDeltaTime;
        if (_timer < interval)
            return;

        _timer = 0f;

        if (!reportInSelect && _session != null && _session.InSelect)
            return;

        int machine = (_session != null ? _session.CurrentIndex : 0) + 1;
        float rpm = _drill != null ? _drill.CurrentRpm : 0f;
        float spindle = _spindle != null ? _spindle.AverageSpeedMms : 0f;
        Vector3 o = _platform != null ? _platform.CurrentOffset : Vector3.zero;

#if UNITY_WEBGL && !UNITY_EDITOR
        MachineBridge_Push(machine, rpm, spindle, o.x * 1000f, o.y * 1000f, o.z * 1000f);
#else
        Debug.Log($"[上报] machine={machine} rpm={rpm:F1} spindle={spindle:F1} xyz={o}");
#endif
    }
}
