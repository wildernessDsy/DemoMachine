using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// 一天里某个 15 分钟时段的“演示参数”存档。
/// 和 MachineTimeSlot 的区别：MachineTimeSlot 是只读的原始 CSV，
/// 这里存的是演示过程中真正跑出来的实时参数（可以回放、可以上传）。
/// </summary>
[Serializable]
public sealed class MachineSavedSlot
{
    public int slotIndex;
    public string timeSlot;
    public float worktableSpeedMms;
    public float spindleSpeedMms;
    public float drillRpm;
    public string machineState;

    /// <summary>该时段累计的采样权重（秒），用来算加权平均。</summary>
    public float sampleSeconds;
    public int sampleCount;

    /// <summary>演示时是否被用户手动拖过滑块。</summary>
    public bool manual;

    /// <summary>最后一次刷新该时段的真实时间（HH:mm:ss）。</summary>
    public string updatedAt;
}

/// <summary>
/// 一台机器一天的全部存档。date 用本地真实日期（yyyy-MM-dd），
/// 时段用模拟时钟的 15 分钟格子（0..95）。
/// </summary>
[Serializable]
public sealed class MachineSavedDay
{
    public int schemaVersion = 1;
    public string date;
    public int machineIndex;
    public string machineName;
    public string createdAt;
    public string updatedAt;

    /// <summary>当天累计演示时长（真实秒）。</summary>
    public float runSeconds;
    public int sampleCount;

    public bool uploaded;
    public string uploadedAt;

    public List<MachineSavedSlot> slots = new List<MachineSavedSlot>();

    /// <summary>已经保存了数据的时段数量（0..96）。</summary>
    public int FilledSlotCount
    {
        get
        {
            if (slots == null)
                return 0;

            int count = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] != null && slots[i].sampleCount > 0)
                    count++;
            }
            return count;
        }
    }

    public MachineSavedSlot FindSlot(int slotIndex)
    {
        if (slots == null)
            return null;

        for (int i = 0; i < slots.Count; i++)
        {
            MachineSavedSlot slot = slots[i];
            if (slot != null && slot.slotIndex == slotIndex)
                return slot;
        }
        return null;
    }

    public MachineSavedSlot GetOrCreateSlot(int slotIndex, string timeSlot)
    {
        if (slots == null)
            slots = new List<MachineSavedSlot>();

        MachineSavedSlot found = FindSlot(slotIndex);
        if (found != null)
            return found;

        found = new MachineSavedSlot
        {
            slotIndex = slotIndex,
            timeSlot = timeSlot,
            machineState = "idle",
        };
        slots.Add(found);
        return found;
    }

    public void SortSlots()
    {
        if (slots == null)
            return;

        slots.Sort(CompareBySlotIndex);
    }

    static int CompareBySlotIndex(MachineSavedSlot a, MachineSavedSlot b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        return a.slotIndex.CompareTo(b.slotIndex);
    }
}

/// <summary>导出 / 上传用的容器（JsonUtility 不能直接序列化顶层数组）。</summary>
[Serializable]
public sealed class MachineDayExport
{
    public int schemaVersion = 1;
    public string exportedAt;
    public string source;
    public string date;
    public List<MachineSavedDay> days = new List<MachineSavedDay>();
}

/// <summary>日期索引，用来知道本地到底存了哪几天。</summary>
[Serializable]
internal sealed class MachineDayIndex
{
    public List<string> dates = new List<string>();
}

/// <summary>
/// 当日实时参数的本地持久化层。
///
/// 存储位置：PlayerPrefs。WebGL 打包下 PlayerPrefs 落在浏览器的 IndexedDB 里，
/// 所以静态托管的网站（COS / OSS / GitHub Pages）不需要任何后端也能“记住”演示过的数据，
/// 刷新页面、换一天再来都还在。编辑器 / 桌面端则落在注册表 / plist / 本地配置目录。
///
/// 键名约定：
///   dsy.machine.day.&lt;yyyy-MM-dd&gt;.&lt;machineIndex&gt;  → 一台机器一天的 JSON
///   dsy.machine.day.index                        → 有数据的日期列表
/// </summary>
public static class MachineDayStore
{
    public const int SchemaVersion = 1;

    /// <summary>最多保留多少天的数据（WebGL 的 PlayerPrefs 有 1MB 上限，7 天足够且很安全）。</summary>
    public const int MaxDaysRetained = 7;

    /// <summary>工程里一共三台机器，这里留一点余量。</summary>
    public const int MaxMachines = 8;

    const string KeyPrefix = "dsy.machine.day.";
    const string IndexKey = "dsy.machine.day.index";

