using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>数据库里的一条 15 分钟时段记录。</summary>
public sealed class MachineTimeSlot
{
    public int slotIndex;
    public string timeSlot;
    public int minutesSinceMidnight;
    public float worktableSpeedMms;
    public float spindleReciprocatingSpeedMms;
    public float drillRpm;
    public string machineState;

    /// <summary>A 轴角度（度）。旧版 CSV 没有该列时按 0 处理。</summary>
    public float aAxisDeg;

    /// <summary>C 轴角度（度）。旧版 CSV 没有该列时按 0 处理。</summary>
    public float cAxisDeg;

    /// <summary>主轴转速 S（rpm）。</summary>
    public float spindleSpeedRpm;

    /// <summary>进给速度 F（mm/min）。</summary>
    public float feedRateMmpm;

    /// <summary>切削深度 ap（mm）。</summary>
    public float cutDepthMm;

    /// <summary>切削宽度 ae（mm）。</summary>
    public float cutWidthMm;

    /// <summary>刀号 T。</summary>
    public int toolNumber = 1;

    /// <summary>单件理想循环时间（秒），来自加工程序。</summary>
    public float cycleTimeSeconds = 150f;
}

/// <summary>
/// 读取 StreamingAssets/MachineTimeData/machine_parameters.csv。
/// 数据源是外部 SQLite 数据库 machine_demo.db 的导出版本；
/// Unity 运行时直接读 CSV，避免额外 SQLite 插件。
/// </summary>
public static class MachineTimeData
{
    public const int SlotsPerDay = 96;
    public const int SlotMinutes = 15;
    public const float ManualHoldSeconds = 8f;
    public const string RelativePath = "MachineTimeData/machine_parameters.csv";

    public static MachineTimeSlot[] Load(out string sourcePath)
    {
        sourcePath = Path.Combine(Application.streamingAssetsPath, RelativePath);

        if (!File.Exists(sourcePath))
        {
            Debug.LogError($"[MachineTimeData] 找不到数据文件：{sourcePath}");
            return Array.Empty<MachineTimeSlot>();
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(sourcePath);
        }
        catch (Exception e)
        {
            Debug.LogError($"[MachineTimeData] 读取失败：{e.Message}");
            return Array.Empty<MachineTimeSlot>();
        }

        var slots = new MachineTimeSlot[SlotsPerDay];
        int count = 0;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] parts = line.Split(',');
            if (parts.Length < 7)
                continue;

            try
            {
                var slot = new MachineTimeSlot
                {
                    slotIndex = int.Parse(parts[0], CultureInfo.InvariantCulture),
                    timeSlot = parts[1],
                    minutesSinceMidnight = int.Parse(parts[2], CultureInfo.InvariantCulture),
                    worktableSpeedMms = float.Parse(parts[3], CultureInfo.InvariantCulture),
                    spindleReciprocatingSpeedMms = float.Parse(parts[4], CultureInfo.InvariantCulture),
                    drillRpm = float.Parse(parts[5], CultureInfo.InvariantCulture),
                    machineState = parts[6],
                    aAxisDeg = parts.Length >= 9 ? float.Parse(parts[7], CultureInfo.InvariantCulture) : 0f,
                    cAxisDeg = parts.Length >= 9 ? float.Parse(parts[8], CultureInfo.InvariantCulture) : 0f,
                    spindleSpeedRpm = parts.Length >= 14 ? float.Parse(parts[9], CultureInfo.InvariantCulture) : 0f,
                    feedRateMmpm = parts.Length >= 14 ? float.Parse(parts[10], CultureInfo.InvariantCulture) : 0f,
                    cutDepthMm = parts.Length >= 14 ? float.Parse(parts[11], CultureInfo.InvariantCulture) : 0f,
                    cutWidthMm = parts.Length >= 14 ? float.Parse(parts[12], CultureInfo.InvariantCulture) : 0f,
                    toolNumber = parts.Length >= 14 ? int.Parse(parts[13], CultureInfo.InvariantCulture) : 1,
                    cycleTimeSeconds = parts.Length >= 15 ? float.Parse(parts[14], CultureInfo.InvariantCulture) : 150f,
                };

                if (slot.slotIndex >= 0 && slot.slotIndex < SlotsPerDay)
                {
                    slots[slot.slotIndex] = slot;
                    count++;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MachineTimeData] 跳过第 {i + 1} 行：{e.Message}");
            }
        }

        if (count != SlotsPerDay)
            Debug.LogWarning($"[MachineTimeData] 期望 {SlotsPerDay} 行，实际读取 {count} 行。");

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null)
            {
                Debug.LogWarning($"[MachineTimeData] 缺少 slot {i}，将使用最小值。");
                slots[i] = new MachineTimeSlot
                {
                    slotIndex = i,
                    timeSlot = $"{i * SlotMinutes / 60:00}:{i * SlotMinutes % 60:00}",
                    minutesSinceMidnight = i * SlotMinutes,
                    worktableSpeedMms = 5f,
                    spindleReciprocatingSpeedMms = 10f,
                    drillRpm = 0f,
                    machineState = "idle",
                };
            }
        }

        Debug.Log($"[MachineTimeData] 已加载 {count} 条时段数据：{sourcePath}");
        return slots;
    }
}
