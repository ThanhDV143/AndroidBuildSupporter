using System;
using System.Collections.Generic;
using System.IO;
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
            public string timestamp;
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

        /// <summary>
        /// Posts the build's result. After a build it follows the Notify switch and Notify on; sent again from the
        /// history (<paramref name="resend"/>) it only needs the webhook.
        /// </summary>
        /// <returns>Null when sent or not needed, otherwise why it failed.</returns>
        public static async Task<string> NotifyAsync(BuildRecord record, bool resend = false)
        {
            BuildSupporterSettings settings = BuildSupporterSettings.instance;
            bool failed = record.state == BuildRunner.Failed;
            if (string.IsNullOrWhiteSpace(LocalSettings.instance.discordWebhook))
                return resend ? "Enter a Discord webhook URL in the Distribution tab." : null;
            if (!resend && (!LocalSettings.instance.notifyEnabled || (failed ? !settings.notifyFailure : !settings.notifySuccess))) return null;

            var payload = new Payload
            {
                embeds = new[]
                {
                    new Embed
                    {
                        title = Limit(Title(record), 256),
                        description = Limit(Description(record, resend), 4000),
                        color = failed ? Red : record.state == BuildRunner.Warning ? Amber : Green,
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
            return await SendAsync(payload, failed && record.HasLog ? File.ReadAllText(record.logPath) : null);
        }

        /// <summary>"✅ Success: My Game QA 1.2.3 (1020300)", in the format of the package publishing workflow.</summary>
        public static string Title(BuildRecord record)
        {
            string status = record.state == BuildRunner.Failed ? "❌ Failure" : record.state == BuildRunner.Warning ? "⚠️ Warning" : "✅ Success";
            return $"{status}: {PlayerSettings.productName} {record.variant} {record.version} ({record.code})";
        }

        /// <summary>One bold-labelled line per detail, like the workflow's messages; empty details are left out.</summary>
        public static string Description(BuildRecord record, bool resend)
        {
            bool failed = record.state == BuildRunner.Failed;
            string package = string.IsNullOrEmpty(record.package) ? PlayerSettings.GetApplicationIdentifier(UnityCompat.Android) : record.package;
            var text = new StringBuilder();
            text.AppendLine(failed
                ? $"{record.variant} build of `{package}` failed after {Naming.Duration(record.seconds)}."
                : $"{record.variant} build of `{package}` finished in {Naming.Duration(record.seconds)}.");
            if (failed) text.AppendLine($"**Reason:** {Reason(record.error)}");
            if (record.files.Count == 1) text.AppendLine($"**File:** {FileLine(record.files[0])}");
            else if (record.files.Count > 1)
            {
                text.AppendLine("**Files:**");
                foreach (BuildFile file in record.files) text.AppendLine(FileLine(file));
            }
            if (record.installedOn.Count > 0) text.AppendLine($"**Installed on:** {string.Join(", ", record.installedOn)}");
            if (record.warnings.Count == 1) text.AppendLine($"**Warning:** {record.warnings[0]}");
            else if (record.warnings.Count > 1)
            {
                text.AppendLine("**Warnings:**");
                foreach (string warning in record.warnings) text.AppendLine("• " + warning);
            }
            if (failed && record.HasLog) text.AppendLine("**Build Log:** attached");
            if (resend) text.AppendLine($"*Sent again from the history; built {record.finishedAt}.*");
            return text.ToString().TrimEnd();
        }

        private static string FileLine(BuildFile file) =>
            (string.IsNullOrEmpty(file.link) ? file.name : $"[{file.name}]({file.link})") + " · " + Naming.Size(file.size);

        /// <summary>The first line of an error, joined with the next when it only introduces it ("… failed with output:").</summary>
        public static string Reason(string text)
        {
            string[] lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            if (lines.Length == 0) return "Unknown error.";
            return lines[0].EndsWith(":", StringComparison.Ordinal) && lines.Length > 1 ? lines[0] + " " + lines[1] : lines[0];
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

        /// <summary>The five longest steps of the build, for the message and the history.</summary>
        public static string SlowestSteps(BuildReport report)
        {
            if (report == null || report.steps.Length == 0) return null;
            // The first level under the whole build, where the time actually splits.
            int depth = report.steps.Any(s => s.depth == 1) ? 1 : 0;
            var steps = report.steps.Where(s => s.depth == depth).OrderByDescending(s => s.duration).Take(5).ToArray();
            if (steps.Length == 0) return null;
            return Limit(string.Join("\n", steps.Select(s => $"{Naming.Duration(s.duration.TotalSeconds)} · {s.name}")), 1024);
        }

        private static string Now => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        private static string Limit(string text, int max) => string.IsNullOrEmpty(text) ? "—" : text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }
}
