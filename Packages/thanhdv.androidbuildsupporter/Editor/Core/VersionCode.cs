using UnityEditor;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>
    /// Bundle Version Code made from the version: the first part as is, every later part in 2 digits, then a 2-digit
    /// build number. 1.11 build 3 is 11103 and 1.2.3 build 0 is 1020300. With the same number of parts and every later
    /// part in 0–99, a higher version always gives a higher code.
    /// </summary>
    internal static class VersionCode
    {
        public const int MaxBuild = 99;
        /// <summary>The highest version code Google Play accepts.</summary>
        public const int MaxCode = 2100000000;
        public const int MinParts = 1;
        public const int MaxParts = 3;

        public static int BuildOf(int code) => code % 100;

        /// <summary>The code without its build number, or an error that says what is wrong with the version.</summary>
        public static bool TryGetBase(string version, int parts, out int baseCode, out string error)
        {
            baseCode = 0;
            error = null;
            string[] tokens = (version ?? "").Trim().Split('.');
            if (tokens.Length != parts)
            {
                error = $"Version must have {parts} part{(parts == 1 ? "" : "s")} ({Example(parts)}).";
                return false;
            }

            long value = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token.Length == 0 || !IsDigits(token))
                {
                    error = $"'{token}' is not a number.";
                    return false;
                }
                if (token.Length > 1 && token[0] == '0')
                {
                    error = $"'{token}' has a leading zero: {token.TrimStart('0').PadLeft(1, '0')} and {token} would give the same code.";
                    return false;
                }
                if (token.Length > 9)
                {
                    error = $"'{token}' is too large.";
                    return false;
                }

                int part = int.Parse(token);
                if (i > 0 && part > 99)
                {
                    error = $"'{token}' is above 99: every part after the first must be 0–99.";
                    return false;
                }
                value = i == 0 ? part : value * 100 + part;
            }

            if (value * 100 + MaxBuild > MaxCode)
            {
                error = $"Version is too large: version codes must stay at or below {MaxCode}.";
                return false;
            }
            baseCode = (int)value;
            return true;
        }

        public const int DefaultParts = 3;

        public static int Parts
        {
            get
            {
                int parts = BuildSupporterSettings.instance.versionParts;
                return parts >= MinParts && parts <= MaxParts ? parts : DefaultParts;
            }
        }

        /// <summary>
        /// The version code the next build uses, without writing anything: the current code when it belongs to the
        /// current version, otherwise the new version's code with build 00.
        /// </summary>
        /// <returns>Null when that code is valid, otherwise why it is not.</returns>
        public static string Evaluate(out int next)
        {
            int current = PlayerSettings.Android.bundleVersionCode;
            next = current;
            if (!TryGetBase(PlayerSettings.bundleVersion, Parts, out int baseCode, out string error)) return error;
            if (current / 100 == baseCode) return null;

            int code = baseCode * 100;
            if (code <= current)
                return $"Version {PlayerSettings.bundleVersion} gives version code {code}, which is not above the current {current}. " +
                       "Google Play rejects a lower code: raise the version, or set Bundle Version Code in Project Settings yourself.";
            next = code;
            return null;
        }

        /// <summary>
        /// Writes the code <see cref="Evaluate"/> gives. Only called when a build starts: the stored code is then always
        /// one a build used (plus one), and changing the version without building never moves it.
        /// </summary>
        public static string Apply()
        {
            string error = Evaluate(out int next);
            if (error == null && next != PlayerSettings.Android.bundleVersionCode) PlayerSettings.Android.bundleVersionCode = next;
            return error;
        }

        private static bool IsDigits(string s)
        {
            foreach (char c in s)
                if (c < '0' || c > '9') return false;
            return true;
        }

        private static string Example(int parts) => parts == 1 ? "12" : parts == 2 ? "1.11" : "1.2.3";
    }
}
