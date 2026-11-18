using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// Changes the settings one build needs and puts them back when disposed, also when the build fails: the release
    /// symbols are removed from Player Settings and the active Build Profile (Unity 6), and Build App Bundle is set.
    /// </summary>
    internal sealed class BuildScope : IDisposable
    {
        private readonly string[] defines;
        private readonly string[] profileDefines;
        private readonly bool appBundle;
        private readonly bool exportProject;

        public BuildScope(IReadOnlyCollection<string> removeSymbols, bool buildAppBundle)
        {
            appBundle = EditorUserBuildSettings.buildAppBundle;
            exportProject = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            EditorUserBuildSettings.buildAppBundle = buildAppBundle;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;

            PlayerSettings.GetScriptingDefineSymbols(UnityCompat.Android, out defines);
            profileDefines = UnityCompat.ProfileDefines;
            if (removeSymbols.Count == 0) return;

            string[] kept = Without(defines, removeSymbols);
            if (kept.Length != defines.Length) PlayerSettings.SetScriptingDefineSymbols(UnityCompat.Android, kept);
            string[] keptProfile = Without(profileDefines, removeSymbols);
            if (keptProfile.Length != profileDefines.Length) UnityCompat.ProfileDefines = keptProfile;
        }

        /// <summary>Symbols of Player Settings and the active Build Profile that the coming build compiles with.</summary>
        public static string[] CurrentSymbols
        {
            get
            {
                PlayerSettings.GetScriptingDefineSymbols(UnityCompat.Android, out string[] symbols);
                return symbols.Concat(UnityCompat.ProfileDefines).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToArray();
            }
        }

        public void Dispose()
        {
            EditorUserBuildSettings.buildAppBundle = appBundle;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = exportProject;

            PlayerSettings.GetScriptingDefineSymbols(UnityCompat.Android, out string[] now);
            if (!now.SequenceEqual(defines)) PlayerSettings.SetScriptingDefineSymbols(UnityCompat.Android, defines);
            if (!UnityCompat.ProfileDefines.SequenceEqual(profileDefines)) UnityCompat.ProfileDefines = profileDefines;

            // SaveAssetIfDirty does not write ProjectSettings.asset; only SaveAssets does.
            AssetDatabase.SaveAssets();
        }

        private static string[] Without(string[] symbols, IReadOnlyCollection<string> remove) =>
            symbols.Where(s => !remove.Contains(s.Trim())).ToArray();
    }
}
