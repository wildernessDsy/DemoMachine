using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 「写实高校工业实验室」环境的程序化建模器（运行时可用的版本）。
///
/// 特点：
///  · 进入 Play / 运行时若场景里没有 LabEnvironment，会自动生成整套实验室（见文件末尾的引导类）；
///  · 编辑器里也可以通过菜单 Tools ▸ DemoMachine 手动生成 / 删除（材质会存成 .mat 资产）；
///  · 运行时生成的材质是内存材质（不落地），所以不会污染工程目录；
///  · 项目原有的「五轴机床」不做任何修改，房间以它为中心。
///
/// 布局（局部坐标，原点 = 机床位置）：
///   X：-7 ~ +7（宽 14m）    Z：-9 ~ +9（深 18m）    Y：0 ~ 4.6（层高）
///   长条窗在 -X 墙（相机左侧），窗外草坪 + 树木；道具在 +X 墙一侧。
/// </summary>
public static class LabEnvironment
{
    public const string RootName = "LabEnvironment";
    const string MaterialFolder = "Assets/LabEnvironment/Materials";

    // ───────── 可调尺寸 ─────────
    public const float RoomWidth = 14f;    // X
    public const float RoomDepth = 18f;    // Z（留足相机后退空间）
    public const float RoomHeight = 4.6f;  // 层高
    public const float WallThickness = 0.2f;

    const float WindowSill = 0.95f;
    const float WindowHead = 3.40f;
    const float WindowHalfLength = 6f;     // 窗沿 Z 方向的一半长度
    const int WindowBays = 6;

    // ───────── 配色 ─────────
    static readonly Color ColorWall = new Color(0.93f, 0.94f, 0.945f);
    static readonly Color ColorFloor = new Color(0.56f, 0.58f, 0.60f);
    static readonly Color ColorCeiling = new Color(0.96f, 0.96f, 0.965f);
    static readonly Color ColorMetalDark = new Color(0.17f, 0.18f, 0.19f);
    static readonly Color ColorMetalMid = new Color(0.42f, 0.44f, 0.47f);
    static readonly Color ColorWood = new Color(0.72f, 0.60f, 0.44f);
    static readonly Color ColorGrass = new Color(0.31f, 0.45f, 0.24f);
    static readonly Color ColorFoliage = new Color(0.24f, 0.40f, 0.20f);
    static readonly Color ColorTrunk = new Color(0.29f, 0.24f, 0.19f);
    static readonly Color ColorYellow = new Color(0.92f, 0.78f, 0.12f);
    static readonly Color ColorRed = new Color(0.72f, 0.09f, 0.09f);
    static readonly Color ColorGreen = new Color(0.10f, 0.52f, 0.26f);
    static readonly Color ColorWhite = new Color(0.95f, 0.95f, 0.95f);

    static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();
    static readonly Dictionary<string, Material> RuntimeMaterials = new Dictionary<string, Material>();

    // ───────────────────────── 生成 / 删除 ─────────────────────────

    /// <summary>在场景里生成整套实验室。若已存在则直接返回已有的根节点。</summary>
    public static GameObject Build()
    {
        var existing = GameObject.Find(RootName);
        if (existing != null)
            return existing;

        var machine = GameObject.Find("五轴机床");
        Vector3 center = machine != null
            ? new Vector3(machine.transform.position.x, 0f, machine.transform.position.z)
            : Vector3.zero;

        if (machine == null)
            Debug.LogWarning("[LabEnvironment] 场景里没找到「五轴机床」，实验室将以世界原点为中心生成。");

        var root = new GameObject(RootName).transform;
        root.position = center;

        BuildShell(root);
        BuildWindowWall(root);
        BuildOutside(root);
        BuildCeilingLights(root);
        BuildProps(root);
        BuildLighting(root);

        Debug.Log("[LabEnvironment] 教学实验室环境已生成：" + RoomWidth + " × " + RoomDepth +
                  " × " + RoomHeight + " m，长条窗在 -X 墙（相机左侧）。", root);

        return root.gameObject;
    }

