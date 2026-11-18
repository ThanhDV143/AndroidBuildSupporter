using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// Preflight → build → (release) universal APK → install and upload together → Discord. Assembly reloads are held
    /// for the whole run, so a script change or a symbol change cannot cut it short.
    /// </summary>
    internal static class BuildRunner
    {
        public const string Succeeded = "Succeeded";
        public const string Warning = "Warning";
        public const string Failed = "Failed";

        private const int MaxLogLength = 4 * 1024 * 1024;

        public static bool IsRunning { get; private set; }
        public static string Status { get; private set; } = "";
        /// <summary>0–1 while a build runs.</summary>
        public static float ProgressValue { get; private set; }
        public static event Action Changed;

        public static async void Start(BuildVariant variant)
        {
            if (IsRunning || BuildPipeline.isBuildingPlayer)
            {
                EditorUtility.DisplayDialog("Android Build Supporter", "A build is already running.", "OK");
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Android Build Supporter", "Wait until scripts finish compiling and Play mode has stopped.", "OK");
                return;
            }

            IsRunning = true;
            // Held until Discord is told: the symbols put back after a release recompile the scripts, and the reload
            // that follows would stop whatever is still running.
            EditorApplication.LockReloadAssemblies();
            try
            {
                await RunAsync(variant);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetStatus("Failed: " + e.Message);
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
                IsRunning = false;
                Changed?.Invoke();
            }
        }

        private static async Task RunAsync(BuildVariant variant)
        {
            bool release = variant == BuildVariant.Release;
            string title = release ? "Release" : "QA";
            LocalSettings local = LocalSettings.instance;
            local.SyncKeystorePasswords();

            SetStatus($"{title}: checking settings…");
            List<string> errors = await Preflight.RunAsync(variant);
            if (errors.Count > 0)
            {
                string message = string.Join("\n\n", errors);
                SetStatus($"{title}: stopped before building. " + errors[0]);
                Debug.LogError("[Android Build Supporter] Build stopped:\n" + message);
                EditorUtility.DisplayDialog($"{title} build stopped", message, "OK");
                return;
            }

            BuildSupporterSettings settings = BuildSupporterSettings.instance;
            DateTime started = DateTime.Now;
            var watch = Stopwatch.StartNew();
            var record = new BuildRecord
            {
                variant = title,
                version = PlayerSettings.bundleVersion,
                code = PlayerSettings.Android.bundleVersionCode,
                folder = Naming.OutputFolder(variant, started)
            };
            string fileName = Naming.FileName(variant, started);
            string apk = Path.Combine(record.folder, fileName + ".apk");
            string aab = Path.Combine(record.folder, fileName + ".aab");
            string package = PlayerSettings.GetApplicationIdentifier(UnityCompat.Android);
            var log = new StringBuilder();
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (log.Length > MaxLogLength) return;
                log.Append('[').Append(type).Append("] ").AppendLine(message);
                if (type == LogType.Error || type == LogType.Exception) log.AppendLine(stack);
            };

            int progress = Progress.Start($"{title} build", $"{PlayerSettings.bundleVersion} ({record.code})");
            Application.logMessageReceived += capture;
            BuildReport report = null;
            try
            {
                SetStatus($"{title}: building {(release ? "AAB" : "APK")}…", progress, 0.1f);
                report = await OutsidePlayerLoop(() => Build(release, release ? aab : apk, settings));
                Application.logMessageReceived -= capture;

                if (report.summary.result != BuildResult.Succeeded)
                {
                    record.state = Failed;
                    record.error = BuildErrors(report, log);
                }
                else
                {
                    // The next build gets a new code; Google Play never accepts the same code twice.
                    PlayerSettings.Android.bundleVersionCode = record.code + 1;
                    AssetDatabase.SaveAssets();
                    record.state = Succeeded;

                    string installApk = release ? null : apk;
                    if (release)
                    {
                        AddFile(record, aab);
                        SetStatus($"{title}: making the APK from the AAB…", progress, 0.6f);
                        string apkError = await Bundletool.BuildUniversalApkAsync(aab, apk);
                        if (apkError == null) installApk = apk;
                        else record.warnings.Add(apkError);
                    }
                    if (installApk != null) AddFile(record, installApk);

                    SetStatus($"{title}: installing and uploading…", progress, 0.7f);
                    Task<List<string>> install = installApk == null || !local.installEnabled
                        ? Task.FromResult(new List<string>())
                        : Adb.InstallAsync(installApk, package, local.installTarget, record.warnings);
                    Task upload = UploadAsync(variant, record, started, progress);
                    await Task.WhenAll(install, upload);
                    record.installedOn = install.Result;
                    if (record.warnings.Count > 0) record.state = Warning;
                }
            }
            catch (Exception e)
            {
                record.state = Failed;
                record.error = (record.error == null ? "" : record.error + "\n") + e.Message;
                Debug.LogException(e);
            }
            finally
            {
                Application.logMessageReceived -= capture;
            }

            record.seconds = watch.Elapsed.TotalSeconds;
            record.finishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            local.lastBuild = record;
            local.Save();

            SetStatus($"{title}: sending to Discord…", progress, 0.95f);
            string discordError = await Discord.NotifyAsync(record, report, record.state == Failed ? log.ToString() : null);
            if (discordError != null) Debug.LogWarning("[Android Build Supporter] Discord: " + discordError);

            Progress.Finish(progress, record.state == Failed ? Progress.Status.Failed : Progress.Status.Succeeded);
            string summary = record.state == Failed
                ? $"{title} failed after {Naming.Duration(record.seconds)}: {FirstLine(record.error)}"
                : $"{title} {record.version} ({record.code}) built in {Naming.Duration(record.seconds)}" +
                  (record.state == Warning ? $" with {record.warnings.Count} warning(s)" : "");
            SetStatus(summary);
            if (record.state == Failed) Debug.LogError("[Android Build Supporter] " + record.error);
            foreach (string warning in record.warnings) Debug.LogWarning("[Android Build Supporter] " + warning);
            if (record.installedOn.Count > 0) Debug.Log("[Android Build Supporter] Installed on " + string.Join(", ", record.installedOn));
        }

        /// <summary>
        /// Code after an await runs inside the player loop, where Unity 6 refuses to start a player build; delayCall
        /// runs between Editor updates instead.
        /// </summary>
        private static Task<T> OutsidePlayerLoop<T>(Func<T> action)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.delayCall += () =>
            {
                try
                {
                    result.SetResult(action());
                }
                catch (Exception e)
                {
                    result.SetException(e);
                }
            };
            return result.Task;
        }

        private static BuildReport Build(bool release, string path, BuildSupporterSettings settings)
        {
            IReadOnlyCollection<string> remove = release ? settings.symbolsToRemove.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() : new List<string>();
            using (new BuildScope(remove, release))
            {
                ForbiddenAssemblyGuard.Active = release ? settings.forbiddenAssemblies.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() : null;
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    var options = new BuildPlayerOptions
                    {
                        scenes = UnityCompat.EnabledScenes,
                        locationPathName = path,
                        target = BuildTarget.Android,
                        targetGroup = BuildTargetGroup.Android,
                        options = UnityCompat.ToBuildOptions(UnityCompat.Compression)
                    };
                    BuildReport report = BuildPipeline.BuildPlayer(options);
                    if (report.summary.result == BuildResult.Succeeded && !File.Exists(path))
                        throw new InvalidOperationException("Unity reported success but did not write " + path);
                    return report;
                }
                finally
                {
                    ForbiddenAssemblyGuard.Active = null;
                }
            }
        }

        private static async Task UploadAsync(BuildVariant variant, BuildRecord record, DateTime started, int progress)
        {
            BuildSupporterSettings settings = BuildSupporterSettings.instance;
            var files = record.files.Where(f =>
                variant == BuildVariant.QA ? settings.uploadQaApk :
                f.path.EndsWith(".aab", StringComparison.OrdinalIgnoreCase) ? settings.uploadReleaseAab : settings.uploadReleaseApk).ToList();
            if (!LocalSettings.instance.uploadEnabled || files.Count == 0) return;

            if (!GoogleAuth.HasRequiredScope)
            {
                record.warnings.Add(GoogleAuth.IsSignedIn
                    ? "Upload skipped: the project folder needs full Drive access. Press Sign in again in the Distribution tab."
                    : "Upload skipped: sign in to Google Drive in the Distribution tab.");
                return;
            }

            try
            {
                string folder = await GoogleDrive.EnsureFolderAsync(Naming.DriveFolder(variant, started));
                for (int i = 0; i < files.Count; i++)
                {
                    BuildFile file = files[i];
                    int index = i;
                    var (link, shareWarning) = await GoogleDrive.UploadAsync(file.path, folder, p =>
                        SetStatus($"Uploading {file.name} {p:P0}…", progress, 0.7f + 0.25f * (index + p) / files.Count));
                    file.link = link;
                    if (shareWarning != null) record.warnings.Add(shareWarning);
                }
            }
            catch (Exception e)
            {
                record.warnings.Add("Upload failed: " + e.Message);
            }
        }

        private static void AddFile(BuildRecord record, string path)
        {
            if (!File.Exists(path)) return;
            record.files.Add(new BuildFile { name = Path.GetFileName(path), path = path, size = new FileInfo(path).Length });
        }

        private static string BuildErrors(BuildReport report, StringBuilder log)
        {
            string[] messages = report.steps
                .SelectMany(step => step.messages)
                .Where(m => m.type == LogType.Error || m.type == LogType.Exception)
                .Select(m => m.content.Trim())
                .Distinct()
                // An exception from a build callback says why; "Building player scripts failed" only says that it did.
                .OrderBy(m => m.Contains("Exception") ? 0 : 1)
                .Take(5)
                .ToArray();
            if (messages.Length > 0) return string.Join("\n", messages);

            string fromLog = log.ToString().Split('\n').FirstOrDefault(l => l.StartsWith("[Error]") || l.StartsWith("[Exception]"));
            return fromLog ?? $"Build {report.summary.result} with {report.summary.totalErrors} error(s).";
        }

        private static string FirstLine(string text) => (text ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";

        private static void SetStatus(string status, int progress = -1, float value = 0)
        {
            Status = status;
            ProgressValue = value;
            if (progress >= 0) Progress.Report(progress, value, status);
            Changed?.Invoke();
        }
    }
}
