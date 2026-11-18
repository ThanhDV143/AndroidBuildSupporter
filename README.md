# Android Build Supporter

An Android build window for Unity: the settings you edit most in one place, QA and release builds, then install, upload and report.

- **Settings**: app identity, version, icon, orientation, API levels, scripting backend, architectures, stripping, define symbols and signing, read from and written to Unity's own settings.
- **Build QA**: an APK with every scripting define symbol.
- **Build release**: an AAB without the symbols you list, then a universal APK made from that AAB with bundletool.
- **After a build**: install and open the APK with adb (USB or Wi-Fi), upload to Google Drive, report to Discord.
- **History**: the last 100 builds of this machine, to install, upload or report one again.
- **Checks before Unity starts building**: keystore passwords, version code, package name, backend and architectures.

## Installation

Requires Unity 2022.3 or newer and the Android Build Support module.

- **Git URL** (*Package Manager → + → Add package from git URL*):
  `https://github.com/ThanhDV143/AndroidBuildSupporter.git?path=/Packages/thanhdv.androidbuildsupporter`
- **Scoped registry** (*Project Settings → Package Manager*): name `ThanhDVs`, URL `https://upm.thanhdv.com`, scope `thanhdv`; then add `thanhdv.androidbuildsupporter` by name.

Open the window from *Tools → ThanhDV → Android Build Supporter*.

## Where settings live

Each label has a coloured dot:

| Dot | Saved in | Committed |
|---|---|---|
| Blue | Unity's own settings (Player Settings, Build Settings) | Yes |
| Grey | `ProjectSettings/ThanhDV.AndroidBuildSupporter.asset` | Yes |
| Orange | `UserSettings/ThanhDV.AndroidBuildSupporter.asset` | No |

Opening the window never changes Unity's settings; only your edits and a build do.

Secrets (keystore passwords, Google client secret and sign-in, Discord webhook) are saved as plain text in `UserSettings/`, which Unity's `.gitignore` template ignores. The window warns when git does not ignore that file.

## Version code

Each version part after the first takes 2 digits, then a 2-digit build number: `1.2.3` build 0 is `1020300`, `1.11` build 3 is `11103`.

- *Version parts* (App tab, 3 by default) is fixed per project. Every part after the first must be 0–99 without a leading zero.
- The code is written when a build starts: changing the version back and forth without building changes nothing.
- The build number goes up after every successful build and restarts at 00 for a new version.

## Release builds

- *Symbols to remove* (Build tab) are removed from Player Settings and, on Unity 6, from the active Build Profile, then restored after the build even when it fails.
- A release build fails if an assembly listed in *Forbidden assemblies* is compiled into it.
- The keystore and both passwords are checked with keytool as you enter them and again before every build, so a wrong password stops the build at once.

## After a build

Three switches above the build buttons turn each step on. A switch checks its setup when it is turned on and turns itself off, saying why, when something is missing.

- **Install & run**: installs the APK on the device chosen in the Build tab with the adb of the Editor's Android SDK, then opens the app. Install problems are reported as warnings and never fail the build.
- **Upload to Drive**: uploads the files ticked in the Distribution tab.
- **Notify Discord**: posts the result to the webhook in the Distribution tab.

### Installing over Wi-Fi

The device and the computer must be on the same network.

- **Android 11+**: on the device open *Developer options → Wireless debugging → Pair device with pairing code*. Enter that IP:port and code next to *Pair*, then enter the IP:port shown on the Wireless debugging screen as *Device address* and press **Connect**.
- **Any version**: plug the device in by USB, select it as the device, then press **Switch selected USB device to Wi-Fi** (runs `adb tcpip 5555` and connects). The cable can then be unplugged.

The last address is reconnected automatically before each install.

### Google Drive

1. In [Google Cloud Console](https://console.cloud.google.com/), create a project and enable the **Google Drive API**.
2. Configure the OAuth consent screen (External) and **publish it** (*In production*). Apps left in *Testing* get sign-ins that expire after 7 days.
3. Create an OAuth client ID of type **Desktop app**.
4. Enter its Client ID and Client secret in the Distribution tab and press **Sign in**. The window then shows the account's name and email.

Files are uploaded to *Subfolder* (`Builds/{version}` by default, folders are created as needed) and shared as "anyone with the link can view". *Copy links* copies them as `[file name](link)`. Where *Subfolder* starts:

- **Project folder empty**: in My Drive. The tool only asks for the `drive.file` scope, so it sees nothing on Drive but the folders and files it created.
- **Project folder set**: inside the folder whose link is pasted there (*Share → Copy link*), for example the folder a manager created for the project and shared with the team, also on a shared drive. The setting is committed, so the whole team uploads to the same folder. The account needs Editor access to it.
  Opening a folder the tool did not create needs the full `drive` scope, so after setting a project folder press **Sign in again**. Google treats `drive` as a restricted scope: until the OAuth app is verified by Google, sign-in shows an "unverified app" warning and the app is limited to a small number of users, which is fine for a team. If the shared drive forbids public links, the files stay reachable by everyone with access to the folder and the build reports a warning.

### Discord

Create a webhook in the channel settings (*Integrations → Webhooks*) and paste its URL. Every build message mentions the users in *Mention* (Discord user IDs separated by commas: *Settings → Advanced → Developer Mode*, then right-click a user → *Copy User ID*), and failed builds attach the build log.

## History

The History tab lists the last 100 builds made on this machine, newest first; click *Last* in the footer to open the latest one. Filter by QA or release, failed builds only, or version.

Open a build to see its files, links, warnings, error and the slowest build steps, and to:

- **Install again**: install its APK on the device chosen in the Build tab, for example to go back to an older version.
- **Upload again**: upload the files that have no Drive link yet, after a failed upload.
- **Send to Discord**: post its result again.
- **Copy links**, **Open log** (failed builds), **Remove** (optionally deleting its files).

The history is saved in `UserSettings/ThanhDV.AndroidBuildSupporter.History.asset` and logs of failed builds in `Library/ThanhDV.AndroidBuildSupporter/Logs`, both on this machine only: the team shares builds through Drive and Discord.

## License

MIT.
