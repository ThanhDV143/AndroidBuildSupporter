# Android Build Supporter

Editor window for Unity 2022.3 to Unity 6 that keeps the Android settings you edit most in one place and builds:

- **Build QA**: an APK with every scripting define symbol.
- **Build release**: an AAB without the symbols listed in *Symbols to remove*, then a universal APK made from that AAB with bundletool.

After a build the APK is installed with adb, the files are uploaded to Google Drive and a report is sent to Discord.

Open it from **Tools > ThanhDV > Android Build Supporter**.

## Where settings live

Each label has a coloured mark:

| Mark | Saved in | Committed |
|---|---|---|
| Blue | Unity's own settings (Player Settings, Build Settings) | Yes |
| Grey | `ProjectSettings/ThanhDV.AndroidBuildSupporter.asset` | Yes |
| Orange | `UserSettings/ThanhDV.AndroidBuildSupporter.asset` | No |

Secrets (keystore passwords, Google client secret and sign-in, Discord webhook) are saved as plain text in `UserSettings/`, which Unity's `.gitignore` template ignores. The window warns when git does not ignore that file.

## Version code

`version parts packed in 2 digits each + a 2-digit build number`: `1.11` build 3 is `11103`, `1.2.3` build 0 is `1020300`.
The number of version parts is fixed per project (Build tab), every part after the first must be 0–99 without a leading zero, and the build number goes up after every successful build.

## Release safety

- Symbols are removed from Player Settings and, on Unity 6, from the active Build Profile, and restored after the build even if it fails.
- A release build fails if an assembly listed in *Forbidden assemblies* is compiled into it.
- Before building, the keystore and both passwords are checked with keytool, so a wrong password stops the build at once.

## Installing on a device

Choose the device in the Build tab; the APK is installed with the adb of the Editor's Android SDK and the app is opened. Install problems are reported as warnings and never fail the build.

Over Wi-Fi (the device and the computer on the same network):

- **Android 11+**: on the device open Developer options > Wireless debugging > Pair device with pairing code. Enter that IP:port and code next to *Pair*, then enter the IP:port shown on the Wireless debugging screen as *Device address* and press **Connect**.
- **Any version**: plug the device in by USB, select it as the device, then press **Switch selected USB device to Wi-Fi** (runs `adb tcpip 5555` and connects). The cable can then be unplugged.

The last address is reconnected automatically before each install.

## Google Drive setup

1. In [Google Cloud Console](https://console.cloud.google.com/), create a project and enable the **Google Drive API**.
2. Configure the OAuth consent screen (External) and **publish it** (*In production*). Apps left in *Testing* get sign-ins that expire after 7 days. The tool only asks for the `drive.file` scope, so it can only see the folders and files it creates.
3. Create an OAuth client ID of type **Desktop app**.
4. Enter its Client ID and Client secret in the Distribution tab and press **Sign in**.

Files are uploaded to *Subfolder* (folders are created as needed) and shared as "anyone with the link can view". Where *Subfolder* starts:

- **Project folder empty**: in My Drive. The tool only asks for the `drive.file` scope, so it sees nothing on Drive but the folders and files it created.
- **Project folder set**: inside the folder whose link is pasted there (Share > Copy link), for example the folder a manager created for the project and shared with the team, also on a shared drive. The setting is committed, so the whole team uploads to the same folder. The account needs Editor access to it.
  Opening a folder the tool did not create needs the full `drive` scope, so after setting a project folder press **Sign in again**. Google treats `drive` as a restricted scope: until the OAuth app is verified by Google, sign-in shows an "unverified app" warning and the app is limited to a small number of users, which is fine for a team. If the shared drive forbids public links, the files stay reachable by everyone with access to the folder and the build reports a warning.

## Discord

Create a webhook in the channel settings (Integrations > Webhooks) and paste its URL. Every build message can mention users by ID (several separated by commas), and failed builds attach the build log.
