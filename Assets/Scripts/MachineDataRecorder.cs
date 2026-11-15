using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// 把“演示过程中真正跑出来的实时参数”按 15 分钟时段采样，并持久化到本地。
///
/// 静态网站和动态网站的区别就在这里：
/// 原来的做法是把 machine_parameters.csv 打包进 WebGL，参数永远是同一份；
/// 现在演示时的每一项读数都会被采样、按时段做加权平均、写进浏览器 IndexedDB，
/// 下次打开同一个站点会先读回这份存档，于是“今天演示过的当日实时参数”被保留下来了。
///
/// 采样规则：
/// - 只在选中了某台机器、且时间没有暂停时采样；
/// - 每 sampleInterval 秒采一次，落到模拟时钟当前所在的 15 分钟格子里；
/// - 同一个格子多次采样做加权平均，权重是该次采样的真实时长；
/// - 用户拖过滑块（手动优先）的时段会被标记 manual，界面上可以看出来。
///
/// 落盘规则：
/// - 最多每 saveInterval 秒写一次 PlayerPrefs，避免每帧写 IndexedDB；
/// - 切换机器 / 回到选择界面 / 失焦 / 退出时强制写一次。
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1010)]
public sealed class MachineDataRecorder : MonoBehaviour
{
    public static MachineDataRecorder Instance { get; private set; }

    [Header("采样")]
    [Tooltip("每隔多少真实秒采一次样。0.25 秒在 ×10 倍速下每个 15 分钟格子约 6 个采样点。")]
    [Min(0.05f)] public float sampleInterval = 0.25f;

    [Tooltip("最多每隔多少真实秒写一次本地存储。")]
    [Min(0.5f)] public float saveInterval = 2f;

    [Header("云端（可选）")]
    [Tooltip("配置了接口地址后，保存时自动上传。地址为空就是纯本地模式，静态托管也能用。")]
    public bool autoSync = true;

    [Tooltip("两次自动上传之间的最小间隔（真实秒）。")]
    [Min(1f)] public float syncInterval = 15f;

    [Header("面板")]
    [Tooltip("按 M 开关“实时参数保存”面板。")]
    public bool showPanel = true;

    MachineTimeController _time;
    MachineSession _session;
    PlatformController _platform;
    SpindleReciprocate _spindle;
    DrillController _drill;

    string _date;
    int _machineIndex = -1;
    MachineSavedDay _day;

    float _sampleTimer;
    float _sampleElapsed;
    float _saveTimer;
    float _lastSyncTime = -999f;

    bool _dirty;
    bool _syncing;
    string _lastSavedAt = "";
    string _lastMessage = "";
    string _lastError = "";
    string _lastExport = "";

    // ─────────────────────────────── 对外状态 ───────────────────────────────

    public string CurrentDate => _date;
    public int CurrentMachineIndex => _machineIndex;
    public MachineSavedDay CurrentDay => _day;

    public int SavedSlotCount => _day != null ? _day.FilledSlotCount : 0;
    public int TotalSampleCount => _day != null ? _day.sampleCount : 0;
    public float RunSeconds => _day != null ? _day.runSeconds : 0f;
    public string LastSavedAt => _lastSavedAt;
    public string LastMessage => _lastMessage;
    public string LastError => _lastError;
    public string LastExport => _lastExport;

