using System.IO;
using UnityEditor;

// Unity -batchmode -projectPath <project> -executeMethod BuildBundle.Run -quit: packs the cap shader into
// hideoutextensions.bundle for the game's Unity (2022.3.43, Windows).
public static class BuildBundle
{
    public static void Run()
    {
        const string output = "Bundles";
        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild
        {
            assetBundleName = "hideoutextensions.bundle",
            assetNames = new[] { "Assets/CapWithHoles.shader" },
            addressableNames = new[] { "CapWithHoles" },
        };
        BuildPipeline.BuildAssetBundles(output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
    }
}
