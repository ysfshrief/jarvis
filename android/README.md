# JARVIS companion for Android

A small native app (Kotlin, Android 10+) that pairs with JARVIS on your PC over your local network.

- **Pairing:** scan the QR code from JARVIS → Settings → Devices → *Show pairing code* (or type the address,
  code and fingerprint). The app trusts exactly the certificate whose SHA-256 fingerprint the PC showed —
  no certificate authority, no cloud.
- **Inside the app:** the companion page served by your PC — today's briefing, approvals, alerts and chat.
- **Notifications:** new alerts every ~15 minutes (Android's minimum for background work). Approvals expire
  after three minutes on the PC, so turn on **Stay connected** in the menu if you want them on your phone in
  time: it checks every 15 seconds and Android shows a permanent notification while it's on.
  Approve/Refuse buttons on notifications require unlocking the phone (Android 12+).
- **Your token** is encrypted with an Android Keystore key and excluded from backups. *Unpair* forgets it on
  the phone; remove the phone on the PC to revoke it there.
- Works only when the phone can reach the PC (same Wi-Fi, or your own VPN). There is no relay server.

## Build

CI builds a debug-signed APK on every push (artifact `JARVIS-companion-android`). Locally, with JDK 17 and
the Android SDK:

```
cd android
gradle testDebugUnitTest assembleDebug     # Gradle 8.9+; output: app/build/outputs/apk/debug/app-debug.apk
```

Install it with `adb install app-debug.apk` or by opening the file on the phone (allow installing from that
source when Android asks).
