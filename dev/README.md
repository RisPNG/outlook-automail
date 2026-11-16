# Outlook Automail developer guide

A Windows tray application that sends ready documents through classic Outlook, normally as one email and, after a definite size rejection before submission, as separately tracked parts. Each employee chooses an account already configured in their Outlook profile. Folder settings and run history live on the shared folder, so several PCs can monitor the same work without independently sending it again.

For installation and everyday use, see the [user guide](../README.md). This guide covers development, shared configuration, Outlook behaviour, recovery, and release packaging. All commands below run from the repository root, one directory above this guide.

## Install and update

Download the [Windows installer](https://github.com/RisPNG/outlook-automail/releases/latest/download/OutlookAutomail-win-Setup.exe). It includes the .NET runtime and installs for the current Windows user. Classic Outlook must already be installed and configured. Source code, downloads, and updates are public in this repository. Downloads and updates do not need a GitHub login.

Setup also installs Microsoft's shared Evergreen WebView2 Runtime if it is missing. The message composer uses it only while its configuration window is open. Tray operation and background sending do not start a browser. A missing WebView2 installation needs an Internet connection for its Microsoft bootstrapper. Portable and development builds need the runtime installed separately.

The installed app checks for updates at startup and every six hours, then downloads them in the background. A downloaded update installs when the app is in the tray and processing and Outlook automation are idle. An open window or editor keeps the update pending. You can also use Check for updates and Install update in the window or tray menu. An update never interrupts a send. The app restarts in the tray and keeps local settings and shared-folder history.

Running a newer Setup replaces the existing installation after confirming the replacement and closes that installed copy if it is running. Local settings, logs, and the message editor cache are kept in `%LOCALAPPDATA%\OutlookAutomailData`, separate from the installed files, so Setup replacement keeps the computer's preferences. The downloaded updates use the idle processing checks described above.

Uninstall through Windows Installed apps or open `Uninstall Outlook Automail.lnk` in the installation folder. This runs Velopack's native uninstaller, which closes the app and removes the installed files, app shortcuts, and uninstall entry. The app's uninstall callback removes its sign-in startup entry, local data folder, and package-specific Velopack log and backup. Shared folder configuration, `.automail` history, message photos, and document files stay on their shared folders. Shared folders cannot be inside the installation or local data folders. Microsoft's shared WebView2 Runtime remains available for other apps. Shared Velopack installer logs are preserved, and Velopack may recreate its package log if it rotates while finishing uninstall. Windows may retain its own installation or security history. Locked files or permission problems can prevent complete removal and are recorded by the native uninstaller.

If you previously used an extracted ZIP, close that copy and install Setup once to enable automatic updates. Portable ZIPs remain available for manual use, but do not update automatically. Builds are currently unsigned, so Windows may display an unknown-publisher warning.

## Build and run

Install [mise](https://mise.jdx.dev/), then run these commands from the project directory:

```powershell
mise trust
mise install
mise run build
mise run test
mise exec -- dotnet run --project src/OutlookAutomail/OutlookAutomail.csproj
```

The SDK version is pinned in [mise.toml](../mise.toml) and [global.json](../global.json). NuGet versions are centralised in [Directory.Packages.props](../Directory.Packages.props), resolved dependencies are locked, and the Velopack CLI is pinned in [.config/dotnet-tools.json](../.config/dotnet-tools.json).

To create a self-contained Windows x64 distribution:

```powershell
mise run publish
```

Run `artifacts/win-x64/OutlookAutomail.exe` from the repository root. Keep the published files together. The self-contained distribution includes the .NET Desktop Runtime. For managed PCs that already have the .NET 10 Desktop Runtime, publish a smaller framework-dependent distribution:

```powershell
mise run publish-small
```

The application runs with the current employee's Windows permissions and requires classic Outlook to be installed and configured. Opening the application again brings its existing window forward. Closing the window keeps the tray application running. Use the tray menu's Exit command to stop it. Exiting the tray app releases its Outlook references without quitting Outlook.

## Choose an Outlook account

The app uses classic Outlook's existing profile. It does not ask for a password, tenant ID, client ID, or separate application registration:

1. Open classic Outlook and confirm that the work account can send email normally.
2. In Outlook Automail, choose the Outlook account button. This connects to classic Outlook and may start it if necessary.
3. Select the intended sending account from the configured accounts. The app uses that account explicitly rather than Outlook's default account.
4. Add or review the shared folders, then enable the folders to monitor on this computer.

Selecting no account pauses this computer's sending. The choice is stored locally, while recipients and schedules are shared. Outlook remains responsible for account authentication. The app does not store mailbox passwords or access tokens.

An unconfigured app does not start Outlook automatically. Background checks can attach to an available running classic Outlook session for the selected account. If Outlook is unavailable, the app waits and reports that status. Opening Outlook again or reconnecting through the account chooser resumes eligible work.

New Outlook does not expose the required Outlook Object Model. Both editions may be installed, but the configured account must be available in classic Outlook. The company's Outlook security settings can still prompt or block programmatic access or sending. The app does not change these settings or dismiss security prompts. Prompt-free unattended sending must be verified on the intended PCs. See Microsoft's [feature comparison](https://support.microsoft.com/en-us/outlook/getstarted/feature-comparison-between-new-outlook-and-classic-outlook) and [Outlook security behaviour](https://learn.microsoft.com/en-us/office/vba/outlook/how-to/security/security-behavior-of-the-outlook-object-model).

## Configure a shared folder

Add a folder using its UNC path, for example `\\server\shared\documents`. If `config.yml` already exists, the app loads that configuration. Otherwise, configure the pending and sent subfolders, To and CC addresses, subject, message, and schedules in the editor. Subfolder paths are relative to the selected root.

New local subscriptions begin paused. Enable the folder when its configuration is ready and the selected Outlook account is available. Removing a folder from one PC removes only that PC's subscription. It does not delete shared settings, files, or history.

The shared layout is:

```text
configured-root/
  config.yml
  To send/
  Sent/
  .automail/
    worker.lock
    state.json
    workers/
    active/
    runs/
      <batch-id>/
        batch.json
        run.json
        ... immutable attempt and completion records ...
        attachments/
```

The application owns `.automail`. Keep this state with the folder. Do not delete its records to retry a send or copy them to an unrelated configuration. Every PC needs permission to read and write the configured root, claim pending files, and archive files.

Only completed, ready files should be placed directly in the pending directory. Finish writing a file outside that directory, then move or rename it into place. There is no artificial readiness delay. Exclusive access and integrity checks still apply, but a file that can be opened alone cannot prove that its producer has finished. Configure nested work queues as their own roots rather than expecting recursive attachment discovery.

Each run captures the ready files and the current recipients/message into a stable batch. New arrivals wait for a later run. The app has no configurable message-size cap. The legacy YAML keys `file_ready_seconds` and `maximum_message_size_mb` are accepted when loading an older configuration but do not delay files or impose a limit, and the editor no longer exposes these settings. Outlook and the mailbox provider still control message-size limits. See [message-size rejection and splitting](#message-size-rejection-and-splitting) for the conditions that permit splitting.

## Write and preview the message

Open the shared folder editor's Message tab and write directly in the email, using the toolbar for font, size, bold, italic, underline, text colour, alignment, lists, indentation, and links. Keyboard shortcuts and undo/redo work in the editor. Existing plain-text messages keep their visible line breaks, and photos appear directly where the cursor was placed. You do not need to write or read HTML markup.

Recipient preview renders the resolved subject, date placeholders, formatting, and embedded photos. It updates shortly after typing. The saved HTML is the source used to build the email, with the date frozen when a batch is created. Different email clients, including classic Outlook's Word-based renderer, can vary in spacing or supported formatting. The preview does not contact external websites or load remote images. Pasted HTML keeps supported email formatting and removes active content and unsupported external resources.

Choose Add photo, paste an image, or drop an image into the body to add a PNG, JPEG, GIF, or BMP. The app copies it into the shared root's `Message assets` folder and saves an embedded content ID. The Photos view lists reusable images with a thumbnail. Insert selected photo reuses an image at the cursor. Remove from message removes its placements while keeping the photo available for reuse and undo. Only photos still placed in the body are attached. `Message assets` is reserved and cannot become a pending or sent folder.

The shared YAML records the body and reusable image paths, for example:

```yaml
body_is_html: true
body: '<p>Please find the documents for {date} attached.</p><img src="cid:signature" alt="Signature">'
inline_images:
- id: signature
  path: Message assets/signature.png
```

Use the GUI to import photos and create valid IDs and paths. Images are included in the email, so recipients do not need access to the shared folder or a website. Only images referenced by the body are included. They count towards Outlook's and the provider's message-size limits and are repeated in each split email. Reusable source files are never moved into Sent. Every batch freezes its own verified copies before claiming documents, so a later photo edit or deletion does not change recovery. An explicit retry of a rejected batch can use only the photos already frozen for that batch. A photo does not count as a pending document. To send a photo-only scheduled message, enable sending without attachments.

Inline photos use HTML `cid:` references, their attachment content IDs, and MIME types. The Outlook connector does not set the optional MAPI attachment flags or RTF rendering position. Real classic Outlook rendering still requires verification on the intended PCs.

## Dates in the subject and body

Open the shared folder configuration and select the Message tab. Enter `{date}` anywhere in the subject or body, choose its default date format and message time zone, and check the live preview. The insertion buttons add a placeholder at the cursor. These settings are saved in the shared `config.yml`, independently of the schedule time zones.

For example, these message settings use Malaysia/Singapore time:

```yaml
subject: "Documents - {date}"
body: "Please find the documents for {date:dd MMMM yyyy} attached."
body_is_html: false
date_format: dd/MM/yyyy
date_time_zone_id: Singapore Standard Time
```

This is the message portion of an existing configuration; keep its folder identity, paths, recipients, and schedules. The GUI saves the complete file.

`{date}` uses `date_format`. An inline format such as `{date:yyyy-MM-dd}` overrides it for that placeholder. Both subject and body can contain several placeholders with different formats. Common formats are:

| Format | Example |
| --- | --- |
| `dd/MM/yyyy` | `16/11/2026` |
| `yyyy-MM-dd` | `2026-11-16` |
| `dd MMMM yyyy` | `16 November 2026` |
| `dddd, dd MMMM yyyy` | `Monday, 16 November 2026` |
| `yyyy-MM-dd HH:mm zzz` | `2026-11-16 06:00 +08:00` |

Formats use [.NET date and time formatting](https://learn.microsoft.com/en-us/dotnet/standard/base-types/custom-date-and-time-format-strings), with invariant English month and weekday names so different PCs produce the same text. `MM` means month; `mm` means minute. Standard format codes follow .NET's own rules, including UTC conversion for formats that require it. Braces and line breaks are not allowed inside a placeholder's format. Invalid formats or unfinished date placeholders prevent saving and sending, with an error identifying the affected field.

Write `{{date}}` or `{{date:yyyy-MM-dd}}` to include that placeholder literally. Other braces, including HTML styles and JSON, remain unchanged. HTML bodies keep their markup, and date replacements are encoded as HTML text.

The date is captured when the batch is first created. A missed schedule processed today uses today's date in the chosen message time zone. The rendered subject and body are saved with the batch, so another PC or a later recovery does not change them. An intentional retry after a definite rejection can apply edited message settings, using the original batch creation instant. Existing configurations without date settings use `dd/MM/yyyy` and `UTC`; messages without placeholders keep their existing text.

## Priority and scheduling

For each shared folder, the first registered worker has priority. Later workers are backups in registration order. Workers publish availability heartbeats while their folder is enabled and their account is usable. A backup can take over after a preferred worker becomes unavailable. A missed heartbeat only affects preference for new work. It never revokes a lock on an active run.

An exclusive SMB file handle protects processing. Every worker checks the same durable occurrence and batch records after acquiring ownership. If a primary returns after a backup has started or completed that occurrence, it leaves that work alone. The primary becomes preferred again for future work. Scheduled work is checked and availability heartbeats are refreshed every 20 seconds. Heartbeats become stale after 90 seconds, so failover normally includes that delay. Run now is an explicit user action that can bypass worker preference while retaining the shared lock and duplicate-prevention records.

Schedules contain explicit local times, weekdays, and time zones. The shared activation time prevents replaying schedules from before the folder was configured. At startup and after interruptions, the app checks for due work. Several missed occurrences are combined into one catch-up opportunity. Depending on the configured policy, an empty occurrence can be skipped, wait for files, or send a message without attachments.

The tray application needs an available, signed-in Windows session, classic Outlook with the selected account, and access to the shared folder. Outlook also needs a connection to the mailbox provider to finish sending. If all PCs are off, asleep, logged out, or awaiting Outlook interaction, sending waits until a worker is available. Start at Windows sign-in is optional and applies only to the current Windows user.

## Message-size rejection and splitting

Automatic splitting requires definite size evidence before submission, either from Outlook rejecting draft preparation or from an available provider preflight limit. It never follows a send call that has started. A queued message, a timeout, a later non-delivery report, or a recipient-specific rejection does not prove that nothing was sent. These outcomes do not authorise automatic splitting or resending.

The planner keeps every document whole and groups documents into roughly balanced emails that fit the usable limit when known. It aims to keep groups as large as practicable rather than create one email per file. Every part repeats the frozen body and inline photos and adds `[Part i/N]` to the subject. Encoding, repeated content, and message overhead count towards the estimated size. Provider limits and Outlook's representation can differ from that estimate, so splitting cannot guarantee acceptance or delivery.

The connector optionally reads the selected Outlook store's `PidTagMaximumSubmitMessageSize` (`0x666D0003`, kilobytes converted to bytes). An absent or unsupported property does not impose a default cap. Draft attachment additions can report a size failure before `Send`; a general COM failure or a send-time `0x80040305` is insufficient proof. The app tries balanced candidate groups from two parts up to one document per part, skipping estimates above a known limit. It records candidate IDs in `preflight-*.json` before creating drafts and prepares all candidates without submitting them. A failed size trial clears only confirmed unsubmitted drafts. It stops when even an individual document with repeated content cannot fit. This is a bounded heuristic rather than an exact packing optimum or a test of recipient delivery.

The successful plan is stored immutably in the parent batch's `split.json` before any child claims documents or sends. Only the parent stays in the active queue. Children use the ordinary batch manifests, immutable submission markers, reconciliation, and archive workflow, with no covered schedule occurrences of their own. Their document sources are the parent's frozen staging area, never a newly arrived file with the same name. Restart consults recorded trial IDs before allowing any original-message submission. A trial found in Outbox or Sent Items blocks automatic processing for review. The selected original account is required throughout split preparation and recovery, and the frozen child message and recipients survive later configuration edits.

A later size failure after plan publication leaves that specific part Size blocked. Existing part numbers and submitted messages remain unchanged. Raise the provider limit and retry, or complete that exact part separately and confirm it. The app does not recursively repartition or renumber messages that recipients may already have received.

Each part has its own durable identity, Outlook correlation, submission attempts, and completion records. Recovery resumes unfinished parts without sending completed parts again. Files are archived for their confirmed part. The parent batch's original schedule occurrence is complete only after all parts are confirmed and archived. A later scheduled run does not replace unfinished parts.

If one document cannot fit with the repeated message, the error identifies that document. If the body or inline photos alone cannot fit, the error identifies the repeated content. Ask the mailbox administrator to raise the applicable limit or arrange an external send or sharing method. Do not replace a recorded attachment or inline photo inside an immutable batch, or delete its journal to start again. If the email is sent outside the app, verify the exact recorded message and documents using reliable evidence, then use the matching run's manual sent confirmation to permit archiving without another send. Outbox alone is insufficient evidence. Review every part separately when a batch has already split.

Creating split metadata upgrades the shared folder configuration to schema version 2. Older apps reject that schema rather than treat the original batch as unsent. Update every PC that monitors the folder before allowing splitting, preserve the shared history, and do not downgrade the configuration while split metadata exists. Schema version 1 remains valid for folders that have not needed splitting.

## Recovery and duplicate prevention

Every batch records its files, message settings, original sending account, stable message correlation ID, and processing state. A frozen batch manifest is published before claiming files. Active membership records identify unfinished work without repeatedly scanning historical batches. Scheduled batches have stable identities, and each send attempt acquires an immutable submission record before asking Outlook to send. Queued, sent, archived, and skipped completion records preserve the outcome if a stale worker later writes an older progress record.

- Interrupted preparation can resume from the recorded batch. A different employee prepares a draft in their own mailbox instead of using another employee's draft.
- Outlook accepting the message into its Outbox records Queued. The files stay in the recorded batch until the app finds the correlated message in Sent Items.
- A confirmed sent message followed by a failed archive operation retries only the file archiving.
- A timeout or crash during submission leaves an uncertain outcome. The application checks the existing message using the original sending account. It does not automatically create a replacement email.
- A definite failure before submission remains available for an intentional retry after the problem has been corrected. Each submission attempt keeps its own immutable outcome. A timeout, security prompt, or COM error after the send call starts is treated conservatively as an uncertain outcome.
- Another employee's account normally cannot inspect the original employee's mailbox. That batch remains blocked for reconciliation or review rather than being resent from another account.
- Manual confirmation that a batch was sent is an explicit recovery action. Verify Sent Items in the original mailbox or other reliable evidence before confirming it. An Outbox entry is not evidence of completed sending. This action permits archiving and does not submit another email.

A Sent Items copy does not prove delivery to every recipient. Non-delivery reports remain part of the mailbox's normal behaviour. The app coordinates submissions and conservatively handles uncertain outcomes. It cannot make an Outlook operation and an SMB state update one transaction. See Microsoft's [Outlook automation documentation](https://learn.microsoft.com/en-us/office/vba/outlook/concepts/getting-started/automating-outlook-from-a-visual-basic-application).

Outlook message entry IDs can change as a message moves. The app uses its own batch correlation property to recognise a message across Drafts, Outbox, and Sent Items. Whether that property synchronises to a second PC depends on the mailbox provider. Missing evidence never authorises a duplicate send. Queued or uncertain batches remain with their original sending account until reconciliation or explicit review.

Unfinished records created by the earlier Graph-based version cannot be treated as Outlook messages. Finish or review them using the earlier version before monitoring that existing queue with this version. Do not delete a journal or change its transport identity to force a retry. Folder configuration remains reusable.

Use one authoritative SMB share whose server correctly implements file sharing and durable writes. Cloud-synchronised folders, independent replicated copies, and offline cached copies do not provide the same coordination. Validate failover and reconnect behaviour against the actual server or NAS before production use.

## Local data and diagnostics

Local settings and application logs are stored under `%LOCALAPPDATA%\OutlookAutomailData`. Shared history belongs to the configured root. Keep logs and journal files when investigating a failed run. Outlook manages its own profile and authentication data separately.

This release starts fresh with new local settings, single-instance, and Outlook message identities. Before upgrading, turn off Start at Windows sign-in in earlier releases, stop every older copy, and finish or review all pending batches. This release cannot correlate Outlook messages created by earlier releases. Select the Outlook account and add the shared folders again after upgrading. Existing local files and earlier company-branded startup entries are left in place. The generic Outlook Automail startup entry uses the current saved preference and is removed on uninstall.

The test suite exercises scheduling, worker selection, batch recovery, and Outlook connector behaviour with fake sessions and local temporary folders. Real Outlook security prompts, inline photo rendering, size rejection, mailbox-provider synchronisation, recipient delivery, and network partitions on the actual SMB server require environment-specific verification. Tests do not start Outlook, authenticate an account, or send actual email. Native Outlook integration has not been verified by these tests.

## Publish releases

Every push to `main` runs the Windows formatting, build, and full test checks in GitHub Actions. Successful runs package the runtime-included installer and publish a release in this repository, whose URL is shared with the app through [release.json](../release.json). The project's version supplies the major and minor numbers, and the workflow run number supplies the patch, so ordinary pushes do not require a manual version bump. Release tags point to the exact commit used to build the package. Feature branches do not publish. Rerunning an already published or superseded run leaves published releases unchanged.

The workflow uses GitHub's automatic `GITHUB_TOKEN` with Contents write permission for this repository. No personal publishing token or extra repository secret is needed. The app reads public releases without credentials. The former `outlook-automail-releases` address redirects here so existing installations keep updating. Do not reuse that repository name, because doing so would remove the redirect.

To package locally after running the checks:

```powershell
mise run format-check
mise run build
mise run test
$releaseVersion = Read-Host 'Version to package (major.minor.patch)'
mise run release -- -Version $releaseVersion
```

Choose a version newer than any published release. Packages are written to `artifacts/releases/<version>`. Local packaging does not publish or install the app. The workflow uses the same release command with `-Publish` and passes its automatic token through `GH_TOKEN`. Publication stages a draft, verifies the feed's packages, uploads all referenced packages, and only then makes the release public. Velopack manages package verification, delta downloads, installation, and restart.
