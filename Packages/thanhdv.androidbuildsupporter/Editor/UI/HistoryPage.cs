using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// The History tab: the builds made on this machine, newest first, filtered by variant, failures and version.
    /// A row opens to show its files and what can be done again with them.
    /// </summary>
    internal sealed class HistoryPage
    {
        private static readonly List<string> Variants = new List<string> { "All builds", "QA", "Release" };

        private VisualElement list;
        private string variant = Variants[0];
        private bool failedOnly;
        private string search = "";
        private string expandedId;

        /// <summary>What the list shows; it is only rebuilt when this changes.</summary>
        private string shown;
        private bool busyShown;

        private string actionMessage;
        private bool actionOk;
        private string actionRecordId;
        private int actionMessageId;

        public void Build(VisualElement page, Form form)
        {
            var filters = new VisualElement();
            filters.AddToClassList("abs-history-filters");
            var variantField = new PopupField<string>(Variants, 0);
            variantField.AddToClassList("abs-history-variant");
            variantField.RegisterValueChangedCallback(e =>
            {
                variant = e.newValue;
                Rebuild();
            });
            var failed = new Toggle { text = "Failed only" };
            failed.AddToClassList("abs-check");
            failed.RegisterValueChangedCallback(e =>
            {
                failedOnly = e.newValue;
                Rebuild();
            });
            var searchField = new TextField { tooltip = "Filter by version, for example 1.2" };
            searchField.AddToClassList("abs-control");
            searchField.RegisterValueChangedCallback(e =>
            {
                search = (e.newValue ?? "").Trim();
                Rebuild();
            });
            var searchLabel = new Label("Version");
            searchLabel.AddToClassList("abs-history-search-label");
            filters.Add(variantField);
            filters.Add(failed);
            filters.Add(searchLabel);
            filters.Add(searchField);
            page.Add(filters);

            list = new VisualElement();
            page.Add(list);
            form.OnRefresh(Refresh);
        }

        /// <summary>Opens a build, for example the last one from the footer.</summary>
        public void Expand(string id)
        {
            expandedId = id;
            Rebuild();
        }

        public void Refresh()
        {
            string key = $"{BuildHistory.Revision}|{variant}|{failedOnly}|{search}|{expandedId}|{actionMessageId}";
            bool busy = !HistoryActions.CanRun;
            if (key == shown && busy == busyShown) return;
            shown = key;
            busyShown = busy;
            Draw();
        }

        private void Rebuild()
        {
            shown = null;
            Refresh();
        }

        private void Draw()
        {
            list.Clear();
            List<BuildRecord> all = BuildHistory.instance.records;
            if (BuildHistory.instance.Latest == null && all.Count == 0)
            {
                list.Add(Empty("No builds yet. Builds made with this window on this machine show up here."));
                return;
            }

            List<BuildRecord> records = all.Where(r =>
                (variant == Variants[0] || r.variant == variant) &&
                (!failedOnly || r.state == BuildRunner.Failed) &&
                (search.Length == 0 || (r.version ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (records.Count == 0)
            {
                list.Add(Empty("No build matches these filters."));
                return;
            }

            foreach (BuildRecord record in records) list.Add(Row(record));
            var count = new Label($"{records.Count} of {all.Count} builds · the last {BuildHistory.MaxRecords} are kept, on this machine only");
            count.AddToClassList("abs-history-count");
            list.Add(count);
        }

        private static Label Empty(string text)
        {
            var label = new Label(text);
            label.AddToClassList("abs-history-empty");
            return label;
        }

        private VisualElement Row(BuildRecord record)
        {
            bool expanded = record.id == expandedId;
            var row = new VisualElement();
            row.AddToClassList("abs-history-row");
            row.EnableInClassList("abs-history-row-open", expanded);

            var header = new VisualElement();
            header.AddToClassList("abs-history-header");
            header.Add(StateChip(record.state));
            var titles = new VisualElement();
            titles.AddToClassList("abs-history-titles");
            var title = new Label($"{record.variant}  {record.version} ({record.code})");
            title.AddToClassList("abs-history-title");
            var meta = new Label(string.Join("  ·  ", new[]
            {
                record.finishedAt, Naming.Duration(record.seconds)
            }.Where(s => !string.IsNullOrEmpty(s))));
            meta.AddToClassList("abs-history-meta");
            titles.Add(title);
            titles.Add(meta);
            header.Add(titles);
            var arrow = new Label(expanded ? "▾" : "▸");
            arrow.AddToClassList("abs-history-arrow");
            header.Add(arrow);
            header.AddManipulator(new Clickable(() =>
            {
                expandedId = expanded ? null : record.id;
                Rebuild();
            }));
            row.Add(header);

            if (expanded) row.Add(Details(record));
            return row;
        }

        private static Label StateChip(string state)
        {
            var chip = new Label(state == BuildRunner.Failed ? "Failed" : state == BuildRunner.Warning ? "Warnings" : "OK");
            chip.AddToClassList("abs-state");
            chip.AddToClassList(state == BuildRunner.Failed ? "abs-state-error" : state == BuildRunner.Warning ? "abs-state-warning" : "abs-state-ok");
            return chip;
        }

        private VisualElement Details(BuildRecord record)
        {
            var details = new VisualElement();
            details.AddToClassList("abs-history-details");

            if (!string.IsNullOrEmpty(record.error)) details.Add(Note(record.error, "error"));
            foreach (string warning in record.warnings) details.Add(Note(warning, "warning"));

            if (record.files.Count > 0)
            {
                VisualElement files = Form.Row(details, "Files");
                files.AddToClassList("abs-history-files");
                foreach (BuildFile file in record.files) files.Add(FileRow(file));
            }
            Info(details, "Package", record.package);
            Info(details, "Started", record.startedAt);
            Info(details, "Folder", string.IsNullOrEmpty(record.folder) ? null : AndroidTools.ProjectRelative(record.folder));
            Info(details, "Drive folder", record.driveFolder);
            Info(details, "Installed on", record.installedOn.Count > 0 ? string.Join(", ", record.installedOn) : null);
            if (!string.IsNullOrEmpty(record.slowestSteps)) Info(details, "Slowest steps", record.slowestSteps);

            var actions = new VisualElement();
            actions.AddToClassList("abs-history-actions");
            bool canRun = HistoryActions.CanRun;
            AddAction(actions, "Install again", canRun && record.Apk != null && AndroidTools.Adb != null,
                record.Apk == null ? "The APK is no longer on disk" : "Install the APK on the device chosen in the Build tab and open the app",
                () => RunAsync(record, HistoryActions.InstallAsync));
            AddAction(actions, "Upload again", canRun && HistoryActions.NotUploaded(record).Count > 0,
                "Upload the files that have no Drive link yet", () => RunAsync(record, HistoryActions.UploadAsync));
            AddAction(actions, "Send to Discord", canRun && Discord.IsWebhook(LocalSettings.instance.discordWebhook),
                "Post this build's result to Discord again", () => RunAsync(record, HistoryActions.NotifyAsync));
            AddAction(actions, "Copy links", record.files.Any(f => !string.IsNullOrEmpty(f.link)),
                "Copy the Drive links as [file name](link)",
                () => EditorGUIUtility.systemCopyBuffer = string.Join("\n", record.files.Where(f => !string.IsNullOrEmpty(f.link)).Select(f => $"[{f.name}]({f.link})")));
            if (record.HasLog) AddAction(actions, "Open log", true, "Open the log of this failed build", () => EditorUtility.OpenWithDefaultApp(record.logPath));
            AddAction(actions, "Remove", canRun, "Remove from the history, and optionally delete the files", () => Remove(record));
            details.Add(actions);

            if (actionRecordId == record.id && actionMessage != null)
                details.Add(Note(actionMessage, actionMessage.EndsWith("…", StringComparison.Ordinal) ? "info" : actionOk ? "ok" : "warning"));
            return details;
        }

        private static VisualElement FileRow(BuildFile file)
        {
            var row = new VisualElement();
            row.AddToClassList("abs-inline");
            row.AddToClassList("abs-history-file");
            bool exists = file.Exists;
            var name = new Label(file.name + "  ·  " + Naming.Size(file.size) + (exists ? "" : "  ·  missing"));
            name.AddToClassList("abs-history-file-name");
            name.EnableInClassList("abs-history-missing", !exists);
            name.tooltip = file.path;
            row.Add(name);
            Button show = Form.Button(row, "Show", () => EditorUtility.RevealInFinder(file.path), "Show the file in the file browser");
            show.SetEnabled(exists);
            if (!string.IsNullOrEmpty(file.link))
            {
                Form.Button(row, "Open", () => Application.OpenURL(file.link), "Open the Drive link");
                Form.Button(row, "Copy link", () => EditorGUIUtility.systemCopyBuffer = $"[{file.name}]({file.link})", "Copy as [file name](link)");
            }
            return row;
        }

        private static void Info(VisualElement parent, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            VisualElement row = Form.Row(parent, label);
            var text = new Label(value);
            text.AddToClassList("abs-history-value");
            row.Add(text);
        }

        private static Label Note(string text, string kind)
        {
            var note = new Label(text);
            note.AddToClassList("abs-notice");
            note.AddToClassList("abs-notice-" + kind);
            return note;
        }

        private static void AddAction(VisualElement parent, string text, bool enabled, string tooltip, Action click)
        {
            Button button = Form.Button(parent, text, click, tooltip);
            button.SetEnabled(enabled);
        }

        private async void RunAsync(BuildRecord record, Func<BuildRecord, Task<(string message, bool ok)>> action)
        {
            ShowMessage(record, "Working…", true);
            var (message, ok) = await action(record);
            ShowMessage(record, message, ok);
            Form.Log($"{record.variant} {record.version} ({record.code}): {message}", ok ? "ok" : "warning");
            // Results go away by themselves, like the other one-off messages of the window.
            int id = actionMessageId;
            list.schedule.Execute(() =>
            {
                if (id != actionMessageId) return;
                actionMessage = null;
                actionMessageId++;
                Refresh();
            }).StartingIn(Form.NoticeMs);
        }

        private void ShowMessage(BuildRecord record, string message, bool ok)
        {
            actionRecordId = record.id;
            actionMessage = message;
            actionOk = ok;
            actionMessageId++;
            Refresh();
        }

        private void Remove(BuildRecord record)
        {
            List<BuildFile> onDisk = record.files.Where(f => f.Exists).ToList();
            string name = $"{record.variant} {record.version} ({record.code})";
            if (onDisk.Count == 0)
            {
                if (EditorUtility.DisplayDialog("Remove build", $"Remove {name} from the history?", "Remove", "Cancel"))
                    BuildHistory.instance.Remove(record, false);
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex("Remove build",
                $"Remove {name} from the history?\n\nIts files on disk:\n{string.Join("\n", onDisk.Select(f => f.name))}\n\nDeleted files cannot be recovered.",
                "Remove, keep files", "Cancel", "Remove and delete files");
            if (choice == 1) return;
            try
            {
                BuildHistory.instance.Remove(record, choice == 2);
            }
            catch (IOException e)
            {
                EditorUtility.DisplayDialog("Remove build", "Could not delete the files: " + e.Message, "OK");
            }
        }
    }
}
