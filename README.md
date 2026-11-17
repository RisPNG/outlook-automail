<p align="center">
  <img src="src/OutlookAutomail/Assets/AppIcon.png" alt="Outlook Automail logo" width="128">
</p>

<h1 align="center">Outlook Automail</h1>

Outlook Automail sends documents from a shared folder through classic Outlook at the times you choose. Several PCs can watch the folder, with the others acting as backups.

## Get started

1. Download and run the [Windows installer](https://github.com/RisPNG/outlook-automail/releases/latest/download/OutlookAutomail-win-Setup.exe). Windows may show an unknown-publisher warning because the app is unsigned.
2. Open **classic Outlook** and check that your work account can send email. New Outlook is not supported.
3. Open Outlook Automail, choose the Outlook account button, and select your sending account. The app uses your existing Outlook sign-in.
4. Add your shared folder, for example `\\server\shared\documents`. Review its recipients, pending and sent folders, message, and schedule. Existing settings load automatically.
5. Enable the folder on this PC when everything is ready. You can also turn on **Start at Windows sign-in**.

Newly added folders start paused. Selecting no Outlook account also pauses sending. Removing a folder only removes this PC's connection to it.

## Prepare the email

Open the folder settings and choose **Message**. Write and format the email, then check the recipient preview. Use **Add photo**, paste an image, or drop one into the message. Photos are included in the email.

Add `{date}` to the subject or message to include the date the email is first prepared. Choose its format and time zone. For example, `Documents - {date}` can become `Documents - 16/11/2026`.

Use the folder editor's **Variables** tab to set values for this computer, then put placeholders such as `{name}` in the shared subject or message. For example, write `Best Regards,` followed by `{name}` on the next line, and set `name` to `Jason` on one PC and `Lucia` on another. Each PC keeps its own values in the same shared `config.yml`. The recipient preview uses the current PC's values.

Update every PC watching the folder before saving variables. Once a variable is defined, each PC using that placeholder needs its own value before it can prepare a new email. Existing batches keep their prepared message during recovery.

## Send documents

Finish saving or copying documents outside the pending folder, then move them into it. Files in subfolders are not collected. Keep the PC awake, signed in, and connected to the share, with classic Outlook available.

The app checks schedules every 20 seconds, with no extra document waiting period. Files still in use wait until they can be opened safely. **Run now** starts a manual run. Closing the window keeps the app running in the tray. Use the tray's **Exit** command to stop it.

Documents normally go together. If Outlook reports a size limit before sending, the app can divide the documents into roughly equal emails labelled `[Part 1/3]`, `[Part 2/3]`, and `[Part 3/3]`, for example. It uses as few parts as it can prepare successfully. Each part keeps the message and photos, and documents stay whole. Files move to the sent folder after their email is confirmed in Sent Items.

If a run is **Size blocked**, one document or the repeated message and photos cannot fit. Ask your administrator to raise the limit, then use **Run now** to retry. If you send or share the recorded files separately, verify the exact batch or part with the recipients before using **Confirm sent**. Keep its files and history intact.

## Check a problem

Check the folder status and run history. If Outlook or the share is unavailable, restore access and let the app check again.

A message in **Outbox** is still waiting. If a run needs review, check the original account's Outbox and Sent Items. Use **Confirm sent** only after verifying the email was sent. It archives files without sending again. Timeouts and later delivery failures do not trigger automatic resends. Sent Items does not guarantee delivery to every recipient.

Keep `config.yml` and `.automail` with the shared documents. Do not delete history or replace recorded files to force a retry. See the [developer and troubleshooting guide](dev/README.md) for recovery details.

## Updates

The installed app downloads updates automatically and installs them when it is idle in the tray. You can also choose **Check for updates** from the window or tray menu. Updates wait for an open window or active send to finish.

Update every PC that watches a shared folder before using automatic message splitting. Once a folder has split-message history, keep all its PCs on a compatible version and do not downgrade its shared settings.

To remove the app, use Windows **Installed apps**. Shared documents, settings, photos, and send history stay on the shared folder.
