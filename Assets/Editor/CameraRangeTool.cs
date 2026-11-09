using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 放开 MachineViewController 的移动范围，让相机能绕到机床后方。
/// 菜单：Tools/相机/放开移动范围
/// </summary>
public static class CameraRangeTool
{
    [MenuItem("Tools/相机/启用我的世界式控制（WASD+鼠标）", false, 2)]
    static void EnableFirstPerson()
    {
        var cam = Camera.main;
        if (cam == null)
        {
            Debug.LogError("[相机] 场景里找不到 Main Camera");
            return;
        }

        var fps = cam.GetComponent<FirstPersonCamera>();
        if (fps == null)
            fps = Undo.AddComponent<FirstPersonCamera>(cam.gameObject);

        var cc = cam.GetComponent<CharacterController>();
        if (cc == null)
            cc = Undo.AddComponent<CharacterController>(cam.gameObject);
        cc.height = 1.7f;
        cc.radius = 0.3f;
        cc.center = new Vector3(0f, -0.85f, 0f);
        cc.stepOffset = 0.35f;
        cc.slopeLimit = 50f;

        Undo.RecordObject(fps, "Tune first person");
        fps.mouseSensitivity = 1f;
        fps.degreesPerPixel = 0.07f;
        fps.moveSpeed = 3.2f;
        fps.sprintMultiplier = 1.8f;
        fps.verticalSpeed = 2.2f;
        fps.gravity = 16f;
        fps.addMachineCollider = true;
        EditorUtility.SetDirty(fps);

        var controller = cam.GetComponent<MachineViewController>();
        if (controller != null)
        {
            Undo.RecordObject(controller, "Enable first person");
            controller.freeWalk = true;
            EditorUtility.SetDirty(controller);
        }

        var scene = cam.gameObject.scene;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[相机] 已启用第一人称控制：WASD 移动、空格跳、鼠标转头、Shift 加速、Esc 释放鼠标。");
    }

    [MenuItem("Tools/相机/关闭我的世界式控制", false, 3)]
    static void DisableFirstPerson()
    {
        var cam = Camera.main;
        if (cam == null)
            return;

        var fps = cam.GetComponent<FirstPersonCamera>();
        if (fps != null)
        {
            Undo.DestroyObjectImmediate(fps);
            var cc = cam.GetComponent<CharacterController>();
            if (cc != null)
                Undo.DestroyObjectImmediate(cc);
            var scene = cam.gameObject.scene;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[相机] 已移除第一人称控制，恢复原来的 F/R 视角模式。");
        }
    }

    [MenuItem("Tools/相机/放开移动范围（可绕到机床后方）", false, 1)]
    static void Widen()
    {
        var cam = Camera.main;
        if (cam == null)
        {
            Debug.LogError("[相机] 场景里找不到 Main Camera");
            return;
        }

        var controller = cam.GetComponent<MachineViewController>();
        if (controller == null)
        {
            Debug.LogError("[相机] Main Camera 上没有 MachineViewController 组件");
            return;
        }

        Undo.RecordObject(controller, "Widen camera range");
        controller.maxRetreat = 40f;   // 后退 40 米，实验室里基本没有边界感
        controller.maxStrafe = 25f;    // 左右各 25 米
        controller.maxVertical = 12f;  // 上下 12 米
        controller.closestStandoff = -0.7f; // 保持“不能钻进机床内部”
        controller.freeWalk = true;         // 彻底取消矩形移动范围
        EditorUtility.SetDirty(controller);

        var scene = controller.gameObject.scene;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[相机] 已取消移动范围限制（freeWalk = true），可以绕到机床任意一侧；场景已保存。");
    }
}
