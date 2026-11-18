using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace ThanhDV.AndroidBuildSupporter
{
    [Serializable]
    internal sealed class BuildFile
    {
        public string name;
        public string path;
        public long size;
        public string link;

        public bool Exists => !string.IsNullOrEmpty(path) && File.Exists(path);
        public bool IsApk => path != null && path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
    }

    [Serializable]
    internal sealed class BuildRecord
    {
        public string id;
        /// <summary>QA or Release.</summary>
        public string variant;
        /// <summary>Succeeded, Warning or Failed.</summary>
        public string state;
        public string version;
        public int code;
        public string package;
        public string startedAt;
        public string finishedAt;
        public double seconds;
        /// <summary>Local folder the files were written to.</summary>
        public string folder;
        /// <summary>Drive folder path under the project folder, resolved when the build ran.</summary>
        public string driveFolder;
        public string slowestSteps;
        /// <summary>Build log of a failed build.</summary>
        public string logPath;
        public List<BuildFile> files = new List<BuildFile>();
        public List<string> installedOn = new List<string>();
        public List<string> warnings = new List<string>();
        public string error;

        public bool HasLog => !string.IsNullOrEmpty(logPath) && File.Exists(logPath);
        public BuildFile Apk => files.FirstOrDefault(f => f.IsApk && f.Exists);
    }

    /// <summary>
    /// The builds made on this machine, newest first. Saved in UserSettings/: file paths only mean something on the
    /// machine that built them, and the team already shares builds through Drive and Discord.
    /// </summary>
    [FilePath("UserSettings/ThanhDV.AndroidBuildSupporter.History.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class BuildHistory : ScriptableSingleton<BuildHistory>
    {
        public const int MaxRecords = 100;
        public const string LogFolder = "Library/ThanhDV.AndroidBuildSupporter/Logs";

        public List<BuildRecord> records = new List<BuildRecord>();

        /// <summary>Goes up on every change, so the window rebuilds its list only when something changed.</summary>
        public static int Revision { get; private set; }

        public static event Action Changed;

        public BuildRecord Latest
        {
            get
            {
                MoveLastBuild();
                return records.FirstOrDefault();
            }
        }

        public BuildRecord Find(string id) => records.FirstOrDefault(r => r.id == id);

        public void Add(BuildRecord record)
        {
            MoveLastBuild();
            records.Insert(0, record);
            // The oldest go, and with them their logs; build files stay where the user keeps them.
            while (records.Count > MaxRecords)
            {
                DeleteLog(records[records.Count - 1]);
                records.RemoveAt(records.Count - 1);
            }
            Commit();
        }

        public void Commit()
        {
            Save(true);
            Revision++;
            Changed?.Invoke();
        }

        /// <param name="deleteFiles">Also deletes the build files from disk.</param>
        public void Remove(BuildRecord record, bool deleteFiles)
        {
            if (deleteFiles)
                foreach (BuildFile file in record.files.Where(f => f.Exists))
                    File.Delete(file.path);
            DeleteLog(record);
            records.Remove(record);
            Commit();
        }

        /// <summary>Saves the log of a failed build and returns its path.</summary>
        public static string SaveLog(string id, string log)
        {
            Directory.CreateDirectory(LogFolder);
            string path = Path.GetFullPath(Path.Combine(LogFolder, id + ".txt"));
            File.WriteAllText(path, log);
            return path;
        }

        private static void DeleteLog(BuildRecord record)
        {
            if (record.HasLog) File.Delete(record.logPath);
        }

        /// <summary>The single last build kept before the history existed becomes its first entry.</summary>
        private void MoveLastBuild()
        {
            LocalSettings local = LocalSettings.instance;
            if (local.lastBuild == null || string.IsNullOrEmpty(local.lastBuild.variant)) return;
            BuildRecord old = local.lastBuild;
            if (string.IsNullOrEmpty(old.id)) old.id = Guid.NewGuid().ToString("N");
            if (records.Count == 0) records.Add(old);
            local.lastBuild = null;
            local.Save();
            Save(true);
            Revision++;
        }
    }
}
