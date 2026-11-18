using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>The Android settings edited most, the tool's own settings, and the two build buttons.</summary>
    internal sealed class BuildSupporterWindow : EditorWindow
    {
        private const string StylePath = "Packages/thanhdv.androidbuildsupporter/Editor/UI/BuildSupporterWindow.uss";
        private const string TabPref = "ThanhDV.AndroidBuildSupporter.Tab";
        private static readonly Regex SymbolPattern = new Regex("^[A-Za-z_][A-Za-z0-9_]*$");
        private static readonly string[] Tabs = { "App", "Android", "Build", "Distribution", "History" };
        private const int HistoryTab = 4;

        private readonly HistoryPage history = new HistoryPage();

        /// <summary>AndroidArchitecture.X86, by value: Unity 6 marks it obsolete and no longer builds it.</summary>
        private const AndroidArchitecture X86 = (AndroidArchitecture)4;
        private static readonly bool X86Supported = typeof(AndroidArchitecture).GetField("X86")?.IsDefined(typeof(ObsoleteAttribute), false) == false;

        private readonly Form form = new Form();
        private readonly List<VisualElement> tabButtons = new List<VisualElement>();
        private readonly List<VisualElement> pages = new List<VisualElement>();
        private Button buildQa;
        private Button buildRelease;
        private VisualElement progress;
        private VisualElement progressFill;
        private Label status;

        private string versionError;
        private int nextCode;
        private List<string> aliases = new List<string>();
        private string aliasError;
        private bool loadingAliases;
        private int aliasRequest;
        /// <summary>The keystore and password the aliases were read with; a change made in Project Settings reads them again.</summary>
        private string aliasSource;

        private static string AliasSource => PlayerSettings.Android.keystoreName + "\n" + PlayerSettings.Android.keystorePass;

        private string aliasPassError;
        private bool checkingAliasPass;
        private int aliasPassRequest;
        /// <summary>Everything the alias password check depends on; a change made in Project Settings checks again.</summary>
        private string aliasPassSource;

        private static string AliasPassSource => AliasSource + "\n" + PlayerSettings.Android.keyaliasName + "\n" + PlayerSettings.Android.keyaliasPass;
        private bool secretsNotIgnored;
        private string googleMessage;
        private string discordMessage;
        private List<AndroidDevice> devices = new List<AndroidDevice>();
        private bool adbBusy;
        private string adbMessage;
        private string adbMessageKind = "info";
        private string pairAddress = "";
        private string pairCode = "";

        [MenuItem("Tools/ThanhDV/Android Build Supporter")]
        private static void Open()
        {
            var window = GetWindow<BuildSupporterWindow>();
            window.titleContent = new GUIContent("Android Build", EditorGUIUtility.IconContent("BuildSettings.Android.Small").image);
            window.minSize = new Vector2(520, 480);
        }

        private void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (style != null) root.styleSheets.Add(style);
            root.AddToClassList("abs-root");

            BuildHeader(root);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("abs-scroll");
            root.Add(scroll);
            AddPage(scroll, BuildAppPage);
            AddPage(scroll, BuildAndroidPage);
            AddPage(scroll, BuildBuildPage);
            AddPage(scroll, BuildDistributionPage);
            AddPage(scroll, page => history.Build(page, form));
            BuildFooter(root);

            ShowTab(EditorPrefs.GetInt(TabPref, 0));
            BuildRunner.Changed += OnBuildChanged;
            root.schedule.Execute(Refresh).Every(1000);
            Refresh();
            CheckSecretsIgnoredAsync();
            ListDevicesAsync();
            LoadAliasesAsync(false);
            // Signed in before the name was saved: read it once.
            if (GoogleAuth.IsSignedIn && string.IsNullOrEmpty(LocalSettings.instance.googleAccount)) LoadGoogleAccountAsync();
            CheckDriveRootAsync();
        }

        private async void LoadGoogleAccountAsync()
        {
            await GoogleAuth.LoadAccountAsync();
            Refresh();
        }

        private void OnDisable() => BuildRunner.Changed -= OnBuildChanged;

        private void OnFocus() => Refresh();

        private void Refresh()
        {
            if (buildQa == null) return;
            // Reads only: opening or refreshing the window never writes Unity's settings.
            versionError = VersionCode.Evaluate(out nextCode);
            LocalSettings.instance.SyncKeystorePasswords();
            if (aliasSource != null && aliasSource != AliasSource && !loadingAliases) LoadAliasesAsync(false);
            else if (aliasPassSource != null && aliasPassSource != AliasPassSource && !loadingAliases && !checkingAliasPass) CheckAliasPasswordAsync();
            form.Refresh();
            UpdateFooter();
        }

        /// <summary>Progress arrives several times a second; only the footer follows it.</summary>
        private void OnBuildChanged()
        {
            if (buildQa == null) return;
            UpdateFooter();
            if (!BuildRunner.IsRunning) Refresh();
        }

        private void UpdateFooter()
        {
            bool running = BuildRunner.IsRunning;
            buildQa.SetEnabled(!running);
            buildRelease.SetEnabled(!running);
            progress.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            progressFill.style.width = Length.Percent(Mathf.Clamp01(BuildRunner.ProgressValue) * 100f);
            status.text = BuildRunner.Status;
            status.style.display = string.IsNullOrEmpty(BuildRunner.Status) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // --- Frame -------------------------------------------------------------------------------------------

        private void BuildHeader(VisualElement root)
        {
            var header = new VisualElement();
            header.AddToClassList("abs-header");

            var icon = new Image();
            icon.AddToClassList("abs-app-icon");
            var initial = new Label();
            initial.AddToClassList("abs-app-initial");
            icon.Add(initial);
            header.Add(icon);

            var titles = new VisualElement();
            titles.AddToClassList("abs-titles");
            var title = new Label();
            title.AddToClassList("abs-title");
            var subtitle = new Label();
            subtitle.AddToClassList("abs-subtitle");
            titles.Add(title);
            titles.Add(subtitle);
            header.Add(titles);

            var platform = new Label();
            platform.AddToClassList("abs-badge");
            header.Add(platform);

            form.OnRefresh(() =>
            {
                Texture2D texture = UnityCompat.DefaultIcon;
                icon.image = texture;
                icon.EnableInClassList("abs-app-icon-empty", texture == null);
                string product = PlayerSettings.productName ?? "";
                initial.text = texture == null && product.Length > 0 ? product.Substring(0, 1).ToUpperInvariant() : "";
                title.text = product;
                subtitle.text = $"{PlayerSettings.GetApplicationIdentifier(UnityCompat.Android)}  ·  {PlayerSettings.bundleVersion} ({PlayerSettings.Android.bundleVersionCode})";
                bool android = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;
                string profile = UnityCompat.ActiveProfileName;
                platform.text = !android ? "Platform is not Android" : profile != null ? "Profile: " + profile : "Android";
                platform.EnableInClassList("abs-badge-warning", !android);
                platform.tooltip = android ? "Active build target" : "Switch the platform to Android in Build Settings before building.";
            });
            root.Add(header);

            // Plain elements rather than Buttons: a Button brings its own borders, margins and padding, which pushed
            // the selected background off centre.
            var tabs = new VisualElement();
            tabs.AddToClassList("abs-tabs");
            for (int i = 0; i < Tabs.Length; i++)
            {
                int index = i;
                var tab = new VisualElement();
                tab.AddToClassList("abs-tab");
                var text = new Label(Tabs[i]);
                text.AddToClassList("abs-tab-text");
                tab.Add(text);
                tab.AddManipulator(new Clickable(() => ShowTab(index)));
                tabButtons.Add(tab);
                tabs.Add(tab);
            }
            root.Add(tabs);

            var legend = new VisualElement();
            legend.AddToClassList("abs-legend");
            foreach (var (source, text) in new[] { (Source.Unity, "Unity settings"), (Source.Tool, "Project (committed)"), (Source.Local, "This machine") })
            {
                VisualElement item = Form.LabelColumn(text, source);
                item.RemoveFromClassList("abs-label");
                item.AddToClassList("abs-legend-item");
                legend.Add(item);
            }
            root.Add(legend);
        }

        private void AddPage(VisualElement parent, Action<VisualElement> build)
        {
            var page = new VisualElement();
            page.AddToClassList("abs-page");
            build(page);
            pages.Add(page);
            parent.Add(page);
        }

        private void ShowTab(int index)
        {
            index = Mathf.Clamp(index, 0, Tabs.Length - 1);
            EditorPrefs.SetInt(TabPref, index);
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].style.display = i == index ? DisplayStyle.Flex : DisplayStyle.None;
                tabButtons[i].EnableInClassList("abs-tab-selected", i == index);
            }
        }

        private void BuildFooter(VisualElement root)
        {
            var footer = new VisualElement();
            footer.AddToClassList("abs-footer");

            progress = new VisualElement();
            progress.AddToClassList("abs-progress");
            progressFill = new VisualElement();
            progressFill.AddToClassList("abs-progress-fill");
            progress.Add(progressFill);
            footer.Add(progress);

            status = new Label();
            status.AddToClassList("abs-status");
            footer.Add(status);

            // Main switches for what happens after a build. Each is checked when turned on and stays off when its
            // setup is not ready, so a build never starts with a step that is bound to fail.
            LocalSettings local = LocalSettings.instance;
            var switches = new VisualElement();
            switches.AddToClassList("abs-switches");
            Switch(switches, "Install & run", "Install the APK on the device chosen in the Build tab and open the app",
                () => local.installEnabled, v => local.installEnabled = v, CheckInstallAsync);
            Switch(switches, "Upload to Drive", "Upload the build to Google Drive (Distribution tab)",
                () => local.uploadEnabled, v => local.uploadEnabled = v, CheckUploadAsync);
            Switch(switches, "Notify Discord", "Send the result to Discord (Distribution tab)",
                () => local.notifyEnabled, v => local.notifyEnabled = v, CheckNotifyAsync);
            footer.Add(switches);
            // Results of what the user just did hide by themselves; problems that block the build stay until fixed.
            form.Notice(footer, () => switchMessage, () => switchMessageKind, autoHide: true);

            var buttons = new VisualElement();
            buttons.AddToClassList("abs-actions");
            buildQa = new Button(() => BuildRunner.Start(BuildVariant.QA)) { text = "Build QA", tooltip = "APK with every scripting define symbol" };
            buildRelease = new Button(() => BuildRunner.Start(BuildVariant.Release)) { text = "Build release", tooltip = "AAB without the release symbols, plus a universal APK made from it" };
            buildQa.AddToClassList("abs-action");
            buildRelease.AddToClassList("abs-action");
            buildRelease.AddToClassList("abs-action-primary");
            buttons.Add(buildQa);
            buttons.Add(buildRelease);
            footer.Add(buttons);

            var last = new VisualElement();
            last.AddToClassList("abs-last");
            var chip = new Label();
            chip.AddToClassList("abs-state");
            var text = new Label();
            text.AddToClassList("abs-last-text");
            // The last build opens in the History tab.
            text.AddManipulator(new Clickable(() =>
            {
                ShowTab(HistoryTab);
                history.Expand(BuildHistory.instance.Latest?.id);
            }));
            last.Add(chip);
            last.Add(text);
            Button show = Form.Button(last, "Show", () =>
            {
                BuildRecord record = BuildHistory.instance.Latest;
                string file = record?.files.Select(f => f.path).FirstOrDefault(File.Exists);
                if (file != null) EditorUtility.RevealInFinder(file);
                else if (record != null && Directory.Exists(record.folder)) EditorUtility.RevealInFinder(record.folder);
            }, "Show the build in the file browser");
            Button copy = Form.Button(last, "Copy links", () =>
            {
                BuildRecord record = BuildHistory.instance.Latest;
                if (record == null) return;
                EditorGUIUtility.systemCopyBuffer = string.Join("\n", record.files.Where(f => f.link != null).Select(f => $"[{f.name}]({f.link})"));
            }, "Copy the Google Drive links");
            footer.Add(last);

            form.OnRefresh(() =>
            {
                BuildRecord record = BuildHistory.instance.Latest;
                bool any = record != null && !string.IsNullOrEmpty(record.variant);
                last.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
                if (!any) return;
                chip.text = record.state == BuildRunner.Failed ? "Failed" : record.state == BuildRunner.Warning ? "Warnings" : "OK";
                chip.EnableInClassList("abs-state-ok", record.state == BuildRunner.Succeeded);
                chip.EnableInClassList("abs-state-warning", record.state == BuildRunner.Warning);
                chip.EnableInClassList("abs-state-error", record.state == BuildRunner.Failed);
                text.text = $"Last: {record.variant} {record.version} ({record.code}) · {Naming.Duration(record.seconds)} · {record.finishedAt}";
                text.tooltip = string.Join("\n", new[] { record.error }.Concat(record.warnings).Concat(new[] { "Click to open it in History." })
                    .Where(s => !string.IsNullOrEmpty(s)));
                show.style.display = record.files.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                copy.style.display = record.files.Any(f => f.link != null) ? DisplayStyle.Flex : DisplayStyle.None;
            });
            root.Add(footer);
        }

        private string switchMessage;
        private string switchMessageKind = "info";

        /// <summary>
        /// A switch that turns on only when <paramref name="check"/> finds nothing wrong; otherwise it turns itself
        /// off again and says why. Turning it off needs no check.
        /// </summary>
        private void Switch(VisualElement parent, string text, string tooltip, Func<bool> get, Action<bool> set, Func<Task<string>> check)
        {
            var toggle = new Toggle { text = text, tooltip = tooltip };
            toggle.AddToClassList("abs-switch");
            bool checking = false;
            toggle.RegisterValueChangedCallback(async e =>
            {
                if (!e.newValue)
                {
                    set(false);
                    LocalSettings.instance.Save();
                    switchMessage = null;
                    Refresh();
                    return;
                }

                checking = true;
                toggle.SetEnabled(false);
                switchMessage = $"Checking {text}…";
                switchMessageKind = "info";
                Refresh();
                string error;
                try
                {
                    error = await check();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                checking = false;
                toggle.SetEnabled(true);
                set(error == null);
                LocalSettings.instance.Save();
                switchMessage = error == null ? null : $"{text} is off: {error}";
                switchMessageKind = "warning";
                Refresh();
            });
            form.OnRefresh(() =>
            {
                if (!checking) toggle.SetValueWithoutNotify(get());
                toggle.SetEnabled(!checking && !BuildRunner.IsRunning);
            });
            parent.Add(toggle);
        }

        private async Task<string> CheckInstallAsync()
        {
            string error = (await Adb.TargetsAsync(LocalSettings.instance.installTarget)).error;
            devices = await Adb.DevicesAsync();
            return error == null ? null : char.ToUpperInvariant(error[0]) + error.Substring(1);
        }

        private static async Task<string> CheckNotifyAsync()
        {
            BuildSupporterSettings settings = BuildSupporterSettings.instance;
            if (!settings.notifySuccess && !settings.notifyFailure) return "Tick Success or Failure under Notify on in the Distribution tab.";
            return await Discord.CheckAsync();
        }

        private static async Task<string> CheckUploadAsync()
        {
            BuildSupporterSettings settings = BuildSupporterSettings.instance;
            LocalSettings local = LocalSettings.instance;
            if (string.IsNullOrWhiteSpace(local.googleClientId) || string.IsNullOrWhiteSpace(local.googleClientSecret))
                return "Enter the Google OAuth Client ID and secret in the Distribution tab.";
            if (!GoogleAuth.IsSignedIn) return "Sign in to Google Drive in the Distribution tab.";
            if (!GoogleAuth.HasRequiredScope) return "The project folder needs full Drive access: press Sign in again in the Distribution tab.";
            if (!settings.uploadQaApk && !settings.uploadReleaseAab && !settings.uploadReleaseApk)
                return "Tick at least one file to upload in the Distribution tab.";
            await GoogleAuth.GetAccessTokenAsync();
            // The project folder must exist and accept files from this account.
            return (await GoogleDrive.CheckRootAsync()).error;
        }

        // --- App ---------------------------------------------------------------------------------------------

        private void BuildAppPage(VisualElement page)
        {
            BuildSupporterSettings settings = BuildSupporterSettings.instance;

            VisualElement identity = Form.Card(page, "Identity");
            form.Text(identity, "Company name", () => PlayerSettings.companyName, v => PlayerSettings.companyName = v.Trim(), Source.Unity);
            form.Text(identity, "Product name", () => PlayerSettings.productName, v => PlayerSettings.productName = v.Trim(), Source.Unity);
            VisualElement packageRow = Form.Row(identity, "Package name", Source.Unity);
            if (UnityCompat.CanOverridePackage)
            {
                Toggle custom = form.Check(packageRow, "Override", () => UnityCompat.OverridePackageName, v => UnityCompat.OverridePackageName = v);
                custom.tooltip = "Override Default Package Name: otherwise Unity uses com.{company}.{product}";
            }
            TextField package = form.TextIn(packageRow, () => PlayerSettings.GetApplicationIdentifier(UnityCompat.Android),
                v => PlayerSettings.SetApplicationIdentifier(UnityCompat.Android, v.Trim()));
            form.OnRefresh(() => package.SetEnabled(!UnityCompat.CanOverridePackage || UnityCompat.OverridePackageName));
            if (!UnityCompat.CanOverridePackage)
                form.Notice(identity, () => "Override Default Package Name could not be read in this Unity version; set it in Player Settings.", "warning", autoHide: true);

            // The format first: it decides what a valid version below looks like.
            VisualElement version = Form.Card(page, "Version");
            form.Popup(version, "Version parts", () => new List<int> { 1, 2, 3 }, () => VersionCode.Parts, v =>
            {
                if (v == VersionCode.Parts) return;
                if (!EditorUtility.DisplayDialog("Change version parts",
                        "Versions with another number of parts give version codes on another scale, which can be lower than codes already on Google Play. Change it?",
                        "Change", "Cancel")) return;
                settings.versionParts = v;
                settings.Save();
            }, v => v == 1 ? "1 part · 12" : v == 2 ? "2 parts · 1.11" : "3 parts · 1.2.3", Source.Tool);
            form.Text(version, "Version", () => PlayerSettings.bundleVersion, v =>
            {
                // The code is only written when a build starts, so trying versions back and forth changes nothing.
                PlayerSettings.bundleVersion = v.Trim();
                versionError = VersionCode.Evaluate(out nextCode);
            }, Source.Unity);
            form.Info(version, "Bundle version code", () =>
            {
                int code = PlayerSettings.Android.bundleVersionCode;
                return $"{code}  ·  build {VersionCode.BuildOf(code):00}";
            }, Source.Unity);
            form.Hint(version, () => versionError == null && nextCode != PlayerSettings.Android.bundleVersionCode
                ? $"The next build uses {nextCode} (version {PlayerSettings.bundleVersion}, build 00)."
                : null);
            form.Hint(version, "Each part after the first takes 2 digits, then a 2-digit build number that goes up after every successful build and restarts at 00 for a new version.");
            form.Notice(version, () => versionError, "error");

            VisualElement icon = Form.Card(page, "Icon");
            VisualElement iconRow = Form.Row(icon, "Default icon", Source.Unity);
            var iconField = new ObjectField { objectType = typeof(Texture2D), allowSceneObjects = false };
            iconField.AddToClassList("abs-control");
            iconField.RegisterValueChangedCallback(e =>
            {
                UnityCompat.DefaultIcon = e.newValue as Texture2D;
                Refresh();
            });
            form.OnRefresh(() => iconField.SetValueWithoutNotify(UnityCompat.DefaultIcon));
            iconRow.Add(iconField);
            form.Notice(icon, () => UnityCompat.HasAndroidIcons
                ? "Android has its own icons in Player Settings > Icon (Adaptive, Round or Legacy), and the app shows those instead of this one."
                : null, "warning");

            VisualElement orientation = Form.Card(page, "Orientation");
            form.Popup(orientation, "Default orientation",
                () => System.Enum.GetValues(typeof(UIOrientation)).Cast<UIOrientation>().ToList(),
                () => PlayerSettings.defaultInterfaceOrientation, v => PlayerSettings.defaultInterfaceOrientation = v,
                v => ObjectNames.NicifyVariableName(v.ToString()), Source.Unity);
            VisualElement allowed = form.Checks(orientation, "Allowed for auto rotation", Source.Unity,
                ("Portrait", () => PlayerSettings.allowedAutorotateToPortrait, v => PlayerSettings.allowedAutorotateToPortrait = v),
                ("Portrait upside down", () => PlayerSettings.allowedAutorotateToPortraitUpsideDown, v => PlayerSettings.allowedAutorotateToPortraitUpsideDown = v),
                ("Landscape left", () => PlayerSettings.allowedAutorotateToLandscapeLeft, v => PlayerSettings.allowedAutorotateToLandscapeLeft = v),
                ("Landscape right", () => PlayerSettings.allowedAutorotateToLandscapeRight, v => PlayerSettings.allowedAutorotateToLandscapeRight = v));
            form.OnRefresh(() => allowed.parent.style.display = PlayerSettings.defaultInterfaceOrientation == UIOrientation.AutoRotation ? DisplayStyle.Flex : DisplayStyle.None);
        }

        // --- Android -----------------------------------------------------------------------------------------

        private void BuildAndroidPage(VisualElement page)
        {
            VisualElement api = Form.Card(page, "API level");
            form.Popup(api, "Minimum API level", () => SdkVersions(false), () => PlayerSettings.Android.minSdkVersion,
                v => PlayerSettings.Android.minSdkVersion = v, v => SdkName(v, false), Source.Unity);
            form.Popup(api, "Target API level", () => SdkVersions(true), () => PlayerSettings.Android.targetSdkVersion,
                v => PlayerSettings.Android.targetSdkVersion = v, v => SdkName(v, true), Source.Unity);

            VisualElement configuration = Form.Card(page, "Configuration");
            form.Popup(configuration, "Scripting backend",
                () => new List<ScriptingImplementation> { ScriptingImplementation.Mono2x, ScriptingImplementation.IL2CPP },
                () => PlayerSettings.GetScriptingBackend(UnityCompat.Android),
                SetBackend,
                v => v == ScriptingImplementation.Mono2x ? "Mono" : v.ToString(), Source.Unity);
            form.Popup(configuration, "Managed stripping level",
                () =>
                {
                    var levels = new List<ManagedStrippingLevel> { ManagedStrippingLevel.Minimal, ManagedStrippingLevel.Low, ManagedStrippingLevel.Medium, ManagedStrippingLevel.High };
                    if (PlayerSettings.GetScriptingBackend(UnityCompat.Android) == ScriptingImplementation.Mono2x) levels.Insert(0, ManagedStrippingLevel.Disabled);
                    return levels;
                },
                () => PlayerSettings.GetManagedStrippingLevel(UnityCompat.Android),
                v => PlayerSettings.SetManagedStrippingLevel(UnityCompat.Android, v),
                v => v.ToString(), Source.Unity);

            // Architectures the backend cannot build are disabled, and the last one ticked cannot be unticked, so the
            // selection is always one Unity can build.
            VisualElement architectures = Form.Grid(configuration, "Target architectures", Source.Unity);
            foreach (var (flag, text) in new[]
                     {
                         (AndroidArchitecture.ARMv7, "ARMv7"), (AndroidArchitecture.ARM64, "ARM64"),
                         (X86, "x86 (ChromeOS)"), (AndroidArchitecture.X86_64, "x86-64 (ChromeOS)")
                     })
            {
                if (flag == X86 && !X86Supported) continue;
                Toggle box = form.Cell(architectures, text,
                    () => (PlayerSettings.Android.targetArchitectures & flag) != 0,
                    v => PlayerSettings.Android.targetArchitectures = v ? PlayerSettings.Android.targetArchitectures | flag : PlayerSettings.Android.targetArchitectures & ~flag);
                form.OnRefresh(() =>
                {
                    AndroidArchitecture selected = PlayerSettings.Android.targetArchitectures;
                    bool supported = (SupportedArchitectures & flag) != 0;
                    bool onlyOne = selected == flag;
                    box.SetEnabled(supported && !onlyOne);
                    box.tooltip = !supported ? "Mono builds ARMv7 only; use IL2CPP for this architecture." : onlyOne ? "At least one architecture is needed." : null;
                });
            }
            form.Hint(configuration, () =>
            {
                bool il2cpp = PlayerSettings.GetScriptingBackend(UnityCompat.Android) == ScriptingImplementation.IL2CPP;
                return il2cpp && (PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64) != 0
                    ? null
                    : "Google Play requires 64-bit: release builds need IL2CPP with ARM64.";
            });

            VisualElement symbols = Form.Card(page, "Scripting define symbols");
            form.StringList(symbols, "Android",
                () =>
                {
                    PlayerSettings.GetScriptingDefineSymbols(UnityCompat.Android, out string[] defines);
                    return defines.ToList();
                },
                list => PlayerSettings.SetScriptingDefineSymbols(UnityCompat.Android, list.ToArray()),
                ValidateSymbol, IsRemovedInRelease, Source.Unity, "a symbol");
            form.Hint(symbols, () => BuildSupporterSettings.instance.symbolsToRemove.Count > 0 ? "Highlighted symbols are removed from the release build." : null);
            form.Notice(symbols, () =>
            {
                string profile = UnityCompat.ActiveProfileName;
                if (profile == null) return null;
                string[] defines = UnityCompat.ProfileDefines;
                return $"Build Profile '{profile}' is active" + (defines.Length > 0 ? " and adds " + string.Join(", ", defines) : "") +
                       ". Release builds remove the symbols from it too.";
            }, "info", autoHide: true);
            form.Notice(symbols, () => UnityCompat.ProfileOverridesPlayerSettings
                ? $"Build Profile '{UnityCompat.ActiveProfileName}' overrides Player Settings. This window edits the global Player Settings, not the profile's."
                : null, "warning");

            BuildSigningCard(page);
        }

        private void BuildSigningCard(VisualElement page)
        {
            VisualElement signing = Form.Card(page, "Signing", "Passwords are checked with keytool as soon as you enter them, and again before every build.");
            form.Toggle(signing, "Use custom keystore", () => PlayerSettings.Android.useCustomKeystore, v => PlayerSettings.Android.useCustomKeystore = v, Source.Unity);
            var fields = new VisualElement();
            signing.Add(fields);
            form.OnRefresh(() => fields.SetEnabled(PlayerSettings.Android.useCustomKeystore));

            TextField path = form.Text(fields, "Keystore", () => PlayerSettings.Android.keystoreName, v =>
            {
                PlayerSettings.Android.keystoreName = v.Trim();
                LoadAliasesAsync(true);
            }, Source.Unity);
            Form.Button(path.parent, "Browse", () =>
            {
                string current = Keystore.Path;
                string folder = !string.IsNullOrEmpty(current) && File.Exists(current) ? Path.GetDirectoryName(current) : AndroidTools.ProjectRoot;
                string chosen = EditorUtility.OpenFilePanel("Select keystore", folder, "keystore,jks");
                if (string.IsNullOrEmpty(chosen)) return;
                PlayerSettings.Android.keystoreName = AndroidTools.ProjectRelative(chosen);
                LoadAliasesAsync(true);
            });

            form.Text(fields, "Keystore password", () => PlayerSettings.Android.keystorePass, v =>
            {
                PlayerSettings.Android.keystorePass = v;
                LoadAliasesAsync(true);
            }, Source.Unity, true);

            // Read from the keystore like Unity's own Alias drop-down; the current alias stays listed even when it
            // is not in the keystore, so nothing changes until the user picks one.
            form.Popup(fields, "Key alias", () => aliases.ToList(), () => PlayerSettings.Android.keyaliasName ?? "", v =>
            {
                PlayerSettings.Android.keyaliasName = v;
                CheckAliasPasswordAsync();
            }, v => string.IsNullOrEmpty(v) ? "Select…" : aliases.Contains(v) || aliases.Count == 0 ? v : v + "  (not in this keystore)", Source.Unity);
            form.Hint(fields, () =>
                loadingAliases ? "Reading aliases…" :
                aliasError != null ? null :
                string.IsNullOrEmpty(PlayerSettings.Android.keystorePass) ? "Enter the keystore password to list its aliases." :
                !File.Exists(Keystore.Path) ? null :
                aliases.Count == 0 ? "This keystore has no private key." : null);
            form.Notice(fields, () => aliasError, "error");
            // Checked as soon as the field is left, like the keystore password is when the aliases are read.
            form.Text(fields, "Alias password", () => PlayerSettings.Android.keyaliasPass, v =>
            {
                PlayerSettings.Android.keyaliasPass = v;
                CheckAliasPasswordAsync();
            }, Source.Unity, true);
            form.Hint(fields, () => checkingAliasPass ? "Checking the alias password…" : null);
            form.Notice(fields, () => aliasPassError, "error");
            form.Hint(signing, "Passwords are also saved in UserSettings/ as plain text, so they survive an Editor restart.");
            form.Notice(signing, SecretsWarning, "error");
        }

        /// <summary>
        /// Lists the keystore's aliases. After the user changed the keystore or its password here, a keystore with
        /// one key selects it; on opening the window, or after a change in Project Settings, nothing is selected.
        /// </summary>
        private async void LoadAliasesAsync(bool userChange)
        {
            aliasSource = AliasSource;
            int request = ++aliasRequest;
            loadingAliases = true;
            Refresh();
            var (found, error) = await Keystore.ListAliasesAsync();
            if (request != aliasRequest) return;
            loadingAliases = false;
            aliases = found;
            aliasError = error;
            string current = PlayerSettings.Android.keyaliasName;
            if (userChange && found.Count > 0 && !found.Contains(current))
                PlayerSettings.Android.keyaliasName = found.Count == 1 ? found[0] : "";
            CheckAliasPasswordAsync();
        }

        /// <summary>
        /// Checks the alias password once the keystore has opened and the alias is one of its keys; an empty field
        /// or a keystore problem already shown above is not reported twice. Only errors are shown.
        /// </summary>
        private async void CheckAliasPasswordAsync()
        {
            aliasPassSource = AliasPassSource;
            int request = ++aliasPassRequest;
            bool ready = !loadingAliases && aliasError == null &&
                         aliases.Contains(PlayerSettings.Android.keyaliasName ?? "") &&
                         !string.IsNullOrEmpty(PlayerSettings.Android.keyaliasPass);
            if (!ready)
            {
                checkingAliasPass = false;
                aliasPassError = null;
                Refresh();
                return;
            }

            checkingAliasPass = true;
            Refresh();
            string error = await Keystore.CheckAliasPasswordAsync();
            if (request != aliasPassRequest) return;
            checkingAliasPass = false;
            aliasPassError = error;
            Refresh();
        }

        // --- Build -------------------------------------------------------------------------------------------

        private void BuildBuildPage(VisualElement page)
        {
            BuildSupporterSettings settings = BuildSupporterSettings.instance;
            LocalSettings local = LocalSettings.instance;

            VisualElement output = Form.Card(page, "Output");
            form.Popup(output, "Compression method",
                () => new List<CompressionMethod> { CompressionMethod.Default, CompressionMethod.LZ4, CompressionMethod.LZ4HC },
                () => UnityCompat.Compression, v => UnityCompat.Compression = v, v => v.ToString(),
                UnityCompat.CanSyncCompression ? Source.Unity : Source.Tool);
            if (!UnityCompat.CanSyncCompression)
                form.Notice(output, () => "Compression Method could not be read in this Unity version, so the tool keeps its own value.", "warning", autoHide: true);

            TextField folder = form.Text(output, "Output folder", () => settings.outputFolder, v => SaveSetting(() => settings.outputFolder = v.Trim()), Source.Tool);
            Form.Button(folder.parent, "Browse", () =>
            {
                string chosen = EditorUtility.OpenFolderPanel("Output folder", AndroidTools.ProjectRoot, "");
                if (string.IsNullOrEmpty(chosen)) return;
                SaveSetting(() => settings.outputFolder = AndroidTools.ProjectRelative(chosen));
                Refresh();
            });
            form.Text(output, "File name", () => settings.fileName, v => SaveSetting(() => settings.fileName = v.Trim()), Source.Tool);
            form.Hint(output, () => $"Next QA build: {AndroidTools.ProjectRelative(Path.Combine(Naming.OutputFolder(BuildVariant.QA, DateTime.Now), Naming.FileName(BuildVariant.QA, DateTime.Now) + ".apk"))}");
            form.Hint(output, "Placeholders: " + Naming.Placeholders);

            BuildDeviceCards(page, local);

            VisualElement release = Form.Card(page, "Release",
                "QA builds an APK with every symbol. Release builds an AAB without the symbols below, then a universal APK from that AAB, which is the one installed.");
            form.StringList(release, "Symbols to remove", () => settings.symbolsToRemove.ToList(),
                list => SaveSetting(() => settings.symbolsToRemove = list), ValidateSymbol, null, Source.Tool, "a symbol");
            form.StringList(release, "Forbidden assemblies", () => settings.forbiddenAssemblies.ToList(),
                list => SaveSetting(() => settings.forbiddenAssemblies = list),
                v => v.IndexOfAny(new[] { ' ', '/', '\\' }) >= 0 ? "Enter an assembly name such as Company.DebugTools." : null, null, Source.Tool, "an assembly name");
            form.Hint(release, () => settings.forbiddenAssemblies.Count > 0 ? "The release build fails if one of these assemblies is compiled into it." : null);

            // Shows the Editor's own jar until another one is chosen; choosing that same jar, or clearing the field,
            // goes back to following the Editor.
            TextField bundletool = form.Text(release, "bundletool",
                () => string.IsNullOrEmpty(local.bundletoolPath) ? AndroidTools.EditorBundletool ?? "" : local.bundletoolPath,
                v => SetBundletool(local, v), Source.Local);
            Form.Button(bundletool.parent, "Browse", () =>
            {
                string current = AndroidTools.Bundletool;
                string chosen = EditorUtility.OpenFilePanel("Select bundletool", current == null ? "" : Path.GetDirectoryName(current), "jar");
                if (string.IsNullOrEmpty(chosen)) return;
                SetBundletool(local, chosen);
                Refresh();
            });
            Button reset = Form.Button(bundletool.parent, "Default", () =>
            {
                SetBundletool(local, "");
                Refresh();
            }, "Use the bundletool that ships with this Editor");
            form.OnRefresh(() => reset.style.display = string.IsNullOrEmpty(local.bundletoolPath) ? DisplayStyle.None : DisplayStyle.Flex);
            form.Hint(release, () => AndroidTools.Bundletool == null ? null :
                string.IsNullOrEmpty(local.bundletoolPath) ? "The bundletool that ships with this Editor." : "A bundletool you chose, on this machine only.");
            form.Notice(release, () => AndroidTools.Bundletool != null ? null :
                string.IsNullOrEmpty(local.bundletoolPath) ? "bundletool not found in the Android module. Select a bundletool .jar." : "That bundletool file does not exist.", "error");
        }

        private void BuildDeviceCards(VisualElement page, LocalSettings local)
        {
            VisualElement install = Form.Card(page, "Install on device", "Used when Install & run is on: the APK is installed with adb and the app is opened.");
            PopupField<string> device = form.Popup(install, "Device", () =>
            {
                var choices = new List<string> { "*" };
                choices.AddRange(devices.Select(d => d.Serial));
                return choices;
            }, () => string.IsNullOrEmpty(local.installTarget) ? "*" : local.installTarget, v =>
            {
                local.installTarget = v;
                local.Save();
            }, DeviceLabel, Source.Local);
            Button refresh = Form.Button(device.parent, "Refresh", ListDevicesAsync, "List connected devices again");
            form.OnRefresh(() => refresh.SetEnabled(!adbBusy));
            form.Notice(install, () => AndroidTools.Adb == null ? "adb not found in the Android SDK the Editor uses." : null, "warning");

            VisualElement wireless = Form.Card(page, "Wireless debugging", "The device and this computer must be on the same Wi-Fi network.");

            TextField address = form.Text(wireless, "Device address", () => local.wirelessAddress, v =>
            {
                local.wirelessAddress = v.Trim();
                local.Save();
            }, Source.Local);
            address.tooltip = "IP:port shown under Developer options > Wireless debugging, or IP:5555 after Switch to Wi-Fi";
            Button connect = Form.Button(address.parent, "Connect", () => RunAdb(async () =>
            {
                string target = Adb.NormalizeAddress(local.wirelessAddress);
                string error = await Adb.ConnectAsync(target);
                if (error != null) return (error, "error");
                local.wirelessAddress = target;
                local.installTarget = target;
                local.Save();
                return ($"Connected to {target}. It is now the install device.", "ok");
            }));
            Button disconnect = Form.Button(address.parent, "Disconnect", () => RunAdb(async () =>
            {
                await Adb.DisconnectAsync(local.wirelessAddress);
                return ("Disconnected.", "info");
            }));

            VisualElement pair = Form.Row(wireless, "Pair (Android 11+)", null,
                "Developer options > Wireless debugging > Pair device with pairing code: enter the IP:port and the 6-digit code it shows");
            TextField pairField = form.TextIn(pair, () => pairAddress, v => pairAddress = v.Trim());
            pairField.tooltip = "IP:port of the pairing dialog (not the connect port)";
            TextField codeField = form.TextIn(pair, () => pairCode, v => pairCode = v.Trim());
            codeField.tooltip = "6-digit pairing code";
            codeField.AddToClassList("abs-code");
            Button pairButton = Form.Button(pair, "Pair", () => RunAdb(async () =>
            {
                string error = await Adb.PairAsync(pairAddress, pairCode);
                pairCode = "";
                return error != null
                    ? (error, "error")
                    : ("Paired. Now enter the IP:port shown on the Wireless debugging screen above and press Connect.", "ok");
            }));
            form.Hint(wireless, "Pairing IP:port and 6-digit code from Wireless debugging > Pair device with pairing code.");

            VisualElement usb = Form.Row(wireless, "From USB", null, "Android 10 and older: switch a device plugged in by USB to Wi-Fi");
            Button switchButton = Form.Button(usb, "Switch selected USB device to Wi-Fi", () => RunAdb(async () =>
            {
                var (connected, error) = await Adb.SwitchToWifiAsync(local.installTarget);
                if (error != null) return (error, "error");
                local.wirelessAddress = connected;
                local.installTarget = connected;
                local.Save();
                return ($"Connected to {connected} over Wi-Fi. The cable can be unplugged.", "ok");
            }));
            switchButton.AddToClassList("abs-button-first");

            form.OnRefresh(() =>
            {
                bool ready = !adbBusy && AndroidTools.Adb != null;
                connect.SetEnabled(ready && !string.IsNullOrWhiteSpace(local.wirelessAddress));
                disconnect.SetEnabled(ready && !string.IsNullOrWhiteSpace(local.wirelessAddress));
                pairButton.SetEnabled(ready);
                bool usbSelected = devices.Any(d => d.Serial == local.installTarget && d.Ready && !Adb.IsNetworkSerial(d.Serial));
                switchButton.SetEnabled(ready && usbSelected);
                switchButton.tooltip = usbSelected ? "Runs adb tcpip 5555 and connects over Wi-Fi" : "Select a device connected by USB in Install on device first";
            });
            form.Notice(wireless, () => adbMessage, () => adbMessageKind, autoHide: true);
        }

        /// <summary>Runs one adb action at a time, shows its result, then lists the devices again.</summary>
        private async void RunAdb(Func<Task<(string message, string kind)>> action)
        {
            if (adbBusy) return;
            adbBusy = true;
            adbMessage = "Working…";
            adbMessageKind = "info";
            Refresh();
            try
            {
                (adbMessage, adbMessageKind) = await action();
                devices = await Adb.DevicesAsync();
            }
            catch (Exception e)
            {
                adbMessage = e.Message;
                adbMessageKind = "error";
            }
            finally
            {
                adbBusy = false;
                Refresh();
            }
        }

        private string DeviceLabel(string serial)
        {
            if (string.IsNullOrEmpty(serial) || serial == "*") return "All connected devices";
            AndroidDevice match = devices.FirstOrDefault(d => d.Serial == serial);
            return match.Serial != null ? match.Label + (Adb.IsNetworkSerial(serial) ? "  (Wi-Fi)" : "") : serial + "  (not connected)";
        }

        private async void ListDevicesAsync()
        {
            if (adbBusy) return;
            adbBusy = true;
            Refresh();
            try
            {
                devices = await Adb.DevicesAsync();
            }
            finally
            {
                adbBusy = false;
                Refresh();
            }
        }

        // --- Distribution ------------------------------------------------------------------------------------

        private void BuildDistributionPage(VisualElement page)
        {
            BuildSupporterSettings settings = BuildSupporterSettings.instance;
            LocalSettings local = LocalSettings.instance;

            VisualElement drive = Form.Card(page, "Google Drive", "Builds go into the project's Drive folder, or My Drive, and are shared as anyone with the link can view.");
            // The client first: signing in needs it.
            form.Text(drive, "Client ID", () => local.googleClientId, v =>
            {
                local.googleClientId = v.Trim();
                local.Save();
            }, Source.Local);
            form.Text(drive, "Client secret", () => local.googleClientSecret, v =>
            {
                local.googleClientSecret = v.Trim();
                local.Save();
            }, Source.Local, true);
            form.Hint(drive, () => GoogleAuth.IsSignedIn ? null : "From an OAuth client of type Desktop app in Google Cloud Console, with the Drive API enabled. See the README.");
            Label account = form.Info(drive, "Account", () =>
                GoogleAuth.IsSigningIn ? "Waiting for the browser…" :
                !GoogleAuth.IsSignedIn ? "Not signed in" :
                string.IsNullOrEmpty(local.googleAccount) ? "Signed in" : local.googleAccount, Source.Local);
            Button sign = Form.Button(account.parent, "Sign in", SignInOrOutAsync);
            form.OnRefresh(() =>
            {
                sign.text = !GoogleAuth.IsSignedIn ? "Sign in" : GoogleAuth.HasRequiredScope ? "Sign out" : "Sign in again";
                sign.SetEnabled(!GoogleAuth.IsSigningIn);
                account.EnableInClassList("abs-info-ok", GoogleAuth.HasRequiredScope);
            });
            form.Notice(drive, () => googleMessage, "error", autoHide: true);
            form.Notice(drive, () => GoogleAuth.IsSignedIn && !GoogleAuth.HasRequiredScope
                ? "This account was signed in for My Drive only. A shared project folder needs full Drive access: press Sign in again."
                : null, "warning");

            // The folder the manager created for the project, shared with everyone who builds it: committed, so the
            // whole team uploads to the same place.
            TextField root = form.Text(drive, "Project folder", () => settings.driveRootFolder, v =>
            {
                SaveSetting(() => settings.driveRootFolder = v.Trim());
                CheckDriveRootAsync();
            }, Source.Tool);
            root.tooltip = "Link of the Google Drive folder for this project (Share > Copy link), or empty for My Drive";
            Button openRoot = Form.Button(root.parent, "Open", () => Application.OpenURL("https://drive.google.com/drive/folders/" + GoogleDrive.RootFolderId),
                "Open the folder in the browser");
            form.OnRefresh(() => openRoot.style.display = GoogleDrive.RootFolderId != null ? DisplayStyle.Flex : DisplayStyle.None);
            form.Hint(drive, () =>
                !GoogleDrive.UsesSharedFolder ? "Empty: builds go to My Drive. Paste a folder link to use the project's shared folder." :
                checkingRoot ? "Opening the folder…" :
                rootName != null ? $"Folder: {rootName}" : null);
            form.Notice(drive, () => rootError, "error");
            form.Text(drive, "Subfolder", () => settings.driveFolder, v => SaveSetting(() => settings.driveFolder = v.Trim()), Source.Tool);
            // QA in the left column, both release files under each other in the right one.
            VisualElement upload = Form.Grid(drive, "Upload", Source.Tool);
            form.Cell(upload, "QA APK", () => settings.uploadQaApk, v => SaveSetting(() => settings.uploadQaApk = v));
            form.Cell(upload, "Release AAB", () => settings.uploadReleaseAab, v => SaveSetting(() => settings.uploadReleaseAab = v));
            Form.Gap(upload);
            form.Cell(upload, "Release APK", () => settings.uploadReleaseApk, v => SaveSetting(() => settings.uploadReleaseApk = v));

            VisualElement discord = Form.Card(page, "Discord", "A message after every build; a failed build attaches its log.");
            TextField webhook = form.Text(discord, "Webhook URL", () => local.discordWebhook, v =>
            {
                local.discordWebhook = v.Trim();
                local.Save();
                discordMessage = null;
            }, Source.Local, true);
            Button test = Form.Button(webhook.parent, "Test", TestDiscordAsync, "Send a test message");
            form.OnRefresh(() => test.SetEnabled(Discord.IsWebhook(local.discordWebhook)));
            form.Notice(discord, () => !string.IsNullOrEmpty(local.discordWebhook) && !Discord.IsWebhook(local.discordWebhook)
                ? "This is not a Discord webhook URL (https://discord.com/api/webhooks/…)." : null, "error");
            form.Notice(discord, () => discordMessage, () => discordMessage == "Test message sent." ? "ok" : "error", autoHide: true);
            form.Checks(discord, "Notify on", Source.Tool,
                ("Success", () => settings.notifySuccess, v => SaveSetting(() => settings.notifySuccess = v)),
                ("Failure", () => settings.notifyFailure, v => SaveSetting(() => settings.notifyFailure = v)));
            form.Text(discord, "Mention", () => settings.mentionUserId, v => SaveSetting(() => settings.mentionUserId = v.Trim()), Source.Tool)
                .tooltip = "Discord user IDs separated by commas: Settings > Advanced > Developer Mode, then right-click a user > Copy User ID";
            form.Hint(discord, "User IDs separated by commas; each build message mentions all of them.");
            form.Notice(discord, () =>
            {
                string[] invalid = Discord.MentionIds(settings.mentionUserId).Invalid;
                return invalid.Length == 0 ? null : $"Not a Discord user ID (digits only): {string.Join(", ", invalid)}. These are skipped.";
            }, "error");

            VisualElement storage = Form.Card(page, "Secrets",
                $"Client secret, Google sign-in, webhook and keystore passwords are saved as plain text in {LocalSettings.RelativePath}, on this machine only.");
            form.Notice(storage, SecretsWarning, "error");
        }

        private string SecretsWarning() =>
            secretsNotIgnored ? $"{LocalSettings.RelativePath} is NOT ignored by git. Add /[Uu]ser[Ss]ettings/ to .gitignore before committing." : null;

        private static void SetBundletool(LocalSettings local, string path)
        {
            path = (path ?? "").Trim();
            string editor = AndroidTools.EditorBundletool;
            bool isEditor = path.Length == 0 || (editor != null && File.Exists(path) &&
                                                 string.Equals(Path.GetFullPath(path), editor, StringComparison.OrdinalIgnoreCase));
            local.bundletoolPath = isEditor ? "" : path;
            local.Save();
        }

        private static void SaveSetting(Action change)
        {
            change();
            BuildSupporterSettings.instance.Save();
        }

        private async void SignInOrOutAsync()
        {
            googleMessage = null;
            try
            {
                // Signed in without the access the project folder needs: sign in again rather than out.
                Task task = GoogleAuth.HasRequiredScope ? GoogleAuth.SignOutAsync() : GoogleAuth.SignInAsync();
                Refresh();
                await task;
            }
            catch (Exception e)
            {
                googleMessage = e.Message;
            }
            Refresh();
            CheckDriveRootAsync();
        }

        private string rootName;
        private string rootError;
        private bool checkingRoot;
        private int rootRequest;

        /// <summary>Opens the project folder to show its name, or why this account cannot upload into it.</summary>
        private async void CheckDriveRootAsync()
        {
            int request = ++rootRequest;
            rootName = null;
            rootError = null;
            if (!GoogleDrive.UsesSharedFolder || !GoogleAuth.HasRequiredScope)
            {
                checkingRoot = false;
                rootError = GoogleDrive.UsesSharedFolder && GoogleDrive.RootFolderId == null ? "Paste the link of a Google Drive folder, or its id." : null;
                Refresh();
                return;
            }

            checkingRoot = true;
            Refresh();
            string name, error;
            try
            {
                (name, error) = await GoogleDrive.CheckRootAsync();
            }
            catch (Exception e)
            {
                (name, error) = (null, e.Message);
            }
            if (request != rootRequest) return;
            checkingRoot = false;
            rootName = name;
            rootError = error;
            Refresh();
        }

        private async void TestDiscordAsync()
        {
            discordMessage = null;
            Refresh();
            string error = await Discord.TestAsync();
            discordMessage = error ?? "Test message sent.";
            Refresh();
        }

        /// <summary>Warns when the file holding secrets would be committed. Outside a git repository nothing is said.</summary>
        private async void CheckSecretsIgnoredAsync()
        {
            ProcessResult result = await ProcessRunner.RunAsync("git", new[] { "check-ignore", "-q", LocalSettings.RelativePath },
                timeoutMs: 10000, workingDirectory: AndroidTools.ProjectRoot);
            secretsNotIgnored = result.ExitCode == 1;
            Refresh();
        }

        /// <summary>Mono builds ARMv7 only; IL2CPP builds every architecture.</summary>
        private static AndroidArchitecture SupportedArchitectures =>
            PlayerSettings.GetScriptingBackend(UnityCompat.Android) == ScriptingImplementation.Mono2x ? AndroidArchitecture.ARMv7 : AndroidArchitecture.All;

        /// <summary>
        /// Changes the backend and keeps the dependent settings valid, as Unity's Player Settings do: Mono drops the
        /// architectures it cannot build, IL2CPP cannot run with stripping disabled.
        /// </summary>
        private static void SetBackend(ScriptingImplementation backend)
        {
            PlayerSettings.SetScriptingBackend(UnityCompat.Android, backend);
            if (backend == ScriptingImplementation.Mono2x)
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7;
            else if (PlayerSettings.GetManagedStrippingLevel(UnityCompat.Android) == ManagedStrippingLevel.Disabled)
                PlayerSettings.SetManagedStrippingLevel(UnityCompat.Android, ManagedStrippingLevel.Minimal);
        }

        private static readonly Dictionary<int, string> AndroidNames = new Dictionary<int, string>
        {
            { 19, "4.4" }, { 21, "5.0" }, { 22, "5.1" }, { 23, "6.0" }, { 24, "7.0" }, { 25, "7.1" }, { 26, "8.0" }, { 27, "8.1" },
            { 28, "9" }, { 29, "10" }, { 30, "11" }, { 31, "12" }, { 32, "12L" }, { 33, "13" }, { 34, "14" }, { 35, "15" }, { 36, "16" }
        };

        /// <summary>The levels this Editor knows, without obsolete ones; Automatic only for the target level.</summary>
        private static List<AndroidSdkVersions> SdkVersions(bool target) =>
            System.Enum.GetValues(typeof(AndroidSdkVersions)).Cast<AndroidSdkVersions>()
                .Where(v => typeof(AndroidSdkVersions).GetField(v.ToString())?.IsDefined(typeof(ObsoleteAttribute), false) != true)
                .Where(v => target || v != AndroidSdkVersions.AndroidApiLevelAuto)
                .Distinct()
                .OrderBy(v => (int)v)
                .ToList();

        private static string SdkName(AndroidSdkVersions version, bool target)
        {
            int level = (int)version;
            if (level == 0) return target ? "Automatic (highest installed)" : "Automatic";
            return AndroidNames.TryGetValue(level, out string name) ? $"Android {name}  (API {level})" : $"API {level}";
        }

        private static string ValidateSymbol(string symbol) =>
            SymbolPattern.IsMatch(symbol) ? null : "A symbol starts with a letter or _ and has only letters, digits and _.";

        private static bool IsRemovedInRelease(string symbol) => BuildSupporterSettings.instance.symbolsToRemove.Contains(symbol);
    }
}
