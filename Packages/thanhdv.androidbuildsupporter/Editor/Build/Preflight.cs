using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>Everything that would fail the build, checked in a second or two before it starts.</summary>
    internal static class Preflight
    {
        private static readonly Regex PackagePattern = new Regex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$");

        public static async Task<List<string>> RunAsync(BuildVariant variant)
        {
            var errors = new List<string>();
            bool release = variant == BuildVariant.Release;

            if (!AndroidTools.ModuleInstalled)
            {
                errors.Add("The Android Build Support module is not installed for this Editor (Unity Hub > Installs > Add modules).");
                return errors;
            }
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                errors.Add("The active platform is not Android. Switch to Android in Build Settings first.");

            string versionError = VersionCode.Apply();
            if (versionError != null) errors.Add(versionError);
            else if (VersionCode.BuildOf(PlayerSettings.Android.bundleVersionCode) >= VersionCode.MaxBuild)
                errors.Add($"Version {PlayerSettings.bundleVersion} has used all {VersionCode.MaxBuild} build numbers. Raise the version.");

            string package = PlayerSettings.GetApplicationIdentifier(UnityCompat.Android);
            if (!PackagePattern.IsMatch(package ?? ""))
                errors.Add($"Package name '{package}' is not valid: use at least two parts such as com.company.game, each starting with a letter.");

            if (UnityCompat.EnabledScenes.Length == 0) errors.Add("No scene is enabled in Build Settings.");

            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(UnityCompat.Android);
            AndroidArchitecture architectures = PlayerSettings.Android.targetArchitectures;
            if (architectures == AndroidArchitecture.None) errors.Add("Select at least one target architecture.");
            if (backend == ScriptingImplementation.Mono2x && (architectures & AndroidArchitecture.ARM64) != 0)
                errors.Add("Mono cannot build ARM64. Use IL2CPP, or untick ARM64.");
            if (release && (backend != ScriptingImplementation.IL2CPP || (architectures & AndroidArchitecture.ARM64) == 0))
                errors.Add("Google Play requires 64-bit: release builds need IL2CPP with ARM64.");

            if (PlayerSettings.Android.useCustomKeystore)
            {
                string keystoreError = await Keystore.CheckAsync();
                if (keystoreError != null) errors.Add(keystoreError);
            }
            else if (release)
            {
                errors.Add("Release builds must be signed with a custom keystore: Google Play rejects the debug keystore.");
            }

            if (release)
            {
                if (AndroidTools.Bundletool == null) errors.Add("bundletool not found. Set its path in the Build tab.");
                if (AndroidTools.Java == null) errors.Add("java not found in the JDK the Editor uses (Preferences > External Tools).");
            }

            try
            {
                Directory.CreateDirectory(Naming.OutputFolder(variant, DateTime.Now));
            }
            catch (Exception e)
            {
                errors.Add("Cannot create the output folder: " + e.Message);
            }

            if (release && UnityCompat.ProfileOverridesPlayerSettings)
                errors.Add($"Build Profile '{UnityCompat.ActiveProfileName}' overrides Player Settings, which this tool does not edit. Remove the override or switch to the Android platform.");

            return errors.Distinct().ToList();
        }
    }
}
