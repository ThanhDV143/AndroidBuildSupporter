using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// The JDK, SDK and bundletool the Editor itself uses for Android, so nothing needs installing. Found through
    /// reflection so this package compiles without the Android module.
    /// </summary>
    internal static class AndroidTools
    {
        private static readonly string Exe = Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : "";

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        public static bool ModuleInstalled => BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);

        private static string PlaybackEngine
        {
            get
            {
                try
                {
                    return BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.Android, BuildOptions.None);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>Android External Tools in Preferences; empty paths mean the copies inside the Android module.</summary>
        private static string ExternalTool(string property, string embedded)
        {
            Type settings = Type.GetType("UnityEditor.Android.AndroidExternalToolsSettings, UnityEditor.Android.Extensions");
            string path = settings?.GetProperty(property, BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
            string engine = PlaybackEngine;
            return engine == null ? null : Path.Combine(engine, embedded);
        }

        public static string JdkRoot => ExternalTool("jdkRootPath", "OpenJDK");
        public static string SdkRoot => ExternalTool("sdkRootPath", "SDK");

        public static string Java => Tool(JdkRoot, "bin", "java" + Exe);
        public static string Keytool => Tool(JdkRoot, "bin", "keytool" + Exe);
        public static string Adb => Tool(SdkRoot, "platform-tools", "adb" + Exe);

        /// <summary>The jar set in the window, otherwise the one in the Android module's Tools folder.</summary>
        public static string Bundletool
        {
            get
            {
                string custom = LocalSettings.instance.bundletoolPath;
                if (!string.IsNullOrEmpty(custom)) return File.Exists(custom) ? custom : null;
                return EditorBundletool;
            }
        }

        private static string editorBundletool;

        /// <summary>The bundletool that ships with the Editor's Android module, or null. Searched once per Editor session.</summary>
        public static string EditorBundletool
        {
            get
            {
                if (editorBundletool != null && File.Exists(editorBundletool)) return editorBundletool;
                string engine = PlaybackEngine;
                if (engine == null || !Directory.Exists(engine)) return null;
                string tools = Path.Combine(engine, "Tools");
                string folder = Directory.Exists(tools) ? tools : engine;
                string found = Directory.GetFiles(folder, "bundletool*.jar", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .LastOrDefault();
                editorBundletool = found == null ? null : Path.GetFullPath(found);
                return editorBundletool;
            }
        }

        private static string Tool(string root, string folder, string file)
        {
            if (string.IsNullOrEmpty(root)) return null;
            string path = Path.Combine(root, folder, file);
            return File.Exists(path) ? path : null;
        }

        /// <summary>A project-relative path made absolute; absolute paths are kept.</summary>
        public static string FullPath(string path) =>
            string.IsNullOrEmpty(path) ? path : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot, path));

        /// <summary>The path relative to the project when it is inside it, so it works on every machine.</summary>
        public static string ProjectRelative(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string root = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(path).Replace('\\', '/');
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }
    }
}
