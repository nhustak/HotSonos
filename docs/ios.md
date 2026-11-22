# HotSonos on iPhone and iPad

iPhone and iPad support is in early development. This is the setup for building the .NET MAUI app for iOS and sending it to TestFlight through Codemagic. The app is `src/HotSonos.Remote`. Android and iOS share the id `com.tangenttech.hotsonos`. The display name is **HotSonos**. The minimum iOS version is 15.0. The binary is universal: iPhone and iPad (`UIDeviceFamily` 1 and 2). The Android build of this same project already runs as a remote for the Windows tray app or standalone on the speakers; this page is only the iOS path.

Nothing in this file or in `codemagic.yaml` is a secret. The App Store Connect `.p8` key, the distribution certificate, and the provisioning profile stay in Codemagic and Apple. Do not commit them.

**iOS compile: UNCHECKED.** The change that added the iOS target was made on Linux. This machine has no Xcode, so `net10.0-ios` has not been compiled. A green GitHub Actions run is the Android and Windows build only. `ios-build-check` on Codemagic is the first compile that counts. Do not treat this document as proof that the iOS project builds.

## What you have to type in

Replace these two placeholders in `codemagic.yaml` before the first TestFlight build. Leave `ios-build-check` alone; it does not use them.

| Placeholder | Where the real value comes from |
| --- | --- |
| `REPLACE_WITH_APP_STORE_CONNECT_INTEGRATION_NAME` | The name you give the API key in Codemagic Team settings, Team integrations, Developer Portal. Copy it exactly, including spaces. |
| `REPLACE_WITH_HOTSONOS_IOS_ENV_GROUP` | The name of the Codemagic environment variable group you create below. |

That environment group needs:

| Variable | Required | Value |
| --- | --- | --- |
| `APP_STORE_APPLE_ID` | Yes | The numeric Apple ID of the HotSonos app record (App Store Connect, the app, App Information). This is not the bundle id. |
| `APPLICATION_DISPLAY_VERSION` | No | Marketing version sent to Apple. Default in the workflow is `1.0.76`. Apple allows one to three integers separated by dots. `Directory.Build.props` is `1.0.0.76`, and that four-part string is not sent to Apple. |
| `FALLBACK_BUILD_NUMBER` | No | Used only when TestFlight has no builds yet. Default is `1`. |

You also need an Apple Distribution certificate and an App Store provisioning profile for `com.tangenttech.hotsonos` in Codemagic code signing identities. The TestFlight workflow loads them with `ios_signing` (`distribution_type: app_store`).

## 1. Renew the Apple Developer membership

TestFlight is not available on a free Apple ID. If the membership has lapsed, renew it before any of the later steps.