    public string MachineLabel
    {
        get
        {
            if (_day != null && !string.IsNullOrEmpty(_day.machineName))
                return _day.machineName;
            if (_machineIndex < 0)
                return "未选择机器";
            return "机器 " + (_machineIndex + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>还没选中机器时不算在记录。</summary>
    public bool IsRecording => _day != null && _machineIndex >= 0;

    // ─────────────────────────────── 生命周期 ───────────────────────────────

    void Awake()
    {
        Instance = this;

        MachineCloudSync.Initialize();

        // 面板可能比本脚本先 Awake，这里主动补一次，保证第一次渲染就有数据。
        _date = MachineDayStore.Today;
        _machineIndex = -1;
        _lastMessage = MachineCloudSync.Enabled ? "已配置云端接口" : "本地模式（无后端）";
    }

    void Start()
    {
        Resolve();
        Debug.Log($"[MachineDataRecorder] 启动。本地已存 {MachineDayStore.DescribeStorage()}；" +
                  $"云端：{(MachineCloudSync.Enabled ? MachineCloudSync.BaseUrl : "未配置")}");
    }

    void Update()
    {
        Resolve();

        if (_time == null)
            return;

        // 暂停 / 选择界面：时间倍速为 0，这一帧不算进演示时长。
        if (Time.timeScale <= 0f)
        {
            _sampleTimer = 0f;
            _sampleElapsed = 0f;
            return;
        }

        int index = _session != null ? _session.CurrentIndex : 0;
        if (index < 0)
        {
            _sampleTimer = 0f;
            _sampleElapsed = 0f;
            return;
        }

        string today = MachineDayStore.Today;
        if (index != _machineIndex || today != _date)
            SwitchTo(today, index);

        float dt = Time.unscaledDeltaTime;

        _sampleElapsed += dt;
        _sampleTimer += dt;
        if (_sampleTimer >= Mathf.Max(0.05f, sampleInterval))
        {
            float window = _sampleElapsed;
            _sampleTimer = 0f;
            _sampleElapsed = 0f;
            Sample(window);
        }

        _saveTimer += dt;
        if (_dirty && _saveTimer >= Mathf.Max(0.5f, saveInterval))
            FlushNow();
    }

    void OnApplicationPause(bool paused)
    {
        if (paused)
            FlushNow();
    }

    void OnApplicationFocus(bool focused)
    {
        if (!focused)
            FlushNow();
    }

    void OnDisable()
    {
        FlushNow();
    }

    void OnDestroy()
    {
        FlushNow();
        if (Instance == this)
            Instance = null;
    }

    // ─────────────────────────────── 采样 ───────────────────────────────

    void Resolve()
    {
        if (_time == null)
            _time = FindAnyObjectByType<MachineTimeController>();
        if (_session == null)
            _session = FindAnyObjectByType<MachineSession>();
        if (_platform == null)
            _platform = FindAnyObjectByType<PlatformController>();
        if (_spindle == null)
            _spindle = FindAnyObjectByType<SpindleReciprocate>();
        if (_drill == null)
            _drill = FindAnyObjectByType<DrillController>();
    }

    void SwitchTo(string date, int machineIndex)
    {
        // 先把上一台机器的数据落盘，再切换。
        FlushNow();

        _date = date;
        _machineIndex = machineIndex;
        _day = MachineDayStore.OpenOrCreate(date, machineIndex, MachineName(machineIndex));

        _dirty = false;
        _sampleTimer = 0f;
        _sampleElapsed = 0f;
        _saveTimer = 0f;

        Debug.Log($"[MachineDataRecorder] 开始记录 {date} / {MachineLabel}，已有 {SavedSlotCount} 段存档");
        _lastMessage = SavedSlotCount > 0
            ? $"已恢复 {date} 的 {SavedSlotCount} 段存档"
            : "今日首次记录";
    }

    string MachineName(int index)
    {
        if (_session != null && _session.machines != null &&
            index >= 0 && index < _session.machines.Length)
        {
            MachineSession.MachineState state = _session.machines[index];
            if (state != null && !string.IsNullOrEmpty(state.name) && state.name != "机器")
                return state.name;
        }

        return "机器 " + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    void Sample(float windowSeconds)
    {
        if (_day == null || windowSeconds <= 0f || _time == null)
            return;

        int slotIndex = _time.CurrentSlot;
        if (slotIndex < 0 || slotIndex >= MachineTimeData.SlotsPerDay)
            return;

        float worktable = _platform != null ? _platform.speed * 1000f : 0f;
        float spindle = _spindle != null ? _spindle.AverageSpeedMms : 0f;
        float drill = _drill != null ? _drill.CurrentRpm : 0f;

        worktable = Safe(worktable);
        spindle = Safe(spindle);
        drill = Safe(drill);

        MachineSavedSlot slot = _day.GetOrCreateSlot(slotIndex, MachineTimeData.SlotLabel(slotIndex));

        float previousWeight = slot.sampleSeconds;
        if (previousWeight <= 0.0001f)
        {
            slot.worktableSpeedMms = worktable;
            slot.spindleSpeedMms = spindle;
            slot.drillRpm = drill;
        }
        else
        {
            float total = previousWeight + windowSeconds;
            slot.worktableSpeedMms = (slot.worktableSpeedMms * previousWeight + worktable * windowSeconds) / total;
            slot.spindleSpeedMms = (slot.spindleSpeedMms * previousWeight + spindle * windowSeconds) / total;
            slot.drillRpm = (slot.drillRpm * previousWeight + drill * windowSeconds) / total;
        }

        slot.sampleSeconds = previousWeight + windowSeconds;
        slot.sampleCount = slot.sampleCount + 1;
        slot.manual = slot.manual || ManualActive;
        slot.machineState = string.IsNullOrEmpty(_time.CurrentStateName) ? "running" : _time.CurrentStateName;
        slot.timeSlot = MachineTimeData.SlotLabel(slotIndex);
        slot.updatedAt = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        _day.machineName = MachineLabel;
        _day.sampleCount = _day.sampleCount + 1;
        _day.runSeconds = _day.runSeconds + windowSeconds;

        _dirty = true;
    }

    static float Safe(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return 0f;
        // 保留一位小数，避免 JsonUtility 写出 316.0000001 这种长尾数字撑大存档。
        return Mathf.Round(value * 10f) / 10f;
    }

    bool ManualActive =>
        (_platform != null && _platform.IsSpeedManual) ||
        (_spindle != null && _spindle.IsSpeedManual) ||
        (_drill != null && _drill.IsRpmManual);

    // ─────────────────────────────── 落盘 / 导出 ───────────────────────────────

    /// <summary>立刻把当前内存里的存档写进本地存储。没有新数据时是空操作。</summary>
    public void FlushNow()
    {
        if (_day == null || !_dirty)
            return;

        try
        {
            MachineDayStore.Save(_day);
            _dirty = false;
            _saveTimer = 0f;
            _lastSavedAt = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            _lastMessage = $"已保存 {_day.FilledSlotCount} / {MachineTimeData.SlotsPerDay} 段";
            _lastError = "";
            AutoSyncIfDue();
        }
        catch (Exception e)
        {
            _lastError = e.Message;
            Debug.LogWarning($"[MachineDataRecorder] 保存失败：{e}");
        }
    }

    void AutoSyncIfDue()
    {
        if (!autoSync || _syncing || !MachineCloudSync.Enabled)
            return;
        if (Time.unscaledTime - _lastSyncTime < syncInterval)
            return;

        _lastSyncTime = Time.unscaledTime;
        SyncNow();
    }

    /// <summary>手动触发一次云端上传（未配置地址时会返回失败提示）。</summary>
    public void SyncNow()
    {
        if (_day == null)
        {
            _lastError = "还没有可上传的数据";
            return;
        }

        if (!MachineCloudSync.Enabled)
        {
            _lastMessage = "未配置云端接口，当前为本地模式";
            _lastError = "";
            return;
        }

        if (_syncing)
            return;

        FlushNow();

        _syncing = true;
        MachineCloudSync.Push(this, _day, OnPushed);
    }

    void OnPushed(bool ok, string message)
    {
        _syncing = false;

        if (ok)
        {
            _lastMessage = message;
            _lastError = "";
        }
        else
        {
            _lastError = message;
        }
    }

    /// <summary>把今天的存档导出成 JSON（桌面端写文件，WebGL 新标签页打开）。</summary>
    public string ExportToday()
    {
        FlushNow();

        string date = string.IsNullOrEmpty(_date) ? MachineDayStore.Today : _date;
        string json = MachineDayStore.BuildExportJson(date);
        string fileName = "machine-day-" + date + ".json";

        bool ok = MachineDataDownload.SaveJson(fileName, json);
        _lastExport = ok
            ? "已导出 " + fileName
            : "导出失败";
        Debug.Log($"[MachineDataRecorder] 导出 {fileName}（{(ok ? "成功" : "失败")}，{json.Length} 字节）");

        return ok ? fileName : string.Empty;
    }

    /// <summary>清空今天全部机器的存档，并重新开一份空的。</summary>
    public void ClearToday()
    {
        string date = string.IsNullOrEmpty(_date) ? MachineDayStore.Today : _date;
        MachineDayStore.ClearDate(date);

        _day = MachineDayStore.OpenOrCreate(
            date,
            Mathf.Max(0, _machineIndex),
            MachineName(Mathf.Max(0, _machineIndex)));

        _dirty = false;
        _saveTimer = 0f;
        _lastSavedAt = "";
        _lastMessage = "已清空 " + date + " 的本地存档";
        _lastExport = "";
        _lastError = "";
    }

    /// <summary>给面板用的一行摘要。</summary>
    public string SummaryLine()
    {
        if (_day == null)
            return "等待选择机器…";

        return $"{_date} · {MachineLabel} · {_day.FilledSlotCount}/{MachineTimeData.SlotsPerDay} 段 · " +
               $"{_day.sampleCount} 次采样";
    }

    /// <summary>调试：面板上显示一行最近存档（证明数据真的在变）。</summary>
    public string RecentSlotLine()
    {
        if (_day == null || _day.slots == null || _day.slots.Count == 0)
            return "暂无存档";

        MachineSavedSlot last = null;
        float best = float.MinValue;
        for (int i = 0; i < _day.slots.Count; i++)
        {
            MachineSavedSlot slot = _day.slots[i];
            if (slot == null || slot.sampleCount <= 0)
                continue;

            float weight = slot.sampleSeconds;
            if (weight > best)
            {
                best = weight;
                last = slot;
            }
        }

        if (last == null)
            return "暂无存档";

        return $"{last.timeSlot} 台 {last.worktableSpeedMms:F1} · 轴 {last.spindleSpeedMms:F1} · 钻 {last.drillRpm:F1}";
    }

    /// <summary>面板是否显示（存在本脚本上，方便 HTML 侧通过 SendMessage 控制）。</summary>
    public bool PanelVisible => showPanel;

    /// <summary>HTML 调用（不带参数）：unityInstance.SendMessage("MachineDataRecorder", "TogglePanel")</summary>
    public void TogglePanel()
    {
        showPanel = !showPanel;
    }

    /// <summary>HTML 调用（带参数）：unityInstance.SendMessage("MachineDataRecorder", "SetPanelVisible", "0")</summary>
    public void SetPanelVisible(string on)
    {
        showPanel = string.IsNullOrEmpty(on) || on == "1" || on.ToLowerInvariant() == "true";
    }

    /// <summary>HTML 调用：设置云端接口地址。</summary>
    public void SetCloudEndpoint(string url)
    {
        MachineCloudSync.SetBaseUrl(url);
        _lastMessage = MachineCloudSync.Enabled
            ? "已配置云端接口：" + MachineCloudSync.BaseUrl
            : "已切换回本地模式";
    }

    /// <summary>HTML 调用：立刻保存一次。</summary>
    public void SaveNow()
    {
        if (_day != null && (_day.sampleCount > 0 || _day.FilledSlotCount > 0))
            _dirty = true;

        FlushNow();
    }
}

/// <summary>场景里没有 MachineDataRecorder 时的运行时兜底。</summary>
public static class MachineDataRecorderBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureRecorder()
    {
        if (UnityEngine.Object.FindAnyObjectByType<MachineDataRecorder>() != null)
            return;

        var go = new GameObject("MachineDataRecorder");
        go.AddComponent<MachineDataRecorder>();
    }
}
