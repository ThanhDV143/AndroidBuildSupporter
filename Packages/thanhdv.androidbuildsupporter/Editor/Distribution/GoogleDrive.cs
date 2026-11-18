using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// Drive API v3 over plain HTTPS: folders, resumable upload streamed from disk, and an "anyone with the link" share.
    /// Builds go under My Drive, or under a folder someone shared (Root folder), also on a shared drive.
    /// </summary>
    internal static class GoogleDrive
    {
        private const string FilesUrl = "https://www.googleapis.com/drive/v3/files";
        private const string UploadUrl = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id&supportsAllDrives=true";
        private const string FolderType = "application/vnd.google-apps.folder";
        private const int UploadAttempts = 3;

        [Serializable]
        private sealed class FileList
        {
            public DriveFile[] files;
        }

        [Serializable]
        private sealed class DriveFile
        {
            public string id;
            public string name;
            public string mimeType;
            public bool trashed;
            public Capabilities capabilities;
        }

        [Serializable]
        private sealed class Capabilities
        {
            public bool canAddChildren;
        }

        [Serializable]
        private sealed class Metadata
        {
            public string name;
            public string mimeType;
            public string[] parents;
        }

        /// <summary>JsonUtility writes every field, and an empty mimeType is not the same as none.</summary>
        [Serializable]
        private sealed class FileMetadata
        {
            public string name;
            public string[] parents;
        }

        [Serializable]
        private sealed class Permission
        {
            public string role = "reader";
            public string type = "anyone";
        }

        [Serializable]
        private sealed class About
        {
            public DriveUser user;
        }

        [Serializable]
        private sealed class DriveUser
        {
            public string displayName;
            public string emailAddress;
        }

        // --- Root folder -----------------------------------------------------------------------------------------

        private static readonly Regex FolderLink = new Regex(@"/folders/([A-Za-z0-9_-]{10,})|[?&]id=([A-Za-z0-9_-]{10,})");
        private static readonly Regex FolderIdPattern = new Regex(@"^[A-Za-z0-9_-]{10,}$");

        /// <summary>
        /// The id of the Root folder setting, which takes a folder link or a bare id; null when it is empty (My Drive)
        /// or not a folder link.
        /// </summary>
        public static string RootFolderId => ParseFolderId(BuildSupporterSettings.instance.driveRootFolder);

        public static bool UsesSharedFolder => !string.IsNullOrWhiteSpace(BuildSupporterSettings.instance.driveRootFolder);

        public static string ParseFolderId(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return null;
            Match link = FolderLink.Match(value);
            if (link.Success) return link.Groups[1].Success ? link.Groups[1].Value : link.Groups[2].Value;
            return FolderIdPattern.IsMatch(value) ? value : null;
        }

        /// <summary>Opens the Root folder and checks this account may add files to it.</summary>
        /// <returns>The folder's name, or an error.</returns>
        public static async Task<(string name, string error)> CheckRootAsync()
        {
            if (!UsesSharedFolder) return ("My Drive", null);
            string id = RootFolderId;
            if (id == null) return (null, "Paste the link of a Google Drive folder, or its id.");

            string token = await GoogleAuth.GetAccessTokenAsync();
            using (UnityWebRequest request = Http.Get($"{FilesUrl}/{id}?fields=id,name,mimeType,trashed,capabilities(canAddChildren)&supportsAllDrives=true", token))
            {
                try
                {
                    await Http.SendAsync(request);
                }
                catch (HttpException e) when (e.Code == 404 || e.Code == 403)
                {
                    return (null, $"This account ({LocalSettings.instance.googleAccount}) cannot open that folder. Ask the folder's owner to share it with this account.");
                }
                DriveFile folder = JsonUtility.FromJson<DriveFile>(request.downloadHandler.text);
                if (folder.mimeType != FolderType) return (null, $"'{folder.name}' is a file, not a folder.");
                if (folder.trashed) return (null, $"'{folder.name}' is in the trash.");
                if (folder.capabilities == null || !folder.capabilities.canAddChildren)
                    return (null, $"This account can only view '{folder.name}'. Ask its owner for Editor access.");
                return (folder.name, null);
            }
        }

        // --- Account ---------------------------------------------------------------------------------------------

        /// <summary>"Name · email" of the signed-in account.</summary>
        public static async Task<string> AccountAsync()
        {
            string token = await GoogleAuth.GetAccessTokenAsync();
            using (UnityWebRequest request = Http.Get("https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)", token))
            {
                await Http.SendAsync(request);
                DriveUser user = JsonUtility.FromJson<About>(request.downloadHandler.text)?.user;
                if (user == null) return "";
                if (string.IsNullOrEmpty(user.displayName)) return user.emailAddress ?? "";
                return string.IsNullOrEmpty(user.emailAddress) ? user.displayName : $"{user.displayName} · {user.emailAddress}";
            }
        }

        // --- Folders and uploads ---------------------------------------------------------------------------------

        /// <summary>Folder path → id, so each build does not look the same folders up again.</summary>
        private static readonly Dictionary<string, string> folders = new Dictionary<string, string>();

        public static void ClearCache() => folders.Clear();

        /// <summary>
        /// Finds or creates every folder of "a/b/c" under the Root folder (My Drive when it is empty) and returns the
        /// id of the last one.
        /// </summary>
        public static async Task<string> EnsureFolderAsync(string path)
        {
            string root = UsesSharedFolder ? RootFolderId ?? throw new InvalidOperationException("The Root folder is not a Google Drive folder link.") : "root";
            string parent = root;
            string walked = root;
            foreach (string raw in path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim();
                if (name.Length == 0) continue;
                walked += "/" + name;
                if (folders.TryGetValue(walked, out string cached))
                {
                    parent = cached;
                    continue;
                }

                string token = await GoogleAuth.GetAccessTokenAsync();
                string query = $"name = '{Escape(name)}' and mimeType = '{FolderType}' and '{parent}' in parents and trashed = false";
                string id;
                using (UnityWebRequest find = Http.Get($"{FilesUrl}?q={Uri.EscapeDataString(query)}&fields=files(id)" +
                                                       "&supportsAllDrives=true&includeItemsFromAllDrives=true", token))
                {
                    await Http.SendAsync(find);
                    FileList list = JsonUtility.FromJson<FileList>(find.downloadHandler.text);
                    id = list.files != null && list.files.Length > 0 ? list.files[0].id : null;
                }

                if (id == null)
                {
                    string body = JsonUtility.ToJson(new Metadata { name = name, mimeType = FolderType, parents = new[] { parent } });
                    using (UnityWebRequest create = Http.Json(FilesUrl + "?fields=id&supportsAllDrives=true", "POST", body, token))
                    {
                        await Http.SendAsync(create);
                        id = JsonUtility.FromJson<DriveFile>(create.downloadHandler.text).id;
                    }
                }
                folders[walked] = id;
                parent = id;
            }
            return parent;
        }

        /// <summary>
        /// Uploads the file into the folder and shares it with anyone who has the link. Some shared drives forbid
        /// public links; the file is then still uploaded and reachable by everyone who can open the folder.
        /// </summary>
        /// <returns>The file's link, and why it is not public when sharing was refused.</returns>
        public static async Task<(string link, string shareWarning)> UploadAsync(string path, string folderId, Action<float> progress)
        {
            string name = Path.GetFileName(path);
            long size = new FileInfo(path).Length;
            string type = path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ? "application/vnd.android.package-archive" : "application/octet-stream";

            string id = null;
            for (int attempt = 1; id == null; attempt++)
            {
                try
                {
                    id = await UploadOnceAsync(path, name, size, type, folderId, progress);
                }
                catch (Exception e) when (attempt < UploadAttempts && !(e is InvalidOperationException))
                {
                    Debug.LogWarning($"[Android Build Supporter] Upload of {name} failed (attempt {attempt}), retrying: {e.Message}");
                    await Task.Delay(2000 * attempt);
                }
            }

            string link = $"https://drive.google.com/file/d/{id}/view?usp=sharing";
            string token = await GoogleAuth.GetAccessTokenAsync();
            using (UnityWebRequest share = Http.Json($"{FilesUrl}/{id}/permissions?supportsAllDrives=true", "POST", JsonUtility.ToJson(new Permission()), token))
            {
                try
                {
                    await Http.SendAsync(share);
                    return (link, null);
                }
                catch (HttpException e)
                {
                    return (link, $"{name} was uploaded but not shared publicly (only people with access to the folder can open it): {e.Message}");
                }
            }
        }

        private static async Task<string> UploadOnceAsync(string path, string name, long size, string type, string folderId, Action<float> progress)
        {
            string token = await GoogleAuth.GetAccessTokenAsync();
            string session;
            string metadata = JsonUtility.ToJson(new FileMetadata { name = name, parents = new[] { folderId } });
            using (UnityWebRequest start = Http.Json(UploadUrl, "POST", metadata, token))
            {
                start.SetRequestHeader("X-Upload-Content-Type", type);
                start.SetRequestHeader("X-Upload-Content-Length", size.ToString());
                await Http.SendAsync(start);
                session = start.GetResponseHeader("Location");
                if (string.IsNullOrEmpty(session)) throw new IOException("Drive did not return an upload session.");
            }

            // One PUT streamed from disk: the whole file is never in memory.
            using (var upload = new UnityWebRequest(session, "PUT", new DownloadHandlerBuffer(), new UploadHandlerFile(path)))
            {
                upload.uploadHandler.contentType = type;
                await Http.SendAsync(upload, progress);
                return JsonUtility.FromJson<DriveFile>(upload.downloadHandler.text).id;
            }
        }

        /// <summary>Escapes a value inside a Drive query string literal.</summary>
        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
    }
}
