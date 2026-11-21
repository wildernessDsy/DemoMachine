using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildWebGLCli
{
    const string OutDir = "E:/WebGLBuild/game";

    [MenuItem("Tools/Build WebGL (game)")]
    public static void Build()
    {
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = false;

        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = OutDir,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result == BuildResult.Succeeded)
            Debug.Log("[BuildWebGLCli] 构建成功: " + OutDir);
        else
            Debug.LogError("[BuildWebGLCli] 构建失败: " + report.summary.result);
    }
}
