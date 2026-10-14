# Microsoft Store listing: FolderTrail

Copy for Partner Center. Limits are the Store's field limits as I know them; confirm in the submission form.

## Before you submit, verify these claims on the installed package

The listing and the privacy policy promise the following. Confirm each one against a real MSIX install:

- [ ] Uninstalling removes the history (data is in the package's private storage).
- [ ] **Settings → Delete all data** clears the database and diagnostics log.
- [ ] Start with Windows works through the startup task, and starts minimized.
- [ ] Upgrading to a newer package keeps the existing database.
- [ ] No network access: run it for a few minutes and check for outbound connections.

---

## Product name
FolderTrail

## Short description (max 100 characters)
A private change journal for your folders: what changed, and when.

## Description (max 10,000 characters)

Ever wondered what changed in a folder while you were away? FolderTrail keeps a searchable journal of what was
created, modified, deleted or renamed in the folders you choose, and when it happened.

**A journal, not a backup.** FolderTrail records names, paths, sizes and times, never file contents. It can tell
you what happened; it cannot restore a file.

**What you can do**
• Watch any folder, with or without its subfolders. Nothing is watched until you add it.
• See changes as they happen, then browse a timeline grouped by day.
• Search and filter by folder, type of change, source and date.
• Export the history to CSV.
• Exclude what you don't care about. Sensible defaults skip .git, bin, obj, node_modules and temp files.
• Keep running quietly in the notification area, and optionally start with Windows.
• Switch between light and dark themes, and use it entirely from the keyboard.

**Honest about what it saw**
FolderTrail records changes live, and runs periodic scans to catch what live monitoring missed, for example when
the app was closed, your PC was asleep, or Windows dropped a notification. Changes found by a scan are labelled
as such and dated by when the scan ran. Renames are only reported when Windows says so directly, never guessed.
The Diagnostics page shows what was and wasn't captured and never overstates it.

**Private by design**
• Everything stays on your PC. There is no account, no analytics, no ads and no network access.
• No administrator rights needed.
• You decide how long history is kept, and Delete all data wipes it.
• Free and open source (MIT).

**Good to know**
• Windows can occasionally lose change notifications. FolderTrail detects this and rescans, but no tool can
  prove every change was caught.
• A scan finds net differences only, so it cannot reconstruct the steps in between and reports a rename as a
  deletion plus a creation.
• Shortcuts to other folders (junctions and symbolic links) inside a watched folder are recorded but not followed.

## Features list (up to 20 items, each max 200 characters)
1. Live journal of created, modified, deleted and renamed files and folders
2. Periodic scans catch changes missed while the app was closed or your PC slept
3. Searchable timeline grouped by day, filterable by folder, type, source and date
4. Export the history to CSV
5. Per-folder and global exclusions, with sensible defaults
6. Runs in the notification area, with optional Start with Windows
7. Fully offline and private: no account, no analytics, no admin rights

## Search keywords (up to 7)
folder monitor, file change log, folder watcher, activity journal, file history, audit folder, change tracker

## Category
Utilities & tools (secondary tag if offered: Productivity)

## Release notes (max 1,500 characters), version 1.0.0
First Microsoft Store release.
• Watch folders and keep a journal of created, modified, deleted and renamed items.
• Searchable, filterable timeline with CSV export.
• Exclusions, notification-area icon, optional Start with Windows, light and dark themes.
• Everything stays on your PC.

## Privacy policy URL
`https://dipjyotisikder.github.io/folder-trail/privacy` (after enabling GitHub Pages: Settings → Pages → Deploy
from branch `main`, folder `/docs`). The page is `docs/privacy.md`.

## Age rating (IARC questionnaire)
Utility app with no user-generated content, no chat, no purchases, no location sharing and no web access.
Answer "No" to every content question. Expect the lowest rating.

## Properties
- Platforms: Windows 10 (1809 or later) and Windows 11, x64.
- Pricing: Free.
- Declares: no network access, no personal data collected.

## Notes for certification (max 2,000 characters)

FolderTrail is a desktop (Win32 / .NET) app that records changes inside folders the user chooses.

**Why runFullTrust is declared.** The app watches arbitrary user-selected folders, on any drive, using the
standard file-change notification API (FileSystemWatcher) and periodic directory scans. It also stores its own
database in its local app data. It does not read file contents, does not need administrator rights, and makes no
network connections.

**How to test (no sign-in needed).**
1. Launch FolderTrail and complete the short first-run steps.
2. Choose "Add folder" and pick any folder, for example a new empty folder on the Desktop.
3. In that folder, create a text file, edit it, rename it, then delete it.
4. Open the Timeline: each change appears with its time, and can be searched and filtered.
5. Use "Export CSV" on the Timeline to save the history as a CSV file.
6. Settings → "Start with Windows" enables the declared startup task (it is off by default).
   It can be switched off in Settings → Apps → Startup.
7. Settings → "Delete all data" clears everything the app stored.

The app has no account, no in-app purchases, no ads and no network features.

## Screenshots (min 1; recommended 4 to 5, at 1920×1080)

Ready in `docs/store/screenshots/`, one light and one dark version of each. Upload the set you prefer (or both).

| File | Screen | Caption |
|---|---|---|
| `1-timeline-*.png` | Timeline with a day's activity and the details pane | See what changed, and when |
| `2-search-*.png` | Search narrowing the history | Search the whole history |
| `3-add-folder-*.png` | Watch-a-folder dialog | Watch only the folders you choose |
| `4-exclusions-*.png` | Per-folder exclusions | Skip the noise: .git, bin, node_modules and more |
| `5-diagnostics-*.png` | Diagnostics | Honest about what was captured |

They are rendered from the real app by a test, from real file activity, and show only a neutral demo folder (no
user name or personal paths). To regenerate after a UI change, use a neutral folder so no profile path leaks in:

```powershell
$env:FOLDERTRAIL_DEMO_DIR = 'C:\FolderTrailDemo'
dotnet test tests/FolderTrail.Tests -c Release --filter "FullyQualifiedName~Store_screenshots"
# images appear in C:\FolderTrailDemo\store-screenshots; copy them into docs/store/screenshots
```

Look at each image before uploading; the Store shows them publicly.

## Store logos
Generated from `src/FolderTrail.App/Assets/foldertrail-256.png` by `scripts/package-msix.ps1`. For the Store
listing itself (not the package), upload a 1:1 app tile at 300×300 or larger; a 1024×1024 source would be sharper.
