using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性把 MachineTimeSystem 加到 SampleScene，并校验 StreamingAssets 数据。
/// 运行时还有 MachineTimeBootstrap 兜底，所以即使场景里没有这个对象也能显示时钟。
/// </summary>
[InitializeOnLoad]
public static class MachineTimeSetup
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string RootName = "MachineTimeSystem";

    static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    static string MarkerPath => Path.Combine(ProjectRoot, "Library", "MachineTimeSetup.done");

    static MachineTimeSetup()
    {
        EditorApplication.delayCall += TryAutoSetup;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += TryAutoSetup;
        };
    }

    static void TryAutoSetup()
    {
        if (Application.isBatchMode)
            return;
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        if (File.Exists(MarkerPath))
            return;

        SetupNow();
    }

    [MenuItem("Tools/DemoMachine/Setup Machine Time")]
    static void MenuSetup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[MachineTimeSetup] 请先退出 Play 模式。");
            return;
        }

        if (File.Exists(MarkerPath))
            File.Delete(MarkerPath);
        SetupNow();
    }

    /// <summary>批处理入口：打开 SampleScene、添加时间系统、保存并校验数据。</summary>
    public static void BatchSetup()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        SetupNow();
        AssetDatabase.SaveAssets();
        Debug.Log("[MachineTimeSetup] BatchSetup 完成。");
    }

    static void SetupNow()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            Debug.LogWarning($"[MachineTimeSetup] 当前场景不是 {ScenePath}，跳过。");
            return;
        }

        var existing = GameObject.Find(RootName);
        if (existing == null)
        {
            var go = new GameObject(RootName);
            go.AddComponent<MachineTimeController>();
            Undo.RegisterCreatedObjectUndo(go, "Create MachineTimeSystem");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[MachineTimeSetup] 已创建 MachineTimeSystem 并保存场景。");
        }
        else if (existing.GetComponent<MachineTimeController>() == null)
        {
            existing.AddComponent<MachineTimeController>();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[MachineTimeSetup] 已给 MachineTimeSystem 添加 MachineTimeController。");
        }
        else
        {
            Debug.Log("[MachineTimeSetup] MachineTimeSystem 已存在，跳过创建。");
        }

        var slots = MachineTimeData.Load(out string dataPath);
        if (slots.Length == MachineTimeData.SlotsPerDay)
        {
            Debug.Log($"[MachineTimeSetup] 数据校验通过：{slots.Length} 条；" +
                      $"08:00 工作台={slots[32].worktableSpeedMms} mm/s，" +
                      $"主轴={slots[32].spindleReciprocatingSpeedMms} mm/s，" +
                      $"钻头={slots[32].drillRpm} RPM；文件={dataPath}");
        }
        else
        {
            Debug.LogError($"[MachineTimeSetup] 数据条数异常：{slots.Length}，文件={dataPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
        File.WriteAllText(MarkerPath, DateTime.Now.ToString("O"));
    }
}
