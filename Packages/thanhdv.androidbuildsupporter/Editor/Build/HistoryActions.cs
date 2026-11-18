using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// What can be done again with a build from the history. These are explicit requests, so they ignore the main
    /// switches; one runs at a time and none while a build runs.
    /// </summary>
    internal static class HistoryActions
    {
        public static bool IsBusy { get; private set; }

        public static bool CanRun => !IsBusy && !BuildRunner.IsRunning;

        /// <summary>Files on disk without a Drive link, which Upload again sends.</summary>
        public static List<BuildFile> NotUploaded(BuildRecord record) => record.files.Where(f => f.Exists && string.IsNullOrEmpty(f.link)).ToList();

        /// <returns>What happened, and whether it worked.</returns>
        public static Task<(string message, bool ok)> InstallAsync(BuildRecord record) => Run(async () =>
        {
            BuildFile apk = record.Apk;
            if (apk == null) return ("The APK of this build is no longer on disk.", false);

            var warnings = new List<string>();
            string package = string.IsNullOrEmpty(record.package) ? UnityEditor.PlayerSettings.GetApplicationIdentifier(UnityCompat.Android) : record.package;
            List<string> installed = await Adb.InstallAsync(apk.path, package, LocalSettings.instance.installTarget, warnings);
            if (installed.Count == 0) return (warnings.FirstOrDefault() ?? "Not installed.", false);
            return ($"Installed on {string.Join(", ", installed)}." + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : ""), warnings.Count == 0);
        });

        public static Task<(string message, bool ok)> UploadAsync(BuildRecord record) => Run(async () =>
        {
            List<BuildFile> files = NotUploaded(record);
            if (files.Count == 0) return ("Every file of this build is already on Drive.", true);

            string error = await BuildRunner.UploadFilesAsync(record, files, null);
            if (error == null)
            {
                // The upload problems the build reported are solved now.
                record.warnings.RemoveAll(w => w.StartsWith("Upload", StringComparison.Ordinal));
                if (record.state == BuildRunner.Warning && record.warnings.Count == 0) record.state = BuildRunner.Succeeded;
            }
            BuildHistory.instance.Commit();
            return error == null ? ($"Uploaded {string.Join(", ", files.Select(f => f.name))}.", true) : (error, false);
        });

        public static Task<(string message, bool ok)> NotifyAsync(BuildRecord record) => Run(async () =>
        {
            string error = await Discord.NotifyAsync(record, resend: true);
            return error == null ? ("Sent to Discord.", true) : (error, false);
        });

        private static async Task<(string message, bool ok)> Run(Func<Task<(string, bool)>> action)
        {
            if (!CanRun) return ("Wait for the running build or action to finish.", false);
            IsBusy = true;
            try
            {
                return await action();
            }
            catch (Exception e)
            {
                return (e.Message, false);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
