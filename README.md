# Outlook Automail

A native Windows tray app that sends scheduled attachment emails through an account configured in classic Outlook. Shared-folder settings coordinate several PCs and prevent completed batches from being sent again. Message templates support formatted dates and embedded photos.

[Download the Windows installer](https://github.com/RisPNG/outlook-automail-releases/releases/latest/download/OutlookAutomail-win-Setup.exe)

The installer includes the .NET runtime. Classic Outlook must already be installed and configured. Close an older portable copy before installing. Run Outlook Automail, connect to classic Outlook, choose an account, and configure or import a shared folder. Newly imported folders remain paused until enabled.

Installed copies check for updates at startup and every six hours. Downloads happen in the background, and installation waits until the app is in the tray and processing is idle. An open window or editor keeps the update pending. The app restarts in the tray. You can also check and install updates from the app's window or tray menu. Public downloads do not require a GitHub account.

Portable ZIPs are available for manual use and do not update automatically. Builds are currently unsigned, so Windows may display an unknown-publisher warning. This repository contains release files only. The application source is maintained privately.
