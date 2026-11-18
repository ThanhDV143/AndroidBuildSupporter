using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ThanhDV.AndroidBuildSupporter
{
    internal readonly struct AndroidDevice
    {
        public readonly string Serial;
        /// <summary>device, unauthorized or offline.</summary>
        public readonly string State;
        public readonly string Model;

        public AndroidDevice(string serial, string state, string model)
        {
            Serial = serial;
            State = state;
            Model = model;
        }

        public bool Ready => State == "device";
        public string Label => (string.IsNullOrEmpty(Model) ? Serial : $"{Model} · {Serial}") + (Ready ? "" : $" ({State})");
    }

    /// <summary>Installs builds with the adb of the Android SDK the Editor uses.</summary>
    internal static class Adb
    {
        public static async Task<List<AndroidDevice>> DevicesAsync()
        {
            var devices = new List<AndroidDevice>();
            string adb = AndroidTools.Adb;
            if (adb == null) return devices;

            ProcessResult result = await ProcessRunner.RunAsync(adb, new[] { "devices", "-l" }, timeoutMs: 15000);
            if (!result.Succeeded) return devices;

            // The first run may print "daemon not running; starting now" before the list.
            foreach (string line in result.Output.Split('\n').SkipWhile(l => !l.StartsWith("List of devices", StringComparison.Ordinal)).Skip(1))
            {
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                string model = parts.Skip(2).FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?.Substring(6).Replace('_', ' ');
                devices.Add(new AndroidDevice(parts[0], parts[1], model));
            }
            return devices;
        }

        /// <summary>Installs the APK on the target saved in the window and opens the app.</summary>
        /// <returns>One line per device: where it was installed, or why not.</returns>
        public static async Task<List<string>> InstallAsync(string apk, string packageName, string target, List<string> warnings)
        {
            var installed = new List<string>();
            var (targets, error) = await TargetsAsync(target);
            if (error != null)
            {
                warnings.Add("Install skipped: " + error);
                return installed;
            }

            string adb = AndroidTools.Adb;
            foreach (AndroidDevice device in targets)
            {
                ProcessResult install = await ProcessRunner.RunAsync(adb, new[] { "-s", device.Serial, "install", "-r", apk }, timeoutMs: 600000);
                if (!install.Succeeded)
                {
                    warnings.Add($"Install failed on {device.Label}: {install.Tail(2)}");
                    continue;
                }

                await ProcessRunner.RunAsync(adb, new[]
                {
                    "-s", device.Serial, "shell", "monkey", "-p", packageName, "-c", "android.intent.category.LAUNCHER", "1"
                }, timeoutMs: 30000);
                installed.Add(device.Label);
            }
            return installed;
        }

        /// <summary>
        /// The ready devices the target means ("*" or empty: every connected device), reconnecting a Wi-Fi device
        /// first because those connections drop when the device sleeps or the Editor restarts adb.
        /// </summary>
        /// <returns>The devices, or why there are none.</returns>
        public static async Task<(List<AndroidDevice> devices, string error)> TargetsAsync(string target)
        {
            if (AndroidTools.Adb == null) return (null, "adb not found in the Android SDK the Editor uses.");
            bool all = string.IsNullOrEmpty(target) || target == "*";

            if (!all && IsNetworkSerial(target)) await ConnectAsync(target);
            else if (all && IsNetworkSerial(LocalSettings.instance.wirelessAddress)) await ConnectAsync(LocalSettings.instance.wirelessAddress);

            List<AndroidDevice> devices = await DevicesAsync();
            if (all)
            {
                List<AndroidDevice> ready = devices.Where(d => d.Ready).ToList();
                if (ready.Count > 0) return (ready, null);
                return (null, devices.Count > 0
                    ? $"{devices[0].Label} is connected but not authorized: allow USB debugging on the device."
                    : "no device is connected. Plug one in by USB or connect one over Wi-Fi in the Build tab.");
            }

            AndroidDevice match = devices.FirstOrDefault(d => d.Serial == target);
            if (match.Serial == null) return (null, $"device {target} is not connected. Choose another device in the Build tab.");
            if (!match.Ready) return (null, $"{match.Label} is not authorized: allow USB debugging on the device.");
            return (new List<AndroidDevice> { match }, null);
        }

        // --- Wireless debugging --------------------------------------------------------------------------------

        public const int DefaultPort = 5555;
        private static readonly Regex AddressPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.\-]*:(\d{1,5})$");
        private static readonly Regex CodePattern = new Regex(@"^\d{6}$");

        /// <summary>"192.168.1.20:5555"; an address without a port gets the default one.</summary>
        public static string NormalizeAddress(string address)
        {
            address = (address ?? "").Trim();
            return address.Length > 0 && address.IndexOf(':') < 0 ? address + ":" + DefaultPort : address;
        }

        /// <returns>Null when the address is host:port with a valid port, otherwise why not.</returns>
        public static string ValidateAddress(string address)
        {
            Match match = AddressPattern.Match(NormalizeAddress(address));
            if (!match.Success) return "Enter the device address as IP:port, for example 192.168.1.20:5555.";
            int port = int.Parse(match.Groups[1].Value);
            return port >= 1 && port <= 65535 ? null : "The port must be 1–65535.";
        }

        public static string ValidatePairingCode(string code) =>
            CodePattern.IsMatch((code ?? "").Trim()) ? null : "The pairing code has 6 digits.";

        /// <summary>Serials of devices connected over the network are their address.</summary>
        public static bool IsNetworkSerial(string serial) => !string.IsNullOrEmpty(serial) && AddressPattern.IsMatch(serial);

        /// <returns>Null when connected, otherwise adb's answer.</returns>
        public static async Task<string> ConnectAsync(string address)
        {
            address = NormalizeAddress(address);
            string invalid = ValidateAddress(address);
            if (invalid != null) return invalid;
            string adb = AndroidTools.Adb;
            if (adb == null) return "adb not found in the Android SDK the Editor uses.";

            // adb exits with 0 even when it fails to connect, so its answer decides.
            ProcessResult result = await ProcessRunner.RunAsync(adb, new[] { "connect", address }, timeoutMs: 20000);
            if (result.ExitCode == ProcessRunner.ExitTimeout)
                return $"No answer from {address}. Check the IP and port, and that the device is on the same Wi-Fi network.";
            string answer = Answer(result);
            return answer.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   answer.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0 &&
                   answer.IndexOf("cannot", StringComparison.OrdinalIgnoreCase) < 0
                ? null
                : answer.Length > 0 ? answer : "adb could not connect to " + address;
        }

        public static async Task DisconnectAsync(string address)
        {
            string adb = AndroidTools.Adb;
            if (adb == null || ValidateAddress(address) != null) return;
            await ProcessRunner.RunAsync(adb, new[] { "disconnect", NormalizeAddress(address) }, timeoutMs: 10000);
        }

        /// <summary>
        /// Android 11+: pairs with the address and code shown under Developer options > Wireless debugging > Pair
        /// device with pairing code. The pairing port differs from the port used to connect afterwards.
        /// </summary>
        /// <returns>Null when paired, otherwise adb's answer.</returns>
        public static async Task<string> PairAsync(string address, string code)
        {
            string invalid = ValidateAddress(address) ?? ValidatePairingCode(code);
            if (invalid != null) return invalid;
            string adb = AndroidTools.Adb;
            if (adb == null) return "adb not found in the Android SDK the Editor uses.";

            ProcessResult result = await ProcessRunner.RunAsync(adb, new[] { "pair", NormalizeAddress(address), code.Trim() }, timeoutMs: 30000);
            if (result.Output.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (result.ExitCode == ProcessRunner.ExitTimeout) return "No answer from the pairing address. Check it and that the pairing dialog is still open.";
            string answer = Answer(result);
            return answer.Length > 0 ? answer : "Pairing failed.";
        }

        /// <summary>adb's last line without the "* daemon started" lines it prints when it starts its server.</summary>
        private static string Answer(ProcessResult result) =>
            result.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("*", StringComparison.Ordinal)).LastOrDefault() ?? "";

        /// <summary>
        /// Any Android version: reads the Wi-Fi address of a device connected by USB, restarts its adb on port 5555
        /// and connects to it, after which the cable can be unplugged.
        /// </summary>
        /// <returns>The address connected to, or an error.</returns>
        public static async Task<(string address, string error)> SwitchToWifiAsync(string serial)
        {
            string adb = AndroidTools.Adb;
            if (adb == null) return (null, "adb not found in the Android SDK the Editor uses.");

            ProcessResult route = await ProcessRunner.RunAsync(adb, new[] { "-s", serial, "shell", "ip", "-f", "inet", "addr", "show", "wlan0" }, timeoutMs: 15000);
            Match ip = Regex.Match(route.Output, @"inet (\d{1,3}(?:\.\d{1,3}){3})/");
            if (!ip.Success) return (null, "The device has no Wi-Fi address. Connect it to the same Wi-Fi network as this computer.");

            ProcessResult tcpip = await ProcessRunner.RunAsync(adb, new[] { "-s", serial, "tcpip", DefaultPort.ToString() }, timeoutMs: 15000);
            if (!tcpip.Succeeded) return (null, "adb tcpip failed: " + tcpip.Tail(2));

            // adbd restarts on the device before it accepts connections.
            await Task.Delay(2000);
            string address = ip.Groups[1].Value + ":" + DefaultPort;
            string error = await ConnectAsync(address);
            return error == null ? (address, null) : (null, error);
        }
    }
}
