# Changelog

## [Unreleased]

First version of EmuDroid.

- Setup wizard: checks virtualization, RAM and disk space, turns on Windows Hypervisor Platform, shows BIOS steps when needed.
- Downloads the official Android Emulator, platform tools and Google Play system images directly from Google, with resume and checksum checks.
- Android 12, 12L, 13, 14, 15 and 16 with Google Play (Android 14 by default).
- Instances: create, start, stop, clone, rename, factory reset, delete. Several can run at the same time.
- Install apps by dragging `.apk`, `.apks` or `.xapk` files onto a running instance.
- Keyboard & mouse controls for games: keys, WASD joystick, mouse aim and fire, per-app layouts, F12 to toggle.
- Automatic updates from GitHub Releases with SHA-256 verification.
- Installer without admin rights; uninstaller asks whether to keep Android data.
