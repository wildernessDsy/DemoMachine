using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>一键把「托盘安装 + 转台运行」效果挂到场景里的五轴机床上。</summary>
public static class PalletDemoSetup
{
    const string MachineName = "五轴机床";
    const string TablePath = "A轴/C轴";

    [MenuItem("Tools/DemoMachine/添加托盘安装 + 转台运行效果", false, 20)]
    public static void AddPalletEffect()
    {
        Scene scene = SceneManager.GetActiveScene();
        GameObject machine = FindMachine();

        if (machine == null)
        {
            Debug.LogError($"[托盘安装] 当前场景里找不到 {MachineName}，请先执行 Tools/DemoMachine/Setup Scene。");
            return;
        }

        Transform table = machine.transform.Find(TablePath);
        if (table == null)
        {
            Debug.LogError($"[托盘安装] 在 {MachineName} 下找不到 {TablePath}。", machine);
            return;
        }

        var effect = machine.GetComponent<PalletInstallEffect>();
        if (effect == null)
            effect = Undo.AddComponent<PalletInstallEffect>(machine);

        Undo.RecordObject(effect, "配置托盘安装效果");
        effect.tableRoot = table;
        effect.tablePath = TablePath;
        effect.machineRootName = MachineName;

        EditorUtility.SetDirty(effect);
        Selection.activeGameObject = machine;
        EditorSceneManager.MarkSceneDirty(scene);

        Debug.Log($"[托盘安装] 已挂到 {MachineName}，转台目标 = {table.name}。按 Play 即可看到托盘降下 → 落位 → 转台运行。", machine);
    }

    static GameObject FindMachine()
    {
        var existing = Object.FindAnyObjectByType<PalletInstallEffect>();
        if (existing != null)
            return existing.gameObject;

        return GameObject.Find(MachineName);
    }
}
