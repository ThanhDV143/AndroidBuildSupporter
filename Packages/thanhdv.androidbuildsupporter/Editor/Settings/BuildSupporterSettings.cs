using System.Collections.Generic;
using UnityEditor;

namespace ThanhDV.AndroidBuildSupporter
{
    internal enum CompressionMethod
    {
        Default,
        LZ4,
        LZ4HC
    }

    /// <summary>
    /// Settings shared by everyone working on the project. Saved in ProjectSettings/ and committed; holds no secret.
    /// Settings Unity already stores (Player Settings, Build Settings) are not copied here.
    /// </summary>
    [FilePath("ProjectSettings/ThanhDV.AndroidBuildSupporter.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class BuildSupporterSettings : ScriptableSingleton<BuildSupporterSettings>
    {
        /// <summary>Number of parts in the version: 1.2.3 has 3, 1.11 has 2.</summary>
        public int versionParts = VersionCode.DefaultParts;

        public string outputFolder = "Builds/{version}";
        public string fileName = "{product}_{version}_{code}_{variant}";

        /// <summary>Only used when the Editor's own Compression Method cannot be read.</summary>
        public CompressionMethod compression = CompressionMethod.LZ4;

        public List<string> symbolsToRemove = new List<string>();
        public List<string> forbiddenAssemblies = new List<string>();

        /// <summary>Link (or id) of the Drive folder the project's builds go into; empty means My Drive.</summary>
        public string driveRootFolder = "";
        /// <summary>Folders created under the root folder for each build.</summary>
        public string driveFolder = "Builds/{version}";
        public bool uploadQaApk = true;
        public bool uploadReleaseAab = true;
        public bool uploadReleaseApk = true;

        public bool notifySuccess = true;
        public bool notifyFailure = true;
        /// <summary>Discord user IDs, separated by commas, mentioned in every build message, success or failure.</summary>
        public string mentionUserId = "";

        public void Save() => Save(true);
    }
}