    /// <summary>删除生成的环境（含运行时自动生成的那份）。</summary>
    public static void Destroy()
    {
        var root = GameObject.Find(RootName);
        if (root == null)
            return;

        if (Application.isPlaying)
            Object.Destroy(root);
        else
            Object.DestroyImmediate(root);
    }

    // ───────────────────────── 建筑外壳 ─────────────────────────

    static void BuildShell(Transform root)
    {
        Material floor = GetMaterial("Floor_Epoxy", ColorFloor, 0.05f, 0.55f);
        Material wall = GetMaterial("Wall_White", ColorWall, 0f, 0.18f);
        Material ceiling = GetMaterial("Ceiling_White", ColorCeiling, 0f, 0.12f);
        Material trim = GetMaterial("Trim_Grey", new Color(0.86f, 0.87f, 0.88f), 0f, 0.25f);
        Material metalDark = GetMaterial("Metal_Dark", ColorMetalDark, 0.6f, 0.45f);
        Material metalMid = GetMaterial("Metal_Mid", ColorMetalMid, 0.7f, 0.5f);
        Material doorMat = GetMaterial("Door_Metal", new Color(0.55f, 0.57f, 0.60f), 0.25f, 0.35f);

        float halfW = RoomWidth * 0.5f;
        float halfD = RoomDepth * 0.5f;
        float h = RoomHeight;

        Box("Floor", root, new Vector3(0f, -0.05f, 0f), new Vector3(RoomWidth, 0.1f, RoomDepth), floor);
        Box("Ceiling", root, new Vector3(0f, h + 0.075f, 0f), new Vector3(RoomWidth, 0.15f, RoomDepth), ceiling);

        // 右墙 +X
        Box("Wall_Right", root, new Vector3(halfW - WallThickness * 0.5f, h * 0.5f, 0f),
            new Vector3(WallThickness, h, RoomDepth), wall);

        // 远墙 +Z
        Box("Wall_Far", root, new Vector3(0f, h * 0.5f, halfD - WallThickness * 0.5f),
            new Vector3(RoomWidth, h, WallThickness), wall);

        // 近墙 -Z（相机身后）
        Box("Wall_Near", root, new Vector3(0f, h * 0.5f, -halfD + WallThickness * 0.5f),
            new Vector3(RoomWidth, h, WallThickness), wall);

        // 踢脚线
        float skirtH = 0.12f;
        float skirtT = 0.03f;
        Box("Skirt_Right", root, new Vector3(halfW - WallThickness - skirtT * 0.5f, skirtH * 0.5f, 0f),
            new Vector3(skirtT, skirtH, RoomDepth - WallThickness * 2f), trim);
        Box("Skirt_Far", root, new Vector3(0f, skirtH * 0.5f, halfD - WallThickness - skirtT * 0.5f),
            new Vector3(RoomWidth - WallThickness * 2f, skirtH, skirtT), trim);
        Box("Skirt_Near", root, new Vector3(0f, skirtH * 0.5f, -halfD + WallThickness + skirtT * 0.5f),
            new Vector3(RoomWidth - WallThickness * 2f, skirtH, skirtT), trim);

        // 右墙上的金属门（装饰用门框 + 门扇）
        float doorX = halfW - WallThickness - 0.02f;
        float doorZ = -5f;

        Box("Door_Frame_Top", root, new Vector3(doorX, 2.25f, doorZ), new Vector3(0.06f, 0.08f, 1.22f), metalDark);
        Box("Door_Frame_L", root, new Vector3(doorX, 1.12f, doorZ - 0.59f), new Vector3(0.06f, 2.24f, 0.08f), metalDark);
        Box("Door_Frame_R", root, new Vector3(doorX, 1.12f, doorZ + 0.59f), new Vector3(0.06f, 2.24f, 0.08f), metalDark);
        Box("Door_Panel", root, new Vector3(doorX - 0.01f, 1.10f, doorZ), new Vector3(0.05f, 2.18f, 1.14f), doorMat);
        Box("Door_Handle", root, new Vector3(doorX - 0.05f, 1.05f, doorZ + 0.48f), new Vector3(0.05f, 0.05f, 0.16f), metalMid);
    }

