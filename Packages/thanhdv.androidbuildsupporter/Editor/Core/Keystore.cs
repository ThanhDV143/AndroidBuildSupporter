using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// Checks the signing settings in about a second with keytool, instead of finding out at the end of a build.
    /// Passwords go to keytool through environment variables, never on the command line.
    /// </summary>
    internal static class Keystore
    {
        private const string StorePassVariable = "ABS_STORE_PASS";
        private const string KeyPassVariable = "ABS_KEY_PASS";

        /// <summary>
        /// Unity 6 can keep keystores in a dedicated folder and resolves the name itself (internal API); otherwise
        /// the name is a path, absolute or relative to the project.
        /// </summary>
        public static string Path
        {
            get
            {
                try
                {
                    var resolve = typeof(PlayerSettings.Android).GetMethod("GetAndroidKeystoreFullPath",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                        null, Type.EmptyTypes, null);
                    if (resolve?.Invoke(null, null) is string full && !string.IsNullOrEmpty(full)) return full;
                }
                catch (Exception)
                {
                    // Fall back to the name as a path.
                }
                return AndroidTools.FullPath(PlayerSettings.Android.keystoreName);
            }
        }

        /// <returns>Null when the keystore opens with these passwords and has the alias, otherwise what is wrong.</returns>
        public static async Task<string> CheckAsync()
        {
            string keystore = Path;
            string storePass = PlayerSettings.Android.keystorePass;
            string alias = PlayerSettings.Android.keyaliasName;
            string keyPass = PlayerSettings.Android.keyaliasPass;

            if (string.IsNullOrEmpty(PlayerSettings.Android.keystoreName)) return "No keystore is selected.";
            if (!File.Exists(keystore)) return $"Keystore not found: {keystore}";
            if (string.IsNullOrEmpty(storePass)) return "Enter the keystore password.";
            if (string.IsNullOrEmpty(alias)) return "Enter the key alias.";
            if (string.IsNullOrEmpty(keyPass)) return "Enter the alias password.";

            string keytool = AndroidTools.Keytool;
            if (keytool == null) return "keytool not found in the JDK the Editor uses (Preferences > External Tools).";

            var environment = new Dictionary<string, string> { { StorePassVariable, storePass } };

            ProcessResult list = await ProcessRunner.RunAsync(keytool, new[]
            {
                "-J-Duser.language=en", "-list", "-keystore", keystore, "-storepass:env", StorePassVariable, "-alias", alias
            }, environment, 30000);
            if (!list.Succeeded)
            {
                if (Contains(list.Output, "password")) return "The keystore password is wrong.";
                if (Contains(list.Output, "does not exist")) return $"The keystore has no alias '{alias}'.";
                return "keytool could not open the keystore:\n" + list.Tail();
            }

            return await CheckAliasPasswordAsync();
        }

        /// <summary>
        /// Whether the alias password opens the alias's private key; assumes the keystore password is right (the
        /// window has checked it by listing the aliases).
        /// </summary>
        /// <returns>Null when it does, otherwise what is wrong.</returns>
        public static async Task<string> CheckAliasPasswordAsync()
        {
            string keytool = AndroidTools.Keytool;
            if (keytool == null) return "keytool not found in the JDK the Editor uses (Preferences > External Tools).";
            var environment = new Dictionary<string, string>
            {
                { StorePassVariable, PlayerSettings.Android.keystorePass }, { KeyPassVariable, PlayerSettings.Android.keyaliasPass }
            };

            // A certificate request is signed with the private key, so it only succeeds with the right alias password.
            string request = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "abs-" + Guid.NewGuid().ToString("N") + ".csr");
            try
            {
                ProcessResult key = await ProcessRunner.RunAsync(keytool, new[]
                {
                    "-J-Duser.language=en", "-certreq", "-keystore", Path, "-storepass:env", StorePassVariable,
                    "-alias", PlayerSettings.Android.keyaliasName, "-keypass:env", KeyPassVariable, "-file", request
                }, environment, 30000);
                if (key.Succeeded) return null;
                if (Contains(key.Output, "password") || Contains(key.Output, "recover key")) return "The alias password is wrong.";
                return "keytool could not read the key:\n" + key.Tail();
            }
            finally
            {
                if (File.Exists(request)) File.Delete(request);
            }
        }

        /// <summary>The aliases of the keystore's private keys, read with the keystore password as Unity does.</summary>
        /// <returns>The aliases, or an error when the keystore cannot be opened.</returns>
        public static async Task<(List<string> aliases, string error)> ListAliasesAsync()
        {
            string keystore = Path;
            string storePass = PlayerSettings.Android.keystorePass;
            if (string.IsNullOrEmpty(PlayerSettings.Android.keystoreName) || !File.Exists(keystore)) return (new List<string>(), null);
            if (string.IsNullOrEmpty(storePass)) return (new List<string>(), null);
            string keytool = AndroidTools.Keytool;
            if (keytool == null) return (new List<string>(), "keytool not found in the JDK the Editor uses.");

            ProcessResult result = await ProcessRunner.RunAsync(keytool, new[]
            {
                "-J-Duser.language=en", "-list", "-v", "-keystore", keystore, "-storepass:env", StorePassVariable
            }, new Dictionary<string, string> { { StorePassVariable, storePass } }, 30000);
            if (!result.Succeeded)
                return (new List<string>(), Contains(result.Output, "password") ? "The keystore password is wrong." : "keytool could not open the keystore:\n" + result.Tail(3));

            // Each entry starts with "Alias name: x" followed by "Entry type: PrivateKeyEntry"; only keys can sign.
            var aliases = new List<string>();
            string alias = null;
            foreach (string raw in result.Output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Alias name:", StringComparison.Ordinal)) alias = line.Substring("Alias name:".Length).Trim();
                else if (alias != null && line.StartsWith("Entry type:", StringComparison.Ordinal))
                {
                    if (line.IndexOf("PrivateKeyEntry", StringComparison.Ordinal) >= 0) aliases.Add(alias);
                    alias = null;
                }
            }
            return (aliases, null);
        }

        private static bool Contains(string text, string value) => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
