using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 把导出的 JSON 交到用户手上。
/// - 编辑器 / 桌面端：写到 Application.persistentDataPath，并在 Console 打印完整路径；
/// - WebGL：用 data: URL 在新标签页打开（浏览器会当普通 JSON 显示，可另存为）。
/// 不依赖任何额外的 .jslib 插件，静态托管直接可用。
/// </summary>
public static class MachineDataDownload
{
    /// <summary>最近一次导出的落地位置，面板可以显示出来。</summary>
    public static string LastLocation { get; private set; } = string.Empty;

    public static bool SaveJson(string fileName, string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        if (string.IsNullOrEmpty(fileName))
            fileName = "machine-data.json";

        try
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string dataUrl = "data:application/json;charset=utf-8," + Uri.EscapeDataString(content);
            Application.OpenURL(dataUrl);
            LastLocation = "浏览器新标签页";
            Debug.Log($"[MachineDataDownload] 已在浏览器打开导出内容（{content.Length} 字节）。");
            return true;
#else
            string path = Path.Combine(Application.persistentDataPath, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            LastLocation = path;
            Debug.Log($"[MachineDataDownload] 已写出：{path}");
            return true;
#endif
        }
        catch (Exception e)
        {
            LastLocation = string.Empty;
            Debug.LogError($"[MachineDataDownload] 导出失败：{e.Message}");
            return false;
        }
    }

    /// <summary>把内容复制到系统剪贴板（WebGL 下走浏览器的复制实现，失败时返回 false）。</summary>
    public static bool CopyToClipboard(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        try
        {
            GUIUtility.systemCopyBuffer = content;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MachineDataDownload] 复制失败：{e.Message}");
            return false;
        }
    }
}