    // ───────────────────────── 长条窗（-X 墙，相机左侧）─────────────────────────

    static void BuildWindowWall(Transform root)
    {
        Material wall = GetMaterial("Wall_White", ColorWall, 0f, 0.18f);
        Material frame = GetMaterial("Window_Frame_DarkMetal", ColorMetalDark, 0.75f, 0.45f);
        Material glass = GetMaterial("Glass_Clear", new Color(0.72f, 0.82f, 0.86f, 0.20f), 0.1f, 0.92f, true);
        Material sillBoard = GetMaterial("Sill_LightGrey", new Color(0.88f, 0.89f, 0.90f), 0.05f, 0.35f);

        float halfW = RoomWidth * 0.5f;
        float halfD = RoomDepth * 0.5f;
        float h = RoomHeight;
        float x = -halfW + WallThickness * 0.5f;

        float innerHalf = WindowHalfLength;
        float pierLength = halfD - innerHalf;

        // 窗两侧的墙垛（沿 Z）
        Box("Wall_Window_Pier_N", root, new Vector3(x, h * 0.5f, -(innerHalf + pierLength * 0.5f)),
            new Vector3(WallThickness, h, pierLength), wall);
        Box("Wall_Window_Pier_S", root, new Vector3(x, h * 0.5f, innerHalf + pierLength * 0.5f),
            new Vector3(WallThickness, h, pierLength), wall);

        // 窗台以下 / 窗顶以上
        Box("Wall_Window_SillBand", root, new Vector3(x, WindowSill * 0.5f, 0f),
            new Vector3(WallThickness, WindowSill, innerHalf * 2f), wall);

        float headH = h - WindowHead;
        Box("Wall_Window_HeadBand", root, new Vector3(x, WindowHead + headH * 0.5f, 0f),
            new Vector3(WallThickness, headH, innerHalf * 2f), wall);

        // 室内窗台板
        Box("Window_Sill", root, new Vector3(x + WallThickness * 0.5f + 0.13f, WindowSill + 0.02f, 0f),
            new Vector3(0.3f, 0.04f, innerHalf * 2f + 0.2f), sillBoard);

        // 玻璃
        float glassH = WindowHead - WindowSill;
        Box("Window_Glass", root, new Vector3(x, WindowSill + glassH * 0.5f, 0f),
            new Vector3(0.02f, glassH, innerHalf * 2f), glass);

        // 窗框
        float barX = x + WallThickness * 0.5f + 0.02f;
        Box("Window_Frame_Bottom", root, new Vector3(barX, WindowSill + 0.035f, 0f),
            new Vector3(0.1f, 0.07f, innerHalf * 2f), frame);
        Box("Window_Frame_Top", root, new Vector3(barX, WindowHead - 0.035f, 0f),
            new Vector3(0.1f, 0.07f, innerHalf * 2f), frame);
        Box("Window_Frame_N", root, new Vector3(barX, WindowSill + glassH * 0.5f, -innerHalf + 0.035f),
            new Vector3(0.1f, glassH, 0.07f), frame);
        Box("Window_Frame_S", root, new Vector3(barX, WindowSill + glassH * 0.5f, innerHalf - 0.035f),
            new Vector3(0.1f, glassH, 0.07f), frame);

        // 竖框
        for (int i = 1; i < WindowBays; i++)
        {
            float z = Mathf.Lerp(-innerHalf, innerHalf, (float)i / WindowBays);
            Box("Window_Mullion_" + i, root, new Vector3(barX, WindowSill + glassH * 0.5f, z),
                new Vector3(0.09f, glassH, 0.05f), frame);
        }

        // 中横档
        Box("Window_Transom", root, new Vector3(barX, WindowSill + glassH * 0.5f, 0f),
            new Vector3(0.09f, 0.045f, innerHalf * 2f), frame);
    }

