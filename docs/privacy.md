# FolderTrail privacy policy

_Last updated: 14 October 2026_

FolderTrail is a Windows app that keeps a change journal for folders you choose. This policy explains what it
stores and where. In short: **everything stays on your computer, and nothing is ever sent anywhere.**

## What FolderTrail collects

FolderTrail doesn't collect anything about you. It has no accounts, no sign-in, no analytics, no advertising,
no crash reporting and no update checks. The app contains no network code and never connects to the internet,
so there is no data for the developer or any third party to receive.

## What FolderTrail stores on your device

For each folder you choose to watch, FolderTrail records, in a local database on your computer:

- the **names and paths** of files and folders inside it,
- **sizes** and **timestamps**,
- the **kind of change** (created, modified, deleted, renamed) and when it was seen.

It **never reads or stores the contents of your files.** It cannot restore a file. It is a journal, not a backup.

It also stores your settings (for example theme and how long to keep history) and a diagnostic log. Log entries
have file and folder paths redacted by default.

Nothing is written inside the folders you watch.

## Where it is stored

| What | Location |
|---|---|
| Change journal and settings | `%LOCALAPPDATA%\FolderTrail\foldertrail.db` |
| Diagnostic log (paths redacted) | `%LOCALAPPDATA%\FolderTrail\diagnostics\` |

The Microsoft Store version keeps this data in the app's private storage, which Windows removes when you
uninstall the app. The direct-download version stores it in the folder above.

## How long it is kept

History is kept for 90 days by default. You can change this to 30, 90, 180 or 365 days, or keep it
indefinitely, under **Settings**. The record of what is currently inside a watched folder is kept for as long as
that folder is watched.

## Sharing and exporting

FolderTrail shares nothing on its own. You can choose to:

- **Export history to CSV.** The file is saved where you choose and contains paths, so treat it accordingly.
- **Export diagnostics.** Paths are left out unless you explicitly ask to include them. If you send a diagnostics
  file to anyone, for example when reporting a problem, that is your decision.

## Deleting your data

- **Settings → Delete all data** removes the database, the settings and the diagnostic log.
- **Remove a folder** from the watched list and choose "stop watching and delete this folder's history" to
  delete what was recorded for it. (If you choose to keep it, it is deleted when it passes the retention period.)
- Uninstalling the Microsoft Store version also removes its stored data.

## Start with Windows

If you turn on **Start with Windows**, FolderTrail registers to launch when you sign in. You can turn it off in
Settings, or in Windows under **Settings → Apps → Startup**.

## Children

FolderTrail has no accounts, content or communication features and collects no personal information from
anyone, including children.

## Changes to this policy

If this policy changes, the new version will be published at this address with an updated date.

## Contact

FolderTrail is open source: <https://github.com/dipjyotisikder/folder-trail>. Questions about privacy can be
raised as an issue at <https://github.com/dipjyotisikder/folder-trail/issues>.
