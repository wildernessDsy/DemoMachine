using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// 把机械臂整体上移到“机床高度的一半”附近：让机械臂的包围盒中心 = 机床包围盒中心高度。
/// 只执行一次，保存场景并刷新预览。
/// </summary>
[InitializeOnLoad]
public static class RobotArmHeight
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";

    static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    static string MarkerPath => Path.Combine(ProjectRoot, "Library", "RobotArmHeight.done");

    static RobotArmHeight()
    {
        EditorApplication.delayCall += TryRun;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += TryRun;
        };
    }

    static void TryRun()
    {
        if (Application.isBatchMode)
            return;
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        if (File.Exists(MarkerPath))
            return;

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            Debug.LogWarning($"[RobotArmHeight] 当前场景不是 {ScenePath}，跳过。");
            return;
        }

        var machine = GameObject.Find("五轴机床");
        var arm = GameObject.Find("RoboticArm");
        if (machine == null || arm == null)
        {
            Debug.LogWarning($"[RobotArmHeight] machine={(machine != null)} arm={(arm != null)}，跳过。");
            return;
        }

        Bounds machineBounds = GetBounds(machine.transform);
        Bounds armBounds = GetBounds(arm.transform);
        float deltaY = machineBounds.center.y - armBounds.center.y;
        arm.transform.position += Vector3.up * deltaY;

        Bounds newArmBounds = GetBounds(arm.transform);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();

        Debug.Log($"[RobotArmHeight] machineCenterY={machineBounds.center.y:F3} " +
                  $"armCenterY(before)={armBounds.center.y:F3} deltaY={deltaY:F3} " +
                  $"armPos={arm.transform.position} newArmCenterY={newArmBounds.center.y:F3}");

        FrameSceneView(machineBounds, newArmBounds);
        RenderPreview();

        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
        File.WriteAllText(MarkerPath, DateTime.Now.ToString("O"));
    }

    static Bounds GetBounds(Transform root)
    {
        bool has = false;
        Bounds bounds = new Bounds(root.position, Vector3.zero);
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                continue;
            if (!has)
            {
                bounds = r.bounds;
                has = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }
        return bounds;
    }

    static void FrameSceneView(Bounds machineBounds, Bounds armBounds)
    {
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null)
            return;

        Bounds combined = machineBounds;
        combined.Encapsulate(armBounds);
        sceneView.LookAt(combined.center, Quaternion.Euler(20f, -35f, 0f),
            Mathf.Max(2f, combined.size.magnitude * 1.2f));
        sceneView.Repaint();
    }

    static void RenderPreview()
    {
        var cam = Camera.main;
        if (cam == null)
            return;

        const int width = 1280;
        const int height = 720;
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        rt.Create();
        var originalTarget = cam.targetTexture;
        cam.targetTexture = rt;
        try
        {
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
                RenderPipeline.SubmitRenderRequest(cam, request);
            else
                cam.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            string dir = Path.Combine(ProjectRoot, "MachinePreviews");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "robot_arm_height.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log("[RobotArmHeight] 预览图：" + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RobotArmHeight] 预览渲染失败：" + e.Message);
        }
        finally
        {
            cam.targetTexture = originalTarget;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
