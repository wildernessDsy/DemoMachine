using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性把 SampleScene 配置成“开机即正面展示机床”的状态：
/// - 实例化五轴机床预制体，并把正面（本地 +X：中间门 + 右侧面板）转向 -Z；
/// - 给主相机挂上 MachineViewController；
/// - 用纯色遮罩渲染迭代校准出机床可见轮廓的真实高度，按 50% 屏高构图；
/// - 保存场景。
/// 可通过菜单 Tools/DemoMachine 手动执行，也支持 -executeMethod 批处理。
/// </summary>
public static class MachineSceneSetup
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string PrefabPath = "Assets/Resources/五轴机床.prefab";
    const string MachineName = "五轴机床";
    const float TargetHeightFraction = 0.5f;

    /// <summary>预览图输出目录：项目根目录下的 MachinePreviews（不在 Assets 里，不会被 Unity 导入）。</summary>
    static string PreviewDir => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "MachinePreviews");

    static readonly Color MaskColor = new Color(1f, 0f, 1f, 1f);

    [MenuItem("Tools/DemoMachine/Setup Scene")]
    public static void Setup()
    {
        Run(false);
    }

    [MenuItem("Tools/DemoMachine/Setup Scene And Render Previews")]
    public static void SetupAndPreview()
    {
        Run(true);
    }

    [MenuItem("Tools/DemoMachine/Verify Framing")]
    public static void VerifyFraming()
    {
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var cam = Camera.main;
            if (cam == null)
                throw new InvalidOperationException("场景里没有 MainCamera 标记的相机。");

            var viewer = cam.GetComponent<MachineViewController>();
            var machine = GameObject.Find(MachineName);

            var rootNames = new System.Text.StringBuilder();
            foreach (var root in scene.GetRootGameObjects())
                rootNames.Append(root.name).Append(", ");
            Debug.Log("[Verify] scene roots: " + rootNames);

            if (machine == null)
            {
                Debug.LogError("[Verify] 场景里找不到机床对象！");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[Verify] machine '{machine.name}' pos={machine.transform.position} " +
                      $"euler={machine.transform.eulerAngles} children={machine.transform.childCount}");
            Debug.Log($"[Verify] viewer={(viewer != null)} calibratedHeight=" +
                      $"{(viewer != null ? viewer.calibratedSilhouetteHeight : 0f):F3} " +
                      $"cameraPos={cam.transform.position} cameraEuler={cam.transform.eulerAngles}");

            if (!MachineViewController.TryGetBounds(machine.transform, out var bounds))
            {
                Debug.LogError("[Verify] 机床没有可渲染的网格。");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[Verify] bounds center={bounds.center} size={bounds.size}");

            int width = 1280;
            int height = 720;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();

            var originalTarget = cam.targetTexture;
            var originalFlags = cam.clearFlags;
            var originalBackground = cam.backgroundColor;

            cam.targetTexture = rt;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = MaskColor;

            string maskPath = Path.Combine(PreviewDir, "preview_verify_original_mask.png");
            if (RenderMaskMeasure(cam, rt, maskPath, out float maskHeight, out float maskWidth))
            {
                Debug.Log($"[Verify] silhouette height={maskHeight:F4} width={maskWidth:F4} target={TargetHeightFraction:F4}");
            }

            cam.clearFlags = originalFlags;
            cam.backgroundColor = originalBackground;
            RenderTo(cam, rt, Path.Combine(PreviewDir, "preview_verify_original.png"));

            cam.targetTexture = originalTarget;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);

            Debug.Log("[Verify] done");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
        }
    }

    static void Run(bool renderPreviews)
    {
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 清掉旧的机床实例，保证脚本可以重复执行。
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == MachineName || root.name == "MachineRoot")
                    UnityEngine.Object.DestroyImmediate(root);
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
                throw new InvalidOperationException("找不到预制体: " + PrefabPath);

            var machine = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            machine.name = MachineName;

            // 模型本地 +X 是正面（门 + 右侧面板）。转 90° 后正面朝向世界 -Z，
            // 相机站在 -Z 一侧朝 +Z 看，正好看到正面。
            machine.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 90f, 0f));

            // 把机床包围盒在水平面上对到世界原点，便于后续摆放和观察。
            if (MachineViewController.TryGetBounds(machine.transform, out var rawBounds))
            {
                var p = machine.transform.position;
                machine.transform.position = new Vector3(p.x - rawBounds.center.x, p.y, p.z - rawBounds.center.z);
            }

            var cam = Camera.main;
            if (cam == null)
                throw new InvalidOperationException("场景里没有 MainCamera 标记的相机。");

            var viewer = cam.GetComponent<MachineViewController>();
            if (viewer == null)
                viewer = cam.gameObject.AddComponent<MachineViewController>();

            viewer.machineRoot = machine.transform;
            viewer.frontLocalDirection = Vector3.right;
            viewer.screenHeightFraction = TargetHeightFraction;
            viewer.maxScreenWidthFraction = 0.9f;
            viewer.calibratedSilhouetteHeight = 0f;

            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 500f;

            viewer.FrameMachine();

            // 用真实渲染出的轮廓校准可见高度，避免包围盒角点造成的偏差。
            if (CalibrateFraming(cam, machine.transform, out float calibratedHeight, out float maskHeight,
                    out float maskWidth, out float calibratedDistance))
            {
                viewer.calibratedSilhouetteHeight = calibratedHeight;
                viewer.FrameMachine();

                Debug.Log($"[Calibration] calibratedSilhouetteHeight={calibratedHeight:F3} " +
                          $"maskHeight={maskHeight:F4} maskWidth={maskWidth:F4} distance={calibratedDistance:F3}");
            }
            else
            {
                Debug.LogWarning("[Calibration] 遮罩校准失败，退回包围盒估算。");
            }

            if (MachineViewController.TryGetBounds(machine.transform, out var bounds))
            {
                viewer.MeasureCurrentView(out float silhouetteHeight, out float silhouetteWidth);

                Debug.Log($"[Setup] machine bounds center={bounds.center} size={bounds.size} rotation={machine.transform.eulerAngles}");
                Debug.Log($"[Setup] camera pos={cam.transform.position} euler={cam.transform.eulerAngles} " +
                          $"distanceToCenter={Vector3.Distance(cam.transform.position, bounds.center):F3}");
                Debug.Log($"[Setup] aabb silhouette height={silhouetteHeight:F4} width={silhouetteWidth:F4} " +
                          $"target={TargetHeightFraction:F2} aspect={cam.aspect:F3}");

                LogPart(machine.transform, "数控面板", "panel");
                LogPart(machine.transform, "门", "door");
                LogPart(machine.transform, "手轮", "handwheel");
            }
            else
            {
                Debug.LogError("[Setup] 机床上没有可渲染的网格，包围盒计算失败。");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[Setup] 场景已保存: " + ScenePath);

            if (renderPreviews)
                RenderPreviews(cam, machine.transform, viewer.calibratedSilhouetteHeight);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
            throw;
        }
    }

    /// <summary>
    /// 把相机放到正前方，渲染纯色遮罩并测量真实轮廓高度，迭代调整距离直到 50%。
    /// 返回调整后的可见轮廓高度（米），供运行时精确构图。
    /// </summary>
    static bool CalibrateFraming(Camera cam, Transform machine, out float visibleHeight,
        out float measuredHeight, out float measuredWidth, out float finalDistance)
    {
        visibleHeight = 0f;
        measuredHeight = 0f;
        measuredWidth = 0f;
        finalDistance = 0f;

        if (!MachineViewController.TryGetBounds(machine, out var bounds))
            return false;

        var renderers = MachineViewController.CollectRenderers(machine);
        int width = 1280;
        int height = 720;
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        rt.Create();

        var originalTarget = cam.targetTexture;
        var originalFlags = cam.clearFlags;
        var originalBackground = cam.backgroundColor;
        var originalPosition = cam.transform.position;
        var originalRotation = cam.transform.rotation;

        cam.targetTexture = rt;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = MaskColor;

        float frontExtent = bounds.extents.z; // 正面已经朝 -Z
        float halfVertical = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float distance = MachineViewController.SolveDistance(cam, renderers, bounds, Vector3.back,
            TargetHeightFraction, 0.9f);

        bool measured = false;
        for (int i = 0; i < 8; i++)
        {
            cam.transform.position = bounds.center + Vector3.back * distance;
            cam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

            if (!RenderMaskMeasure(cam, rt, null, out float h, out float w))
                break;

            measuredHeight = h;
            measuredWidth = w;
            finalDistance = distance;
            measured = true;

            if (Mathf.Abs(h - TargetHeightFraction) <= 0.003f)
                break;

            float scale = Mathf.Clamp(h / TargetHeightFraction, 0.6f, 1.6f);
            distance = frontExtent + (distance - frontExtent) * scale;
        }

        // 最后再渲染一张图存下来作为验证。
        cam.transform.position = bounds.center + Vector3.back * distance;
        cam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        string maskPath = Path.Combine(PreviewDir, "preview_front_mask.png");
        Directory.CreateDirectory(PreviewDir);
        if (RenderMaskMeasure(cam, rt, maskPath, out float finalH, out float finalW))
        {
            measuredHeight = finalH;
            measuredWidth = finalW;
            finalDistance = distance;
            measured = true;
        }

        visibleHeight = 2f * (distance - frontExtent) * halfVertical * measuredHeight;

        cam.targetTexture = originalTarget;
        cam.clearFlags = originalFlags;
        cam.backgroundColor = originalBackground;
        cam.transform.SetPositionAndRotation(originalPosition, originalRotation);
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);

        return measured && measuredHeight > 0.01f;
    }

    static void LogPart(Transform machine, string partName, string label)
    {
        var part = FindDeep(machine, partName);
        if (part == null)
        {
            Debug.LogWarning($"[Setup] 没找到部件 {partName}");
            return;
        }

        string extra = "";
        if (MachineViewController.TryGetBounds(part, out var b))
            extra = $" rendererBounds center={b.center} size={b.size}";
        Debug.Log($"[Setup] {label} '{partName}' pos={part.position}{extra}");
    }

    static Transform FindDeep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == name)
                return t;
        }
        return null;
    }

    /// <summary>从四个方向各渲染一张预览图，并渲染正面遮罩验证真实轮廓占比。</summary>
    static void RenderPreviews(Camera cam, Transform machine, float calibratedHeight)
    {
        if (!MachineViewController.TryGetBounds(machine, out var bounds))
            return;

        Directory.CreateDirectory(PreviewDir);

        int width = 1280;
        int height = 720;
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        rt.Create();

        var originalTarget = cam.targetTexture;
        var originalPosition = cam.transform.position;
        var originalRotation = cam.transform.rotation;
        var originalFlags = cam.clearFlags;
        var originalBackground = cam.backgroundColor;

        cam.targetTexture = rt; // 让 camera.aspect 跟随预览分辨率

        var renderers = MachineViewController.CollectRenderers(machine);
        float halfVertical = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float frontExtent = bounds.extents.z;
        float frontDistance = calibratedHeight > 0.01f
            ? frontExtent + calibratedHeight / (2f * TargetHeightFraction * halfVertical)
            : MachineViewController.SolveDistance(cam, renderers, bounds, Vector3.back, TargetHeightFraction, 0.9f);

        // 正面
        cam.transform.position = bounds.center + Vector3.back * frontDistance;
        cam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        if (RenderTo(cam, rt, Path.Combine(PreviewDir, "preview_front_-Z.png")))
            Debug.Log("[Preview] front_-Z");

        // 正面遮罩验证
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = MaskColor;
        string maskPath = Path.Combine(PreviewDir, "preview_front_mask.png");
        if (RenderMaskMeasure(cam, rt, maskPath, out float maskHeight, out float maskWidth))
        {
            Debug.Log($"[Mask] final front silhouette height={maskHeight:F4} width={maskWidth:F4} " +
                      $"target={TargetHeightFraction:F4}");
        }
        cam.clearFlags = originalFlags;
        cam.backgroundColor = originalBackground;

        var directions = new (string name, Vector3 dir)[]
        {
            ("back_+Z", Vector3.forward),
            ("right_+X", Vector3.right),
            ("left_-X", Vector3.left),
        };

        foreach (var (name, dir) in directions)
        {
            float distance = MachineViewController.SolveDistance(cam, renderers, bounds, dir, TargetHeightFraction, 0.9f);
            cam.transform.position = bounds.center + dir * distance;
            cam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);

            string path = Path.Combine(PreviewDir, $"preview_{name}.png");
            if (RenderTo(cam, rt, path))
                Debug.Log("[Preview] " + name);
        }

        cam.targetTexture = originalTarget;
        cam.transform.SetPositionAndRotation(originalPosition, originalRotation);
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
    }

    static bool RenderTo(Camera cam, RenderTexture rt, string path)
    {
        if (!RenderAndRead(cam, rt, out var tex))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
        return true;
    }

    static bool RenderAndRead(Camera cam, RenderTexture rt, out Texture2D tex)
    {
        tex = null;
        try
        {
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
                RenderPipeline.SubmitRenderRequest(cam, request);
            else
                cam.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Preview] 渲染失败: " + e.Message);
            if (tex != null)
                UnityEngine.Object.DestroyImmediate(tex);
            tex = null;
            return false;
        }
    }

    /// <summary>
    /// 渲染纯洋红背景的遮罩，按实际背景色取容差统计非背景像素的包围盒。
    /// </summary>
    static bool RenderMaskMeasure(Camera cam, RenderTexture rt, string savePath,
        out float heightFraction, out float widthFraction)
    {
        heightFraction = 0f;
        widthFraction = 0f;

        if (!RenderAndRead(cam, rt, out var tex))
            return false;

        int w = tex.width;
        int h = tex.height;
        var pixels = tex.GetPixels32();
        Color32 background = pixels[0];

        int minX = w, maxX = -1, minY = h, maxY = -1;
        int count = 0;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                Color32 c = pixels[row + x];
                int diff = Math.Abs(c.r - background.r) + Math.Abs(c.g - background.g) + Math.Abs(c.b - background.b);
                if (diff <= 30)
                    continue;

                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (savePath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));
            File.WriteAllBytes(savePath, tex.EncodeToPNG());
        }

        UnityEngine.Object.DestroyImmediate(tex);

        if (count < 100 || maxX < minX || maxY < minY)
        {
            Debug.LogWarning("[Mask] 没有检测到机床像素。");
            return false;
        }

        widthFraction = (maxX - minX + 1) / (float)w;
        heightFraction = (maxY - minY + 1) / (float)h;
        return true;
    }
}