    static readonly Dictionary<string, MachineSavedDay> Cache = new Dictionary<string, MachineSavedDay>();
    static List<string> _dates;
    static string _todayCache;
    static int _todayStamp = -1;

    /// <summary>每次写入自增，UI 用它判断要不要刷新缓存。</summary>
    public static int Version { get; private set; }

    /// <summary>本地真实日期（yyyy-MM-dd）。每帧都要读，所以按天缓存一下字符串。</summary>
    public static string Today
    {
        get
        {
            DateTime now = DateTime.Now;
            int stamp = now.Year * 1000 + now.DayOfYear;
            if (_todayCache == null || stamp != _todayStamp)
            {
                _todayStamp = stamp;
                _todayCache = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return _todayCache;
        }
    }

    public static string NowStamp()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string StorageKey(string date, int machineIndex)
    {
        return KeyPrefix + date + "." + machineIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>读一台机器一天的存档；没有就返回 null（null 也会被缓存，避免反复读 PlayerPrefs）。</summary>
    public static MachineSavedDay Load(string date, int machineIndex)
    {
        if (string.IsNullOrEmpty(date) || machineIndex < 0 || machineIndex >= MaxMachines)
            return null;

        string key = StorageKey(date, machineIndex);

        MachineSavedDay cached;
        if (Cache.TryGetValue(key, out cached))
            return cached;

        string json = PlayerPrefs.GetString(key, string.Empty);
        if (string.IsNullOrEmpty(json))
        {
            Cache[key] = null;
            return null;
        }

        MachineSavedDay day = null;
        try
        {
            day = JsonUtility.FromJson<MachineSavedDay>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MachineDayStore] 解析存档失败 {key}：{e.Message}");
        }

        if (day != null)
        {
            if (day.slots == null)
                day.slots = new List<MachineSavedSlot>();
            day.date = date;
            day.machineIndex = machineIndex;
        }

        Cache[key] = day;
        return day;
    }

    /// <summary>读一台机器一天的存档，没有就建一个空壳（不会立刻写盘）。</summary>
    public static MachineSavedDay OpenOrCreate(string date, int machineIndex, string machineName)
    {
        MachineSavedDay day = Load(date, machineIndex);
        if (day != null)
        {
            if (!string.IsNullOrEmpty(machineName))
                day.machineName = machineName;
            return day;
        }

        day = new MachineSavedDay
        {
            schemaVersion = SchemaVersion,
            date = date,
            machineIndex = machineIndex,
            machineName = machineName,
            createdAt = NowStamp(),
            updatedAt = NowStamp(),
            slots = new List<MachineSavedSlot>(),
        };

        Cache[StorageKey(date, machineIndex)] = day;
        return day;
    }

    /// <summary>写盘（同时更新日期索引、按策略淘汰最旧的日期）。</summary>
    public static void Save(MachineSavedDay day)
    {
        if (day == null || string.IsNullOrEmpty(day.date))
            return;
        if (day.machineIndex < 0 || day.machineIndex >= MaxMachines)
            return;

        day.schemaVersion = SchemaVersion;
        day.updatedAt = NowStamp();
        day.SortSlots();

        string key = StorageKey(day.date, day.machineIndex);
        string json = JsonUtility.ToJson(day);

        try
        {
            PlayerPrefs.SetString(key, json);
            RegisterDate(day.date);
            PlayerPrefs.Save();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MachineDayStore] 写入存档失败：{e.Message}");
            return;
        }

        Cache[key] = day;
        Version++;
    }

    /// <summary>把日期登记到索引里（新日期排在最前，超出 MaxDaysRetained 的最旧日期会被清掉）。</summary>
    static void RegisterDate(string date)
    {
        List<string> dates = Dates();
        if (dates.Contains(date))
            return;

        dates.Add(date);
        dates.Sort(CompareDatesDesc);
        _dates = dates;

        PruneDates(dates);
        SaveIndex(dates);
    }

    /// <summary>淘汰超出保留天数的日期，连数据一起删掉，避免吃满 WebGL 的 1MB 配额。</summary>
    static void PruneDates(List<string> dates)
    {
        while (dates.Count > MaxDaysRetained)
        {
            string oldest = dates[dates.Count - 1];
            dates.RemoveAt(dates.Count - 1);
            DeleteStoredDate(oldest);
        }
    }

    static void DeleteStoredDate(string date)
    {
        if (string.IsNullOrEmpty(date))
            return;

        for (int i = 0; i < MaxMachines; i++)
        {
            string key = StorageKey(date, i);
            if (PlayerPrefs.HasKey(key))
                PlayerPrefs.DeleteKey(key);
            Cache.Remove(key);
        }
    }

