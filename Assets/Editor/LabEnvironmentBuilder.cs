using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 教学实验室环境的编辑器菜单入口（建模逻辑在 Assets/Scripts/LabEnvironment.cs，运行时也能用）。
///
/// Tools ▸ DemoMachine ▸ ① 生成教学实验室环境 —— 把整套环境生成到当前场景，材质存成 .mat 资产；
/// Tools ▸ DemoMachine ▸ ② 删除教学实验室环境 —— 把生成出来的 LabEnvironment 删掉。
///
/// 说明：进入 Play 时，如果场景里没有 LabEnvironment，LabEnvironmentAutoBootstrap 会自动生成一份
///      （运行时材质只存在内存里，不会写进工程目录）。所以平时不用点这个菜单也能看到实验室。
/// </summary>
public static class LabEnvironmentMenu
{
    [MenuItem("Tools/DemoMachine/① 生成教学实验室环境到当前场景", false, 100)]
    public static void BuildIntoScene()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("提示", "请先退出 Play 模式，再手动生成到场景里。\n（Play 模式下会自动生成，不需要手点）", "好");
            return;
        }

        var existing = GameObject.Find(LabEnvironment.RootName);
        if (existing != null)
        {
            bool rebuild = EditorUtility.DisplayDialog(
                "已存在实验室环境",
                "当前场景里已经有 " + LabEnvironment.RootName + " 了，要删掉重建吗？",
                "重建", "取消");
            if (!rebuild)
            {
                Selection.activeGameObject = existing;
                return;
            }

            Undo.DestroyObjectImmediate(existing);
        }

        var root = LabEnvironment.Build();
        if (root != null)
        {
            Undo.RegisterCreatedObjectUndo(root, "生成教学实验室环境");
            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Debug.Log("[LabEnvironment] 已生成到当前场景，记得 Ctrl+S 保存。", root);
        }
    }

    [MenuItem("Tools/DemoMachine/② 删除教学实验室环境", false, 101)]
    public static void RemoveFromScene()
    {
        var existing = GameObject.Find(LabEnvironment.RootName);
        if (existing == null)
        {
            Debug.Log("[LabEnvironment] 场景里没有 " + LabEnvironment.RootName + "，无需删除。");
            return;
        }

        if (Application.isPlaying)
            Object.Destroy(existing);
        else
            Undo.DestroyObjectImmediate(existing);

        Debug.Log("[LabEnvironment] 已删除 " + LabEnvironment.RootName + "。");
    }
}
