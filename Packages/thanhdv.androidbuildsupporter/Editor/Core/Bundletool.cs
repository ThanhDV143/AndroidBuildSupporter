using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEditor;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>Makes a universal APK from the AAB of a release build, signed with the same keystore.</summary>
    internal static class Bundletool
    {
        /// <returns>Null on success, otherwise why the APK was not made.</returns>
        public static async Task<string> BuildUniversalApkAsync(string aab, string apk)
        {
            string jar = AndroidTools.Bundletool;
            string java = AndroidTools.Java;
            if (jar == null) return "bundletool not found.";
            if (java == null) return "java not found in the JDK the Editor uses.";

            string temp = Path.Combine(Path.GetTempPath(), "abs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string apks = Path.Combine(temp, "universal.apks");
            string storePass = Path.Combine(temp, "store");
            string keyPass = Path.Combine(temp, "key");
            try
            {
                // Passwords go through files bundletool reads, so they never appear on a command line.
                File.WriteAllText(storePass, PlayerSettings.Android.keystorePass);
                File.WriteAllText(keyPass, PlayerSettings.Android.keyaliasPass);

                ProcessResult result = await ProcessRunner.RunAsync(java, new[]
                {
                    "-jar", jar, "build-apks", "--mode=universal", "--overwrite",
                    "--bundle=" + aab, "--output=" + apks,
                    "--ks=" + Keystore.Path, "--ks-pass=file:" + storePass,
                    "--ks-key-alias=" + PlayerSettings.Android.keyaliasName, "--key-pass=file:" + keyPass
                }, timeoutMs: 600000);
                if (!result.Succeeded) return "bundletool failed:\n" + result.Tail();

                using (var archive = new ZipArchive(File.OpenRead(apks), ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = archive.GetEntry("universal.apk");
                    if (entry == null) return "bundletool made no universal.apk.";
                    using (Stream source = entry.Open())
                    using (FileStream target = File.Create(apk))
                        await source.CopyToAsync(target);
                }
                return null;
            }
            catch (Exception e)
            {
                return "Could not make the APK: " + e.Message;
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (Exception)
                {
                    // Left in the system temp folder.
                }
            }
        }
    }
}
