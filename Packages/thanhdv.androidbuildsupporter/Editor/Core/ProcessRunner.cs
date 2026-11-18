using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ThanhDV.AndroidBuildSupporter
{
    internal readonly struct ProcessResult
    {
        public readonly int ExitCode;
        public readonly string Output;

        public ProcessResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output;
        }

        public bool Succeeded => ExitCode == 0;

        /// <summary>The last lines of the output, for an error message.</summary>
        public string Tail(int lines = 6) =>
            string.Join("\n", Output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Reverse().Take(lines).Reverse());
    }

    /// <summary>Runs a command line tool without blocking the Editor. Arguments are quoted, never passed through a shell.</summary>
    internal static class ProcessRunner
    {
        public const int ExitTimeout = -100;
        public const int ExitNotStarted = -101;

        public static Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments,
            IDictionary<string, string> environment = null, int timeoutMs = 120000, string workingDirectory = null)
        {
            var output = new StringBuilder();
            var info = new ProcessStartInfo(file, string.Join(" ", arguments.Select(Quote)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };
            if (environment != null)
                foreach (KeyValuePair<string, string> pair in environment) info.EnvironmentVariables[pair.Key] = pair.Value;

            var process = new Process { StartInfo = info };
            DataReceivedEventHandler append = (_, e) =>
            {
                if (e.Data == null) return;
                lock (output) output.AppendLine(e.Data);
            };
            process.OutputDataReceived += append;
            process.ErrorDataReceived += append;

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                process.Dispose();
                return Task.FromResult(new ProcessResult(ExitNotStarted, $"Could not start {file}: {e.Message}"));
            }

            // Waits on a worker thread; awaiting the task from the Editor resumes on the main thread.
            return Task.Run(() =>
            {
                using (process)
                {
                    bool exited = process.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                            // It exited meanwhile.
                        }
                    }
                    // Without a timeout, also waits until the redirected output has been read to the end.
                    process.WaitForExit();
                    string text;
                    lock (output) text = output.ToString();
                    return exited
                        ? new ProcessResult(process.ExitCode, text)
                        : new ProcessResult(ExitTimeout, text + $"\nTimed out after {timeoutMs / 1000}s.");
                }
            });
        }

        /// <summary>Quotes one argument the way the Windows C runtime and .NET split a command line.</summary>
        private static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;

            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                quoted.Append(c);
            }
            quoted.Append('\\', backslashes * 2);
            return quoted.Append('"').ToString();
        }
    }
}
