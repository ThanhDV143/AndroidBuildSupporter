using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// Unity settings that have no public API, or differ between Unity 2022.3 and Unity 6. Every version check and
    /// every reflection lives here; each member says whether it works in this Editor.
    /// </summary>
    internal static class UnityCompat
    {
        public static readonly NamedBuildTarget Android = NamedBuildTarget.Android;

        // --- Override Default Package Name: a Player Settings property with no public API ---------------------

        private const string OverridePackageProperty = "overrideDefaultApplicationIdentifier";
        private static SerializedObject playerSettings;

        private static SerializedProperty OverridePackage
        {
            get
            {
                try
                {
                    if (playerSettings == null || playerSettings.targetObject == null)
                    {
                        UnityEngine.Object asset = Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings");
                        if (asset == null) return null;
                        playerSettings = new SerializedObject(asset);
                    }
                    playerSettings.Update();
                    return playerSettings.FindProperty(OverridePackageProperty);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static bool CanOverridePackage => OverridePackage != null;

        public static bool OverridePackageName
        {
            get => OverridePackage?.boolValue ?? true;
            set
            {
                SerializedProperty property = OverridePackage;
                if (property == null || property.boolValue == value) return;
                property.boolValue = value;
                playerSettings.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // --- Compression Method of Build Settings: internal in both Unity versions -----------------------------

        private static readonly MethodInfo GetCompression = typeof(EditorUserBuildSettings).GetMethod(
            "GetCompressionType", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(BuildTargetGroup) }, null);
        private static readonly MethodInfo SetCompression = typeof(EditorUserBuildSettings).GetMethod(
            "SetCompressionType", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
            GetCompression == null ? Type.EmptyTypes : new[] { typeof(BuildTargetGroup), GetCompression.ReturnType }, null);

        /// <summary>False when the Editor's Compression Method cannot be read; the tool then keeps its own value.</summary>
        public static bool CanSyncCompression => GetCompression != null && SetCompression != null && GetCompression.ReturnType.IsEnum;

        public static CompressionMethod Compression
        {
            get
            {
                if (!CanSyncCompression) return BuildSupporterSettings.instance.compression;
                try
                {
                    string name = GetCompression.Invoke(null, new object[] { BuildTargetGroup.Android }).ToString();
                    return FromUnityName(name);
                }
                catch (Exception)
                {
                    return BuildSupporterSettings.instance.compression;
                }
            }
            set
            {
                BuildSupporterSettings settings = BuildSupporterSettings.instance;
                if (settings.compression != value)
                {
                    settings.compression = value;
                    settings.Save();
                }
                if (!CanSyncCompression) return;

                Type enumType = GetCompression.ReturnType;
                string unityName = Enum.GetNames(enumType).FirstOrDefault(n => FromUnityName(n) == value);
                if (unityName == null) return;
                try
                {
                    SetCompression.Invoke(null, new[] { BuildTargetGroup.Android, Enum.Parse(enumType, unityName) });
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Android Build Supporter] Could not set Compression Method in Build Settings: " + e.InnerException?.Message);
                }
            }
        }

        private static CompressionMethod FromUnityName(string name)
        {
            if (string.Equals(name, "Lz4HC", StringComparison.OrdinalIgnoreCase)) return CompressionMethod.LZ4HC;
            if (string.Equals(name, "Lz4", StringComparison.OrdinalIgnoreCase)) return CompressionMethod.LZ4;
            return CompressionMethod.Default;
        }

        public static BuildOptions ToBuildOptions(CompressionMethod method) =>
            method == CompressionMethod.LZ4 ? BuildOptions.CompressWithLz4 :
            method == CompressionMethod.LZ4HC ? BuildOptions.CompressWithLz4HC : BuildOptions.None;

        // --- Build Profiles (Unity 6) --------------------------------------------------------------------------

        /// <summary>Name of the active Build Profile, or null when the platform itself is active (always on 2022.3).</summary>
        public static string ActiveProfileName
        {
            get
            {
#if UNITY_6000_0_OR_NEWER
                return UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile()?.name;
#else
                return null;
#endif
            }
        }

        /// <summary>Scripting Defines the active Build Profile adds on top of Player Settings.</summary>
        public static string[] ProfileDefines
        {
            get
            {
#if UNITY_6000_0_OR_NEWER
                return UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile()?.scriptingDefines ?? Array.Empty<string>();
#else
                return Array.Empty<string>();
#endif
            }
            set
            {
#if UNITY_6000_0_OR_NEWER
                UnityEditor.Build.Profile.BuildProfile profile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
                if (profile == null) return;
                profile.scriptingDefines = value;
                EditorUtility.SetDirty(profile);
#endif
            }
        }

        /// <summary>
        /// Whether the active Build Profile keeps its own Player Settings, so values edited through the Player Settings
        /// API may not be the ones it builds with. Unknown (false) when Unity's internals change.
        /// </summary>
        public static bool ProfileOverridesPlayerSettings
        {
            get
            {
#if UNITY_6000_0_OR_NEWER
                UnityEditor.Build.Profile.BuildProfile profile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
                if (profile == null) return false;
                try
                {
                    PropertyInfo property = profile.GetType().GetProperty("playerSettings", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    return property != null && property.GetValue(profile) as UnityEngine.Object != null;
                }
                catch (Exception)
                {
                    return false;
                }
#else
                return false;
#endif
            }
        }

        /// <summary>The scenes a build of the active platform or Build Profile contains.</summary>
        public static string[] EnabledScenes
        {
            get
            {
                EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
#if UNITY_6000_0_OR_NEWER
                UnityEditor.Build.Profile.BuildProfile profile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
                if (profile != null)
                {
                    FieldInfo overrides = profile.GetType().GetField("m_OverrideGlobalSceneList", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (overrides != null && overrides.GetValue(profile) is bool own && own) scenes = profile.scenes;
                }
#endif
                return scenes.Where(s => s.enabled && !string.IsNullOrEmpty(s.path)).Select(s => s.path).ToArray();
            }
        }

        // --- Icons ---------------------------------------------------------------------------------------------

        public static Texture2D DefaultIcon
        {
            get => PlayerSettings.GetIcons(NamedBuildTarget.Unknown, IconKind.Any).FirstOrDefault();
            set => PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { value }, IconKind.Any);
        }

        /// <summary>Whether Android has icons of its own (Adaptive, Round, Legacy), which replace the Default Icon.</summary>
        public static bool HasAndroidIcons
        {
            get
            {
                try
                {
                    return PlayerSettings.GetSupportedIconKinds(Android)
                        .SelectMany(kind => PlayerSettings.GetPlatformIcons(Android, kind))
                        .Any(icon => icon.GetTextures().Any(t => t != null));
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