1. Sign in at [developer.apple.com/account](https://developer.apple.com/account) with the Apple ID that should own the app.
2. If the membership is expired, renew it from that account page and pay the annual fee.
3. If you have never enrolled, start at [developer.apple.com/programs/enroll](https://developer.apple.com/programs/enroll/). Enroll as an individual, or as the organization that should appear as the seller. An organization enrollment needs a D-U-N-S number and a legal-entity check.
4. Wait until the membership is active. This can take a day for an individual and longer for an organization.
5. Open [App Store Connect](https://appstoreconnect.apple.com/) and accept any pending agreements, including the Paid Applications agreement. TestFlight uploads fail while an agreement is pending, even for a free app. Tax and banking can wait until you charge money, but the agreement itself should be accepted.

## 2. Register the App ID

1. Open [Certificates, Identifiers & Profiles](https://developer.apple.com/account/resources/identifiers/list).
2. Add an identifier, type App IDs, type App.
3. Description: `HotSonos`.
4. Bundle ID: Explicit, `com.tangenttech.hotsonos`.
5. Do not enable Push Notifications. The iOS entitlements file is empty on purpose. A capability on the App ID without a matching entitlement (or the reverse) breaks signing.
6. Do not enable **Multicast Networking** yet. That capability is `com.apple.developer.networking.multicast`. Apple approves it per team (see the next section). Turning it on before the entitlement is in the profile makes the TestFlight sign fail.
7. Register.

## 3. Multicast entitlement (later, after Apple says yes)

Find PC sends a UDP probe on port **42343**. Speaker search sends SSDP to `239.255.255.250:1900`. iOS treats both as multicast. Without the entitlement the calls fail or get no answer. The app does not crash: Connect keeps the address and PIN fields, and the status line tells you to type the address from the PC Phone window. Speaker search says the same and points back at Connect.

There is no `NSBonjourServices` entry. HotSonos does not use mDNS. Do not add one to "make local network work." `NSLocalNetworkUsageDescription` is already set, and `NSAllowsLocalNetworking` allows plain HTTP to the PC (`42342`) and the speakers (`1400`). `NSCameraUsageDescription` is set so Scan QR can use the camera. `CFBundleURLTypes` registers the `hotsonos` scheme so a `hotsonos://pair` link opens the app. Connect can also pair with the password set on the PC. The phone does not save that password. The saved PC address, port, and PIN stay across updates of bundle id `com.tangenttech.hotsonos`. Changing that id wipes the pairing.

When you want Find PC and SSDP to work on a device:

1. Request the entitlement at [developer.apple.com/contact/request/networking-multicast](https://developer.apple.com/contact/request/networking-multicast). Say the app discovers a local UPnP speaker system (SSDP, UDP 1900) and a companion PC (UDP broadcast, port 42343) on the same LAN, and that it does not browse the wider internet.
2. Wait for Apple to approve it. This is a manual review. The app is usable before it lands, by typing the PC address.
3. After approval, edit the App ID and enable Multicast Networking.
4. Add this key to `src/HotSonos.Remote/Platforms/iOS/Entitlements.plist` inside the empty `dict`:

```xml
<key>com.apple.developer.networking.multicast</key>
<true/>
```

5. In Codemagic, fetch a new App Store profile for `com.tangenttech.hotsonos` so the profile contains the capability. The old profile will not sign the new entitlement.
6. Run `ios-testflight` again.

Until step 4, leave the entitlements file empty.

## 4. Create the App Store Connect app record

Codemagic cannot create the first app record for you.

1. In App Store Connect, go to Apps, then the add button, then New App.
2. Platform: iOS. Name: `HotSonos`. Primary language: your choice.
3. Bundle ID: `com.tangenttech.hotsonos`.
4. SKU: any unique string you will not change, for example `hotsonos-remote-ios`.
5. User access: full access, unless you have a reason to limit it.
6. Create the app. You do not need screenshots or a privacy policy URL for the first TestFlight upload. You will need them before App Store review.
7. Open App Information and copy the numeric Apple ID. That number is `APP_STORE_APPLE_ID`.

## 5. Create the App Store Connect API key and add it to Codemagic

1. In App Store Connect, open Users and Access, then Integrations, then App Store Connect API.
2. Generate a team key. Name it something like `Codemagic HotSonos`. Access: **App Manager**. A Developer key can sign, but it cannot upload to TestFlight.
3. Download the `.p8` file. Apple allows that download once. Store a private copy outside the repo.
4. Copy the Issuer ID (above the key list) and the Key ID (on the key row).
5. In Codemagic, open Team settings, then Team integrations, then Developer Portal, then Manage keys.
6. Add key. The name you enter here is the integration name. Put that exact name in `codemagic.yaml` in place of `REPLACE_WITH_APP_STORE_CONNECT_INTEGRATION_NAME`.
7. Enter the Issuer ID and Key ID, upload the `.p8`, and save.
8. Still in Team settings, open codemagic.yaml settings, then Code signing identities.
9. On iOS certificates, generate a certificate. Reference name: anything unique, for example `hotsonos-distribution`. Type: Apple Distribution. Select the API key you just added. Apple allows only three active Apple Distribution certificates on the team. If generation fails because the team is at the limit, revoke an unused one or upload an existing `.p12` that you have the private key for.
10. On iOS provisioning profiles, fetch or create an App Store profile whose bundle id is `com.tangenttech.hotsonos`. It must include the distribution certificate from the previous step. Give it a reference name and save it. `ios_signing` in the workflow selects this profile by distribution type and bundle id, not by the reference name.

## 6. Connect the HotSonos repo

1. In Codemagic, add an application. Choose the team that holds the API key.
2. Connect GitHub if it is not connected yet, and authorize the HotSonos repository (`nhustak/HotSonos`).
3. Select the repository. Project type: codemagic.yaml (or "other", if that is how the UI lists a repo that already has `codemagic.yaml`).
4. Finish adding the app.
5. On the app page, create the webhook (right side, Webhooks). Without the webhook, pull requests will not start `ios-build-check`.
6. Check for configuration file, and point the scan at the branch that contains `codemagic.yaml`. You should see `ios-testflight` and `ios-build-check`.
7. Create an environment variable group for the team. The group name replaces `REPLACE_WITH_HOTSONOS_IOS_ENV_GROUP`. Add `APP_STORE_APPLE_ID`. Mark the group available to this app. Do not put the `.p8` contents in the group; the integration already stores the key.

## 7. Which workflow to run first

Run **`ios-build-check` first**. It is the pull-request workflow. It installs .NET 10 and the `maui-ios` workload, restores only `HotSonos.Remote.csproj` for `net10.0-ios`, and publishes an unsigned iOS simulator build (`iossimulator-arm64`, no signing). It does not read the API key, the env group, or any certificate. A green run means the iOS project compiles. It does not mean the app is signed or that TestFlight accepted it.

After the check is green and the placeholders, API key, certificate, profile, and `APP_STORE_APPLE_ID` are in place, start **`ios-testflight`** manually on that branch, or merge to `master`. That workflow signs an `ios-arm64` IPA (`BuildIpa`), sets the build number to one higher than the latest TestFlight build (`app-store-connect get-latest-testflight-build-number` plus 1), and publishes with `auth: integration` and `submit_to_testflight: true`.

Pushes to `master` also start `ios-testflight`, but only when the commit touches `src/HotSonos.Remote/**`, `src/HotSonos.Remote.Protocol/**`, or `codemagic.yaml`. A signed upload from an unfinished branch is how you burn build numbers. The Windows app, the installer, and a Core-only commit do not start it. Core and Protocol are still compiled when a watched path changes, because the remote project references them.

The first build after a `when.changeset` change always runs, even if the commit is outside those paths. Skipping starts on the next build.

GitHub Actions is unchanged: the phone job is `dotnet build src/HotSonos.Remote/HotSonos.Remote.csproj -c Release -f net10.0-android` on Ubuntu. It does not install the iOS workload.

## 8. Add TestFlight testers

1. Wait until App Store Connect shows the build as processing, then ready to test. The first processing pass often takes several minutes after Codemagic says the upload succeeded.
2. If App Store Connect asks about encryption, `Info.plist` already sets `ITSAppUsesNonExemptEncryption` to false (LAN HTTP and no custom crypto). You should not get the question. If you do, answer that the app does not use non-exempt encryption.
3. Internal testers: Users and Access, then the user, then add them on the app under TestFlight, Internal Testing. Internal testers must be App Store Connect users on the team. They can install without a beta review.
4. External testers: TestFlight, add a group, add email addresses. The first build you give an external group needs a short Beta App Review (a description of what to test, and a contact email). Later builds to the same group usually skip that review.
5. Each tester installs the TestFlight app and accepts the email invite. The build is the same binary for iPhone and iPad.

## iPad layout

Android is not part of this layout. The iOS-only code is compiled out of the Android build.

- **Player.** On iPad the Player column is centered and capped at 520 points. Album art still uses the same rule as the phone (about 46% of the height, clamped from 160 to 420) measured against that column. iPhone stays full width. Side margins use the same background as the page.
- **Tabs.** The eight tabs are the app's own strip (Player, Now, Play, Queue, Library, Rooms, Reports, Settings), not the platform bar. On iPad they fit and are centered. On iPhone they stay in a horizontal scroller, aligned to the start, and the selected tab is scrolled into view.
- **Safe area.** iOS pages use the container safe area, so the tab bar and Connect sit above the home indicator and below the status bar. Android does not set that property.
- **Orientations.** iPhone: portrait, landscape left, landscape right. iPad: those three plus upside down.
- **Other screens.** Now, Play, Queue, Library, Rooms, Reports, Settings, and Connect stay full width and scroll. They are the same views as the phone.
- **Volume keys.** The on-screen − and + are the volume control. Hardware volume keys are wired on Android only.
- **adb.** Wireless debugging in the PC Phone window is the Android install path. It is not used for iOS.

## Common failures

- **Integration name does not match.** Codemagic reports that the App Store Connect integration is missing. The yaml value must be the integration name, character for character.
- **`APP_STORE_APPLE_ID` missing or not numeric.** The build-number step stops on purpose. The bundle id will not work here. A wrong number makes `get-latest-testflight-build-number` fail or read a different app.
- **No app record yet.** The IPA can build and then App Store Connect rejects the upload. Create the app in section 4 first.
- **Agreement pending.** App Store Connect returns an error about agreements or a role that cannot upload. Accept the agreements and confirm the key is App Manager.
- **Certificate limit.** Apple's "You already have a current Distribution certificate" means the team has three. Reuse an existing certificate you can export, or revoke one that is unused.
- **Profile bundle id does not match.** `ios_signing` fetches profiles for `com.tangenttech.hotsonos` only. A profile for a different id, or a development profile, is ignored and the keychain step exits because it cannot find a profile.
- **Multicast capability mismatch.** Do not enable Multicast Networking on the App ID unless `Entitlements.plist` contains `com.apple.developer.networking.multicast` and the profile was fetched after that. The file is empty until Apple approves the request.
- **Marketing version rejected.** `APPLICATION_DISPLAY_VERSION` must look like `1.0.76` or `1.0.0`, not `1.0.0.76`.
- **Build number already used.** The workflow adds one to the latest TestFlight build. Uploading the same number again fails. Re-run the workflow rather than reusing an IPA.
- **Export compliance prompt.** `Info.plist` sets `ITSAppUsesNonExemptEncryption` to false. If that key is removed, TestFlight holds the build until someone answers the encryption question in App Store Connect.
- **Windows solution pulled into the build.** Both workflows set `working_directory` to `src/HotSonos.Remote` and publish `HotSonos.Remote.csproj` with `-f net10.0-ios`. Restoring `HotSonos.slnx` builds the WPF tray app and fails on the Mac. Do not "simplify" the scripts to a solution build.
- **iOS build on Windows or Linux.** The remote csproj targets `net10.0-ios` only on macOS, or when the build passes `-p:HotSonosRemoteTargetFrameworks=net10.0-ios` / `-p:IncludeiOS=true`. A Windows or Ubuntu `dotnet build -f net10.0-android` stays on Android. Passing `-p:TargetFrameworks` instead of `-p:HotSonosRemoteTargetFrameworks` would try to retarget Protocol and Core. Do not do that.
- **Find PC finds nothing on a phone.** Expected until the multicast entitlement is approved, and also expected if Local Network access was denied. Type the address and the 6-digit PIN from the PC Phone window. Allow Local Network if iOS asks.

## What Codemagic builds

| Path | What it is | What Codemagic does with it |
| --- | --- | --- |
| `src/HotSonos.App` and the rest of `HotSonos.slnx` | Windows tray app, installer, tests. | Not restored as a solution, not published. |
| `src/HotSonos.Remote` | MAUI app. Android and iOS. | Working directory. Project file `HotSonos.Remote.csproj`. |
| `src/HotSonos.Remote.Protocol` | Shared phone DTOs. `net10.0`. | Compiled only as a reference of that csproj. |
| `src/HotSonos.Core` | UPnP client. `net10.0`. | Compiled only as a reference of that csproj. A Core-only commit does not start the workflow. |
| `codemagic.yaml` | These two workflows. | A change here is part of the watched changeset. |

Restore and publish both pass `-f net10.0-ios` and `-p:HotSonosRemoteTargetFrameworks=net10.0-ios`. There is no solution file on the command line.
