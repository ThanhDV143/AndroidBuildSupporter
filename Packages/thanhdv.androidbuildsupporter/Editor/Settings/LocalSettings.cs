using UnityEditor;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// Settings of this machine, including secrets. Saved in UserSettings/, which Unity's .gitignore template ignores.
    /// Values are stored as plain text.
    /// </summary>
    [FilePath(RelativePath, FilePathAttribute.Location.ProjectFolder)]
    internal sealed class LocalSettings : ScriptableSingleton<LocalSettings>
    {
        public const string RelativePath = "UserSettings/ThanhDV.AndroidBuildSupporter.asset";

        public string keystorePass = "";
        public string keyaliasPass = "";

        public string discordWebhook = "";

        public string googleClientId = "";
        public string googleClientSecret = "";
        public string googleRefreshToken = "";
        /// <summary>Scopes Google granted, space separated.</summary>
        public string googleScope = "";
        /// <summary>Shown in the window; read from Drive after signing in.</summary>
        public string googleAccount = "";

        /// <summary>"*" installs on every connected device, otherwise an adb serial.</summary>
        public string installTarget = "*";

        /// <summary>Main switches above the build buttons; each is only turned on after its setup checks out.</summary>
        public bool installEnabled;
        public bool uploadEnabled;
        public bool notifyEnabled;
        /// <summary>Last address connected to over Wi-Fi, reconnected before installing.</summary>
        public string wirelessAddress = "";
        /// <summary>"" uses the bundletool that ships with the Editor's Android module.</summary>
        public string bundletoolPath = "";

        /// <summary>Where the last build was kept before the history existed; moved into it once, then cleared.</summary>
        public BuildRecord lastBuild;

        public void Save() => Save(true);

        /// <summary>
        /// Unity keeps keystore passwords in memory only. Copies them between Player Settings and this file: the
        /// passwords Unity has win, and the saved ones are put back after the Editor restarts.
        /// </summary>
        public void SyncKeystorePasswords()
        {
            RestoreKeystorePasswords();
            string store = PlayerSettings.Android.keystorePass ?? "";
            string alias = PlayerSettings.Android.keyaliasPass ?? "";
            if (store == keystorePass && alias == keyaliasPass) return;
            keystorePass = store;
            keyaliasPass = alias;
            Save();
        }

        /// <summary>Once per domain: Player Settings lose the passwords only when the Editor restarts.</summary>
        private static bool restored;

        [InitializeOnLoadMethod]
        private static void RestoreOnLoad() => EditorApplication.delayCall += RestoreKeystorePasswords;

        private static void RestoreKeystorePasswords()
        {
            if (restored) return;
            restored = true;
            LocalSettings local = instance;
            if (string.IsNullOrEmpty(PlayerSettings.Android.keystorePass) && !string.IsNullOrEmpty(local.keystorePass))
                PlayerSettings.Android.keystorePass = local.keystorePass;
            if (string.IsNullOrEmpty(PlayerSettings.Android.keyaliasPass) && !string.IsNullOrEmpty(local.keyaliasPass))
                PlayerSettings.Android.keyaliasPass = local.keyaliasPass;
        }
    }
}