    // ───────────────────────── 室外 ─────────────────────────

    static void BuildOutside(Transform root)
    {
        Material grass = GetMaterial("Grass", ColorGrass, 0f, 0.08f);
        Material walkway = GetMaterial("Concrete_Walkway", new Color(0.74f, 0.75f, 0.74f), 0f, 0.2f);
        Material trunk = GetMaterial("Tree_Trunk", ColorTrunk, 0f, 0.1f);
        Material foliage = GetMaterial("Tree_Foliage", ColorFoliage, 0f, 0.08f);
        Material hedge = GetMaterial("Hedge", new Color(0.21f, 0.35f, 0.18f), 0f, 0.1f);

        float halfW = RoomWidth * 0.5f;

        // 草坪（-X 侧）
        Box("Outside_Grass", root, new Vector3(-(halfW + 26f), -0.18f, 0f),
            new Vector3(52f, 0.3f, 70f), grass);

        // 窗外散水 / 走道
        Box("Outside_Walkway", root, new Vector3(-(halfW + 1.4f), -0.02f, 0f),
            new Vector3(2.6f, 0.12f, RoomDepth + 2f), walkway);

        // 绿篱
        Box("Outside_Hedge", root, new Vector3(-(halfW + 3.1f), 0.42f, 0f),
            new Vector3(0.7f, 0.85f, RoomDepth + 2f), hedge);

        // 树木
        Vector3[] treePositions =
        {
            new Vector3(-13f, 0f, -8f),
            new Vector3(-17f, 0f, -3f),
            new Vector3(-12f, 0f, 2f),
            new Vector3(-18f, 0f, 7f),
            new Vector3(-13f, 0f, 11f),
            new Vector3(-22f, 0f, 1f),
        };

        float[] treeHeights = { 2.4f, 3.0f, 2.2f, 3.4f, 2.6f, 3.1f };

        for (int i = 0; i < treePositions.Length; i++)
        {
            var tree = new GameObject("Tree_" + (i + 1)).transform;
            tree.SetParent(root, false);
            tree.localPosition = treePositions[i];

            float trunkH = treeHeights[i];
            Cylinder("Trunk", tree, new Vector3(0f, trunkH * 0.5f, 0f),
                new Vector3(0.28f, trunkH * 0.5f, 0.28f), trunk);

            Sphere("Foliage_A", tree, new Vector3(0f, trunkH + 0.55f, 0f), new Vector3(2.4f, 2.2f, 2.4f), foliage);
            Sphere("Foliage_B", tree, new Vector3(0.55f, trunkH + 0.05f, 0.35f), new Vector3(1.7f, 1.6f, 1.7f), foliage);
            Sphere("Foliage_C", tree, new Vector3(-0.5f, trunkH + 0.15f, -0.3f), new Vector3(1.6f, 1.5f, 1.6f), foliage);
        }
    }

    // ───────────────────────── 顶部 LED ─────────────────────────

