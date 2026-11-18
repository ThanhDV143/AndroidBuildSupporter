using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Networking;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>Build reports through a Discord webhook; a failed build attaches its log.</summary>
    internal static class Discord
    {
        private const int Green = 0x2ECC71;
        private const int Amber = 0xF1C40F;
        private const int Red = 0xE74C3C;

        [Serializable]
        private sealed class Payload
        {
            public string username = "Android Build Supporter";
            public string content = "";
            public Embed[] embeds;
            public AllowedMentions allowed_mentions = new AllowedMentions();
        }

        [Serializable]
        private sealed class Embed
        {
            public string title;
            public string description;
            public int color;
            public Field[] fields;
            public string timestamp;
        }

        [Serializable]
        private sealed class Field
        {
            public string name;
            public string value;
            public bool inline;
        }

        [Serializable]
        private sealed class AllowedMentions
        {
            public string[] parse = new string[0];
            public string[] users = new string[0];
        }

        /// <summary>Only Discord webhook addresses, so a mistyped value cannot send build details anywhere else.</summary>
        public static bool IsWebhook(string url) =>
            Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps &&
            (uri.Host == "discord.com" || uri.Host == "discordapp.com" || uri.Host.EndsWith(".discord.com", StringComparison.Ordinal)) &&
            uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);

        /// <summary>Asks Discord whether the webhook exists, without posting anything to the channel.</summary>
        /// <returns>Null when it does, otherwise why not.</returns>
        public static async Task<string> CheckAsync()
        {
            string url = LocalSettings.instance.discordWebhook?.Trim();
            if (string.IsNullOrEmpty(url)) return "Enter a Discord webhook URL in the Distribution tab.";
            if (!IsWebhook(url)) return "The Discord webhook URL in the Distribution tab is not a webhook URL.";
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                try
                {
                    await Http.SendAsync(request);
                    return null;
                }
                catch (HttpException e)
                {
                    return e.Code == 401 || e.Code == 404
                        ? "Discord does not know this webhook: it was deleted or the URL is wrong."
                        : "Could not reach Discord: " + e.Message;
                }
            }
        }

        public static Task<string> TestAsync() => SendAsync(new Payload
        {
            embeds = new[] { new Embed { title = "Android Build Supporter is connected", description = PlayerSettings.productName, color = Green, timestamp = Now } }
        }, null);

        /// <returns>Null when sent or not needed, otherwise why it failed.</returns>
        public static async Task<string> NotifyAsync(BuildRecord record, BuildReport report, string failureLog)
        {
            BuildSupporterSettings settings = BuildSupporterSettings.instance;
            bool failed = record.state == BuildRunner.Failed;
            if (!LocalSettings.instance.notifyEnabled || string.IsNullOrWhiteSpace(LocalSettings.instance.discordWebhook) ||
                (failed ? !settings.notifyFailure : !settings.notifySuccess)) return null;

            var fields = new List<Field>
            {
                new Field { name = "Version", value = $"{record.version} ({record.code})", inline = true },
                new Field { name = "Package", value = PlayerSettings.GetApplicationIdentifier(UnityCompat.Android), inline = true },
                new Field { name = "Duration", value = Naming.Duration(record.seconds), inline = true }
            };
            string commit = await CommitAsync();
            if (commit != null) fields.Add(new Field { name = "Commit", value = commit, inline = true });
            if (record.files.Count > 0)
                fields.Add(new Field
                {
                    name = "Files",
                    value = Limit(string.Join("\n", record.files.Select(f => (f.link != null ? $"[{f.name}]({f.link})" : f.name) + " · " + Naming.Size(f.size))), 1024)
                });
            if (record.installedOn.Count > 0) fields.Add(new Field { name = "Installed on", value = Limit(string.Join(", ", record.installedOn), 1024) });
            if (record.warnings.Count > 0) fields.Add(new Field { name = "Warnings", value = Limit(string.Join("\n", record.warnings.Select(w => "• " + w)), 1024) });
            string steps = SlowestSteps(report);
            if (steps != null) fields.Add(new Field { name = "Slowest steps", value = steps });

            string title = $"{record.variant} build " + (failed ? "failed" : record.state == BuildRunner.Warning ? "finished with warnings" : "succeeded");
            var payload = new Payload
            {
                embeds = new[]
                {
                    new Embed
                    {
                        title = Limit($"{title} · {PlayerSettings.productName}", 256),
                        description = failed ? Limit("```\n" + record.error + "\n```", 4000) :
                            record.state == BuildRunner.Warning ? "Built, but some steps need attention." : "Ready to test.",
                        color = failed ? Red : record.state == BuildRunner.Warning ? Amber : Green,
                        fields = fields.ToArray(),
                        timestamp = Now
                    }
                }
            };

            string[] users = MentionIds(settings.mentionUserId).Valid;
            if (users.Length > 0)
            {
                payload.content = string.Join(" ", users.Select(u => $"<@{u}>"));
                payload.allowed_mentions.users = users;
            }
            return await SendAsync(payload, failed ? failureLog : null);
        }

        /// <summary>Discord lets one message mention at most 100 users.</summary>
        private const int MaxMentions = 100;

        /// <summary>
        /// The user IDs of the Mention setting, separated by commas: the valid ones (digits only, at most
        /// <see cref="MaxMentions"/>, no repeats) and the entries that are not IDs.
        /// </summary>
        public static (string[] Valid, string[] Invalid) MentionIds(string setting)
        {
            string[] entries = (setting ?? "").Split(',').Select(e => e.Trim()).Where(e => e.Length > 0).ToArray();
            string[] valid = entries.Where(e => e.All(char.IsDigit)).Distinct().Take(MaxMentions).ToArray();
            string[] invalid = entries.Where(e => !e.All(char.IsDigit)).ToArray();
            return (valid, invalid);
        }

        private static async Task<string> SendAsync(Payload payload, string attachment)
        {
            string url = LocalSettings.instance.discordWebhook?.Trim();
            if (!IsWebhook(url)) return "The webhook URL is not a Discord webhook.";

            string json = JsonUtility.ToJson(payload);
            UnityWebRequest request;
            if (string.IsNullOrEmpty(attachment))
            {
                request = Http.Json(url, "POST", json);
            }
            else
            {
                request = UnityWebRequest.Post(url, new List<IMultipartFormSection>
                {
                    new MultipartFormDataSection("payload_json", Encoding.UTF8.GetBytes(json), "application/json"),
                    new MultipartFormFileSection("files[0]", Encoding.UTF8.GetBytes(attachment), "build-log.txt", "text/plain")
                });
            }

            using (request)
            {
                try
                {
                    await Http.SendAsync(request);
                    return null;
                }
                catch (HttpException e)
                {
                    return e.Message;
                }
            }
        }

        private static string SlowestSteps(BuildReport report)
        {
            if (report == null || report.steps.Length == 0) return null;
            // The first level under the whole build, where the time actually splits.
            int depth = report.steps.Any(s => s.depth == 1) ? 1 : 0;
            var steps = report.steps.Where(s => s.depth == depth).OrderByDescending(s => s.duration).Take(5).ToArray();
            if (steps.Length == 0) return null;
            return Limit(string.Join("\n", steps.Select(s => $"{Naming.Duration(s.duration.TotalSeconds)} · {s.name}")), 1024);
        }

        /// <summary>"abc1234 on main", plus "(uncommitted changes)"; null outside a git repository.</summary>
        private static async Task<string> CommitAsync()
        {
            string root = AndroidTools.ProjectRoot;
            ProcessResult head = await ProcessRunner.RunAsync("git", new[] { "rev-parse", "--short", "HEAD" }, timeoutMs: 10000, workingDirectory: root);
            if (!head.Succeeded) return null;
            ProcessResult branch = await ProcessRunner.RunAsync("git", new[] { "rev-parse", "--abbrev-ref", "HEAD" }, timeoutMs: 10000, workingDirectory: root);
            ProcessResult status = await ProcessRunner.RunAsync("git", new[] { "status", "--porcelain" }, timeoutMs: 10000, workingDirectory: root);
            string text = head.Output.Trim();
            if (branch.Succeeded) text += " on " + branch.Output.Trim();
            if (status.Succeeded && status.Output.Trim().Length > 0) text += " (uncommitted changes)";
            return text;
        }

        private static string Now => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        private static string Limit(string text, int max) => string.IsNullOrEmpty(text) ? "—" : text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }
}
