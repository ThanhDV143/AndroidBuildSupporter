using System;
using System.IO;
using System.Linq;
using UnityEditor;

namespace ThanhDV.AndroidBuildSupporter
{
    internal enum BuildVariant
    {
        QA,
        Release
    }

    /// <summary>Placeholders of the output folder, file name and Drive folder: {product} {version} {code} {variant} {date} {time}.</summary>
    internal static class Naming
    {
        public const string Placeholders = "{product} {version} {code} {variant} {date} {time}";

        public static string Expand(string pattern, BuildVariant variant, DateTime time)
        {
            return (pattern ?? "")
                .Replace("{product}", PlayerSettings.productName)
                .Replace("{version}", PlayerSettings.bundleVersion)
                .Replace("{code}", PlayerSettings.Android.bundleVersionCode.ToString())
                .Replace("{variant}", variant == BuildVariant.QA ? "qa" : "release")
                .Replace("{date}", time.ToString("yyyyMMdd"))
                .Replace("{time}", time.ToString("HHmm"));
        }

        /// <summary>The file name without extension; characters a file name cannot have become '_'.</summary>
        public static string FileName(BuildVariant variant, DateTime time)
        {
            string name = Expand(BuildSupporterSettings.instance.fileName, variant, time);
            name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            return name.Length == 0 ? "build" : name;
        }

        public static string OutputFolder(BuildVariant variant, DateTime time)
        {
            string folder = Expand(BuildSupporterSettings.instance.outputFolder, variant, time);
            string[] segments = folder.Replace('\\', '/').Split('/')
                .Select((s, i) => i == 0 && s.EndsWith(":") ? s : new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()))
                .ToArray();
            return AndroidTools.FullPath(string.Join("/", segments));
        }

        public static string DriveFolder(BuildVariant variant, DateTime time) => Expand(BuildSupporterSettings.instance.driveFolder, variant, time);

        public static string Size(long bytes) =>
            bytes >= 1 << 20 ? $"{bytes / (1024.0 * 1024.0):0.0} MB" : $"{bytes / 1024.0:0} KB";

        public static string Duration(double seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
        }
    }
}
