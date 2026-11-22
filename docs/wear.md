# Wear OS companion

HotSonos on a Pixel Watch (Wear OS) is a small screen: the current title and artist, play/pause, previous, next, and volume. The rotary crown changes volume by the same step as the phone volume buttons (2).

The watch does not use the PIN, the PC address, or speaker discovery. It sends commands to the phone over the Wearable Data Layer (`MessageClient`, path `/hotsonos/v1`). The phone app forwards them to the PC client or the standalone speaker engine, the same path as the Player buttons, and sends the polled now-playing snapshot back.

.NET MAUI has no Wear OS target, so the companion is Kotlin and Compose for Wear OS in `src/HotSonos.Wear`. It uses the same application id as the phone, `com.tangenttech.hotsonos`, which is what the Data Layer requires. The two packages are installed on different devices. A watch and a phone cannot both have that id on one device.

The phone's built-in media controls (the system session from Settings → **Show media controls (watch, lock screen)**) are separate. That switch does not turn this companion off. Opening the HotSonos tile on the watch uses this app.

## Signing

The Data Layer only delivers messages when the phone and the watch are signed with the same certificate, not only the same application id.

Both debug builds use `src/HotSonos.Wear/debug.keystore` (alias `androiddebugkey`, store and key password `android`). That file is in the repo so a CI watch APK matches a local Debug phone build. It is a debug key, not the Play upload key.

A phone already installed with a different signature has to be uninstalled first. A Release phone signed with the Play upload key does not talk to this debug watch.

## Build

From the repo root, with JDK 17 or later and an Android SDK that has platform 36:

```bash
cd src/HotSonos.Wear
./gradlew assembleDebug
```

The signed debug APK is:

```text
src/HotSonos.Wear/app/build/outputs/apk/debug/app-debug.apk
```

GitHub Actions job `wear` runs that command and uploads the APK as `HotSonos-wear-debug`.

The phone Debug APK, signed with the same key, is:

```text
src/HotSonos.Remote/bin/Debug/net10.0-android/com.tangenttech.hotsonos-Signed.apk
```

```bash
dotnet build src/HotSonos.Remote/HotSonos.Remote.csproj -c Debug -f net10.0-android
```

## Install on the watch over Wi-Fi

The watch and the computer must be on the same Wi-Fi. The phone running HotSonos must be paired with the watch in the Wear OS app (the normal Bluetooth companion pair). No HotSonos PIN is entered on the watch.

1. On the watch, open **Settings → System → About** and tap the build number until developer options are on.
2. Open **Settings → Developer options**. Turn on **ADB debugging** and **Debug over Wi-Fi**.
3. The watch shows its IP address and a connection port, and a pairing code with a pairing port.
4. On the computer:

```bash
adb pair WATCH_IP:PAIR_PORT
adb connect WATCH_IP:CONNECT_PORT
adb install -r src/HotSonos.Wear/app/build/outputs/apk/debug/app-debug.apk
```

`adb pair` asks for the pairing code shown on the watch. After a later reboot, `adb connect` to the connection port is enough until the watch drops the wireless debugging session.

5. Install the Debug phone build (same keystore) and open HotSonos on the phone. Open HotSonos on the watch. The title replaces "Open HotSonos on the phone" once the phone answers. Play, pause, skip, and volume then follow the active room.

If the watch stays on the hint, confirm the phone app is in the foreground or was opened so its process is running, and that both APKs were signed with `debug.keystore`. A Release upload-key phone will not answer this watch.