    static void BuildCeilingLights(Transform root)
    {
        Material fixture = GetMaterial("LED_Fixture_Body", new Color(0.80f, 0.81f, 0.82f), 0.4f, 0.5f);
        Material led = GetMaterial("LED_Emissive", new Color(1f, 0.99f, 0.96f), 0f, 0.2f,
            false, new Color(1f, 0.98f, 0.94f), 2.2f);

        float y = RoomHeight - 0.09f;
        float[] xs = { -4f, 0f, 4f };
        float[] zs = { -4f, 4f };

        for (int zi = 0; zi < zs.Length; zi++)
        {
            for (int xi = 0; xi < xs.Length; xi++)
            {
                var group = new GameObject($"Ceiling_Light_{zi}_{xi}").transform;
                group.SetParent(root, false);
                group.localPosition = new Vector3(xs[xi], 0f, zs[zi]);

                Box("Body", group, new Vector3(0f, y, 0f), new Vector3(1.5f, 0.07f, 0.22f), fixture);
                Box("LED", group, new Vector3(0f, y - 0.045f, 0f), new Vector3(1.42f, 0.02f, 0.16f), led);

                var lightGo = new GameObject("PointLight");
                lightGo.transform.SetParent(group, false);
                lightGo.transform.localPosition = new Vector3(0f, y - 0.45f, 0f);

                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.98f, 0.94f);
                light.intensity = 2.4f;
                light.range = 9f;
                light.shadows = LightShadows.None;
            }
        }
    }

    // ───────────────────────── 室内道具（+X 墙一侧）─────────────────────────

    static void BuildProps(Transform root)
    {
        float halfW = RoomWidth * 0.5f;

        Material wood = GetMaterial("Workbench_Top_Wood", ColorWood, 0f, 0.25f);
        Material metalMid = GetMaterial("Metal_Mid", ColorMetalMid, 0.7f, 0.5f);
        Material metalDark = GetMaterial("Metal_Dark", ColorMetalDark, 0.6f, 0.45f);
        Material cabinet = GetMaterial("Cabinet_BlueGrey", new Color(0.28f, 0.33f, 0.38f), 0.35f, 0.45f);
        Material white = GetMaterial("Plastic_White", ColorWhite, 0f, 0.2f);
        Material yellow = GetMaterial("Mark_Yellow", ColorYellow, 0f, 0.3f);

        // 工作台（靠右墙，长轴沿 Z）
        var bench = new GameObject("Workbench").transform;
        bench.SetParent(root, false);
        bench.localPosition = new Vector3(halfW - WallThickness - 0.45f, 0f, -1.5f);

        Box("Bench_Top", bench, new Vector3(0f, 0.90f, 0f), new Vector3(0.78f, 0.06f, 2.2f), wood);
        Box("Bench_Shelf", bench, new Vector3(0f, 0.26f, 0f), new Vector3(0.68f, 0.03f, 2.1f), metalMid);

        for (int i = 0; i < 4; i++)
        {
            float lx = (i % 2 == 0) ? -0.32f : 0.32f;
            float lz = (i < 2) ? -1.02f : 1.02f;
            Box("Bench_Leg_" + i, bench, new Vector3(lx, 0.44f, lz), new Vector3(0.06f, 0.88f, 0.06f), metalMid);
        }

        // 工具柜（靠右墙）
        var cab = new GameObject("ToolCabinet").transform;
        cab.SetParent(root, false);
        cab.localPosition = new Vector3(halfW - WallThickness - 0.28f, 0f, 2.8f);

        Box("Cab_Body", cab, new Vector3(0f, 0.93f, 0f), new Vector3(0.5f, 1.86f, 1.0f), cabinet);

        for (int i = 0; i < 3; i++)
        {
            float doorY = 0.40f + i * 0.58f;
            Box("Cab_Door_" + i, cab, new Vector3(-0.26f, doorY, 0f), new Vector3(0.03f, 0.54f, 0.94f), metalMid);
            Box("Cab_Handle_" + i, cab, new Vector3(-0.29f, doorY, 0.38f), new Vector3(0.02f, 0.16f, 0.03f), metalDark);
        }

        // 灭火器（右墙门边）
        var ext = new GameObject("FireExtinguisher").transform;
        ext.SetParent(root, false);
        ext.localPosition = new Vector3(halfW - WallThickness - 0.12f, 0f, -4.2f);

        Box("Ext_Bracket", ext, new Vector3(0f, 1.05f, 0f), new Vector3(0.10f, 0.42f, 0.05f), metalDark);

        Material red = GetMaterial("Extinguisher_Red", ColorRed, 0.1f, 0.45f);
        Cylinder("Ext_Body", ext, new Vector3(-0.11f, 1.02f, 0f), new Vector3(0.16f, 0.24f, 0.16f), red);
        Cylinder("Ext_Label", ext, new Vector3(-0.11f, 1.06f, 0f), new Vector3(0.163f, 0.05f, 0.163f), white);
        Cylinder("Ext_Valve", ext, new Vector3(-0.11f, 1.30f, 0f), new Vector3(0.07f, 0.04f, 0.07f), metalDark);
        Box("Ext_Horn", ext, new Vector3(-0.11f, 1.36f, 0.10f), new Vector3(0.05f, 0.05f, 0.16f), metalDark);

        // 安全标识（右墙 + 远墙）
        float signX = halfW - WallThickness - 0.015f;
        CreateSign(root, "Sign_Exit", new Vector3(signX, 1.75f, 4.4f),
            new Vector3(0.03f, 0.30f, 0.52f), ColorGreen, Quaternion.Euler(0f, -90f, 0f));
        CreateSign(root, "Sign_Warning", new Vector3(signX, 1.75f, 5.3f),
            new Vector3(0.03f, 0.46f, 0.46f), ColorYellow, Quaternion.Euler(0f, -90f, 0f));
        CreateSign(root, "Sign_NoSmoking", new Vector3(signX, 1.75f, 6.2f),
            new Vector3(0.03f, 0.46f, 0.46f), ColorRed, Quaternion.Euler(0f, -90f, 0f));

        // 地面黄色警示线（围出机床工作区）
        var mark = new GameObject("FloorMarking").transform;
        mark.SetParent(root, false);

        float mw = 6.6f;
        float md = 4.6f;
        float lineT = 0.10f;
        float markY = 0.006f;

        Box("Mark_N", mark, new Vector3(0f, markY, md * 0.5f), new Vector3(mw, 0.012f, lineT), yellow);
        Box("Mark_S", mark, new Vector3(0f, markY, -md * 0.5f), new Vector3(mw, 0.012f, lineT), yellow);
        Box("Mark_W", mark, new Vector3(-mw * 0.5f, markY, 0f), new Vector3(lineT, 0.012f, md), yellow);
        Box("Mark_E", mark, new Vector3(mw * 0.5f, markY, 0f), new Vector3(lineT, 0.012f, md), yellow);
    }

    static void CreateSign(Transform parent, string name, Vector3 localPos, Vector3 size, Color color, Quaternion rotation)
    {
        var signRoot = new GameObject(name).transform;
        signRoot.SetParent(parent, false);
        signRoot.localPosition = localPos;
        signRoot.localRotation = rotation;

        Material plate = GetMaterial("Sign_" + ColorToKey(color), color, 0f, 0.3f);
        Material white = GetMaterial("Plastic_White", ColorWhite, 0f, 0.2f);

        Box("Plate", signRoot, Vector3.zero, size, plate);

        Vector3 innerSize = new Vector3(
            Mathf.Max(size.x * 0.42f, 0.004f),
            Mathf.Max(size.y * 0.55f, 0.004f),
            Mathf.Max(size.z * 0.42f, 0.004f));

        Vector3 innerOffset = new Vector3(
            size.x < 0.05f ? -0.006f : 0f,
            0f,
            size.z < 0.05f ? -0.006f : 0f);

        Box("Inner", signRoot, innerOffset, innerSize, white);
    }

    static string ColorToKey(Color c)
    {
        if (c == ColorGreen) return "Green";
        if (c == ColorYellow) return "Yellow";
        if (c == ColorRed) return "Red";
        return "Other";
    }

    // ───────────────────────── 灯光 ─────────────────────────

    static void BuildLighting(Transform root)
    {
        var existing = GameObject.Find("Directional Light");
        Light sun;

        if (existing != null && existing.GetComponent<Light>() != null)
        {
            sun = existing.GetComponent<Light>();
        }
        else
        {
            var sunGo = new GameObject("Directional Light");
            sunGo.transform.SetParent(root, false);
            sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
        }

        sun.color = new Color(1f, 0.96f, 0.90f);
        sun.intensity = 1.15f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.75f;
        sun.transform.rotation = Quaternion.Euler(38f, 90f, 0f);   // 从 -X 窗斜射进来

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.45f, 0.50f);
        RenderSettings.fog = false;
        RenderSettings.reflectionIntensity = 0.6f;
    }

    // ───────────────────────── 图元 / 材质 ─────────────────────────

    static GameObject Box(string name, Transform parent, Vector3 localPosition, Vector3 size, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = size;
        StripCollider(go);
        ApplyMaterial(go, material);
        return go;
    }

    static GameObject Cylinder(string name, Transform parent, Vector3 localPosition, Vector3 size, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = size;
        StripCollider(go);
        ApplyMaterial(go, material);
        return go;
    }

    static GameObject Sphere(string name, Transform parent, Vector3 localPosition, Vector3 size, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = size;
        StripCollider(go);
        ApplyMaterial(go, material);
        return go;
    }

    static void StripCollider(GameObject go)
    {
        var collider = go.GetComponent<Collider>();
        if (collider != null)
        {
            if (Application.isPlaying)
                Object.Destroy(collider);
            else
                Object.DestroyImmediate(collider);
        }
    }

    static void ApplyMaterial(GameObject go, Material material)
    {
        if (material == null)
            return;

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;
    }

    static Material GetMaterial(string name, Color color, float metallic = 0f, float smoothness = 0.3f,
                                bool transparent = false, Color? emissionColor = null, float emissionIntensity = 0f)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            if (Materials.TryGetValue(name, out Material editorCached) && editorCached != null)
                return editorCached;

            EnsureMaterialFolder();
            string path = MaterialFolder + "/" + name + ".mat";
            var existingAsset = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existingAsset != null)
            {
                Materials[name] = existingAsset;
                return existingAsset;
            }

            var created = CreateMaterial(name, color, metallic, smoothness, transparent, emissionColor, emissionIntensity);
            AssetDatabase.CreateAsset(created, path);
            Materials[name] = created;
            return created;
        }