    static int CompareDatesDesc(string a, string b)
    {
        // yyyy-MM-dd 的字典序等于时间序，倒序就是最近的在前。
        return string.CompareOrdinal(b, a);
    }

    public static List<string> Dates()
    {
        if (_dates != null)
            return _dates;

        _dates = new List<string>();

        string json = PlayerPrefs.GetString(IndexKey, string.Empty);
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                MachineDayIndex index = JsonUtility.FromJson<MachineDayIndex>(json);
                if (index != null && index.dates != null)
                {
                    for (int i = 0; i < index.dates.Count; i++)
                    {
                        string date = index.dates[i];
                        if (!string.IsNullOrEmpty(date) && !_dates.Contains(date))
                            _dates.Add(date);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MachineDayStore] 读取日期索引失败：{e.Message}");
            }
        }

        _dates.Sort(CompareDatesDesc);
        return _dates;
    }

    static void SaveIndex(List<string> dates)
    {
        var index = new MachineDayIndex { dates = new List<string>() };
        for (int i = 0; i < dates.Count; i++)
        {
            if (!string.IsNullOrEmpty(dates[i]))
                index.dates.Add(dates[i]);
        }

        PlayerPrefs.SetString(IndexKey, JsonUtility.ToJson(index));
        PlayerPrefs.Save();
    }

    /// <summary>本地是否有任何一天的数据。</summary>
    public static bool HasAnyData()
    {
        return Dates().Count > 0;
    }

    /// <summary>今天是否有某台机器的数据。</summary>
    public static bool HasTodayData(int machineIndex)
    {
        return Load(Today, machineIndex) != null;
    }

    /// <summary>清空某一天的全部机器存档。</summary>
    public static void ClearDate(string date)
    {
        if (string.IsNullOrEmpty(date))
            return;

        DeleteStoredDate(date);

        List<string> dates = Dates();
        if (dates.Remove(date))
            SaveIndex(dates);
        else
            PlayerPrefs.Save();

        Version++;
    }

    /// <summary>清空全部本地存档。</summary>
    public static void ClearAll()
    {
        List<string> dates = Dates();
        for (int i = 0; i < dates.Count; i++)
            DeleteStoredDate(dates[i]);

        dates.Clear();
        PlayerPrefs.SetString(IndexKey, JsonUtility.ToJson(new MachineDayIndex()));
        PlayerPrefs.Save();

        Cache.Clear();
        Version++;
    }

    /// <summary>把某一天的（最多三台机器）存档打包成导出 JSON。</summary>
    public static string BuildExportJson(string date)
    {
        var export = new MachineDayExport
        {
            schemaVersion = SchemaVersion,
            exportedAt = NowStamp(),
            source = "DemoMachine WebGL",
            date = date,
            days = new List<MachineSavedDay>(),
        };

        if (!string.IsNullOrEmpty(date))
        {
            for (int i = 0; i < MaxMachines; i++)
            {
                MachineSavedDay day = Load(date, i);
                if (day == null || day.FilledSlotCount <= 0)
                    continue;

                day.SortSlots();
                export.days.Add(day);
            }
        }

        return JsonUtility.ToJson(export, true);
    }

    /// <summary>把全部保留的日期打包成导出 JSON。</summary>
    public static string BuildExportJsonAll()
    {
        var export = new MachineDayExport
        {
            schemaVersion = SchemaVersion,
            exportedAt = NowStamp(),
            source = "DemoMachine WebGL",
            date = string.Empty,
            days = new List<MachineSavedDay>(),
        };

        List<string> dates = Dates();
        for (int i = 0; i < dates.Count; i++)
        {
            for (int machine = 0; machine < MaxMachines; machine++)
            {
                MachineSavedDay day = Load(dates[i], machine);
                if (day == null || day.FilledSlotCount <= 0)
                    continue;

                day.SortSlots();
                export.days.Add(day);
            }
        }

        return JsonUtility.ToJson(export, true);
    }

    /// <summary>调试用：本地已经存了几天、几个机器。</summary>
    public static string DescribeStorage()
    {
        List<string> dates = Dates();
        int machines = 0;
        int slots = 0;

        for (int i = 0; i < dates.Count; i++)
        {
            for (int machine = 0; machine < MaxMachines; machine++)
            {
                MachineSavedDay day = Load(dates[i], machine);
                if (day == null || day.FilledSlotCount <= 0)
                    continue;
                machines++;
                slots += day.FilledSlotCount;
            }
        }

        return $"{dates.Count} 天 / {machines} 条记录 / {slots} 段";
    }
}
