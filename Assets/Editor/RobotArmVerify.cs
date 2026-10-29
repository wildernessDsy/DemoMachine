using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// 只读校验：确认 SampleScene 里机床和机械臂都在，重新渲染预览图，并把 Scene 视角框到两者。
/// 不改场景、不保存场景。
/// </summary>
[InitializeOnLoad]
public static class RobotArmVerify
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string MachineName = "五轴机床";
    const string ArmName = "RoboticArm";

    static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    static string MarkerPath => Path.Combine(ProjectRoot, "Library", "RobotArmVerify.done");

    static RobotArmVerify()
    {
        EditorApplication.delayCall += Run;
    }

    static void Run()
    {
        if (Application.isBatchMode)
            return;
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        if (File.Exists(MarkerPath))
            return;

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            return;

        var machine = GameObject.Find(MachineName);
        var arm = GameObject.Find(ArmName);

        string machineStatus = machine == null ? "missing"
            : PrefabUtility.GetPrefabInstanceStatus(machine).ToString();
        string armStatus = arm == null ? "missing"
            : PrefabUtility.GetPrefabInstanceStatus(arm).ToString();

        int machineRenderers = machine == null ? 0 : machine.GetComponentsInChildren<Renderer>(true).Length;
        int armRenderers = arm == null ? 0 : arm.GetComponentsInChildren<Renderer>(true).Length;

        Debug.Log($"[RobotArmVerify] machine={(machine != null)} status={machineStatus} renderers={machineRenderers} | " +
                  $"arm={(arm != null)} status={armStatus} renderers={armRenderers}");

        if (machine != null && arm != null)
        {
            Bounds combined = GetBounds(machine.transform);
            combined.Encapsulate(GetBounds(arm.transform));

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                sceneView.LookAt(combined.center, Quaternion.Euler(20f, -35f, 0f),
                    Mathf.Max(2f, combined.size.magnitude * 1.2f));
                sceneView.Repaint();
            }

            RenderPreview();
        }

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

    static void RenderPreview()
    {
        var cam = Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[RobotArmVerify] Camera.main 不存在。");
            return;
        }

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
            string path = Path.Combine(dir, "robot_arm_verify.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log("[RobotArmVerify] 预览图：" + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RobotArmVerify] 渲染失败：" + e.Message);
        }
        finally
        {
            cam.targetTexture = originalTarget;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
