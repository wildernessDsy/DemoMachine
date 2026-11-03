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
