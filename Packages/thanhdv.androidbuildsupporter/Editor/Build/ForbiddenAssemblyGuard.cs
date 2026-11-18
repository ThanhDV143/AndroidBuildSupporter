using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// Fails a release build right after its scripts compile when an assembly from the Forbidden assemblies list is
    /// in it, for example because its symbol is still set somewhere the release did not remove it from.
    /// </summary>
    internal sealed class ForbiddenAssemblyGuard : IPostBuildPlayerScriptDLLs
    {
        /// <summary>Set only while this tool runs a release build; other builds are not checked.</summary>
        internal static IReadOnlyCollection<string> Active;

        public int callbackOrder => 0;

        public void OnPostBuildPlayerScriptDLLs(BuildReport report)
        {
            if (Active == null || Active.Count == 0) return;

            var forbidden = new HashSet<string>(Active.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            string[] found = report.GetFiles()
                .Select(f => Path.GetFileName(f.path))
                .Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && forbidden.Contains(Normalize(name)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (found.Length == 0) return;

            throw new BuildFailedException("[Android Build Supporter] Forbidden assemblies are in the release build: " +
                                           string.Join(", ", found) +
                                           ". The symbol that compiles them is probably still set in Player Settings or the active Build Profile; add it to Symbols to remove.");
        }

        private static string Normalize(string name)
        {
            name = name.Trim();
            return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }
    }
}