#endif
        if (RuntimeMaterials.TryGetValue(name, out Material runtimeCached) && runtimeCached != null)
            return runtimeCached;

        var runtime = CreateMaterial(name, color, metallic, smoothness, transparent, emissionColor, emissionIntensity);
        RuntimeMaterials[name] = runtime;
        return runtime;
    }

    static Material CreateMaterial(string name, Color color, float metallic, float smoothness,
                                    bool transparent, Color? emissionColor, float emissionIntensity)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Diffuse");

        var material = new Material(shader) { name = name };

        material.SetColor("_BaseColor", color);
        material.SetColor("_Color", color);
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_Glossiness", smoothness);

        if (emissionColor.HasValue && emissionIntensity > 0f)
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", emissionColor.Value * emissionIntensity);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        if (transparent)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
        }
        else
        {
            material.renderQueue = -1;
        }

        return material;
    }

#if UNITY_EDITOR
    static void EnsureMaterialFolder()
    {
        if (!AssetDatabase.IsValidFolder("Assets/LabEnvironment"))
            AssetDatabase.CreateFolder("Assets", "LabEnvironment");

        if (!AssetDatabase.IsValidFolder(MaterialFolder))
            AssetDatabase.CreateFolder("Assets/LabEnvironment", "Materials");
    }
#endif
}

/// <summary>
/// 「常态化」入口：每次进入 Play（或游戏启动）时，如果场景里没有实验室就自动生成。
/// 项目里已经手动生成过 LabEnvironment 的话，这里不会重复生成。
/// 不想要自动生成：把下面这个 static class 整段删掉即可。
/// </summary>
public static class LabEnvironmentAutoBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBuild()
    {
        if (GameObject.Find(LabEnvironment.RootName) != null)
            return;

        LabEnvironment.Build();
    }
}
