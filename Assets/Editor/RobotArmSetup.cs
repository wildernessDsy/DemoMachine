using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// 把 IPP 的机器手臂 OBJ 自动放到 SampleScene 里、机床的右侧。
/// 模型复制进 Assets 后，Unity 导入完成时会自动执行一次；也可用菜单 Tools/DemoMachine/Place Robotic Arm 手动重跑。
/// </summary>
[InitializeOnLoad]
public static class RobotArmSetup
{
    const string ModelPath = "Assets/Models/RobotArm/robotic+arm+3d+model.obj";
    const string MaterialPath = "Assets/Models/RobotArm/RobotArm.mat";
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string RootName = "RoboticArm";
    const float GapFromMachine = 0.45f;

    static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    static string MarkerPath => Path.Combine(ProjectRoot, "Library", "RobotArmSetup.done");

    static RobotArmSetup()
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
        if (!File.Exists(ModelPath))
            return;

        SetupNow();
    }

    [MenuItem("Tools/DemoMachine/Place Robotic Arm")]
    public static void SetupNow()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[RobotArmSetup] 请先退出 Play 模式，再放置机械臂。");
            return;
        }

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            Debug.LogWarning($"[RobotArmSetup] 当前场景不是 {ScenePath}（当前：{scene.path}），已跳过。");
            return;
        }

        if (!File.Exists(ModelPath))
        {
            Debug.LogError($"[RobotArmSetup] 找不到模型文件：{ModelPath}");
            return;
        }

        try
        {
            AssetDatabase.ImportAsset(ModelPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null)
            {
                Debug.LogError("[RobotArmSetup] 模型导入失败或尚未完成，请稍后重新执行菜单。");
                return;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == RootName)
                    UnityEngine.Object.DestroyImmediate(root);
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
            instance.name = RootName;
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            instance.transform.localScale = Vector3.one;

            AssignMaterial(instance);

            var machine = GameObject.Find("五轴机床");
            if (machine == null)
            {
                Debug.LogError("[RobotArmSetup] 场景里找不到机床（五轴机床），不知道往哪边放。");
                return;
            }

            Bounds machineBounds = GetBounds(machine.transform);
            Bounds armBounds = GetBounds(instance.transform);

            float targetX = machineBounds.max.x + GapFromMachine + armBounds.extents.x;
            float targetZ = machineBounds.center.z;
            Vector3 delta = new Vector3(
                targetX - armBounds.center.x,
                -armBounds.min.y,
                targetZ - armBounds.center.z);
            instance.transform.position += delta;

            Bounds placedBounds = GetBounds(instance.transform);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[RobotArmSetup] 已放置 {RootName}：pos={instance.transform.position} " +
                      $"size={placedBounds.size} machineRight={machineBounds.max.x:F3}");

            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
            File.WriteAllText(MarkerPath,
                $"{DateTime.Now:O}\nplace={instance.transform.position}\nsize={placedBounds.size}\n");

            RenderPreview();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    static void AssignMaterial(GameObject instance)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[RobotArmSetup] 找不到 URP Lit shader，保留模型自带材质。");
                return;
            }

            material = new Material(shader) { name = "RobotArm" };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", new Color(0.72f, 0.74f, 0.78f, 1f));
            if (material.HasProperty("_Metallic"))
                material.SetFloat("_Metallic", 0.35f);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", 0.55f);

            AssetDatabase.CreateAsset(material, MaterialPath);
            AssetDatabase.SaveAssets();
        }

        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            int count = Mathf.Max(1, renderer.sharedMaterials.Length);
            var materials = new Material[count];
            for (int i = 0; i < count; i++)
                materials[i] = material;
            renderer.sharedMaterials = materials;
        }
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
            string path = Path.Combine(dir, "robot_arm_preview.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log("[RobotArmSetup] 预览图：" + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RobotArmSetup] 预览渲染失败：" + e.Message);
        }
        finally
        {
            cam.targetTexture = originalTarget;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
