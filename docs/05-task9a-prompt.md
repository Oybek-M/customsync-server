Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 9a — capture storage maintenance (media store, retention, TDLib cache, systemd unit)

Plan 05 Task 9 is split in two. **9a (this prompt)** keeps the capture
service's disk usage bounded and visible and adds its systemd unit. It
changes **nothing on the server**. 9b (a separate prompt, given only
after 9a is verified) sends the measurements made here to the backend.

## REPOSITORY

- **Paths depend on the computer** (laptop and PC differ). First run
  `hostname`, then take `<server>` and `<tdesktop>` from the table in
  `<tdesktop>\docs\MACHINES.md`. Find `<tdesktop>`: whichever of
  `C:\TBuild\tdesktop` / `D:\TBuild\tdesktop` / `D:\Oybek\Telegram\tdesktop`
  exists; otherwise the folder containing `Telegram\SourceFiles\custom_db.cpp`.
  Check every path exists before using it; `git -C <server> remote -v`
  must show `Oybek-M/customsync-server`. Never guess a path — if not found,
  stop and ask.
- Repo: `<server>` (`customsync-server`), branch `Oybek`. Push only to
  `origin Oybek`.
- **`<tdesktop>` is READ-ONLY.** No edits, commits, pulls, pushes or
  builds there — another session owns it. Read only the documents named
  below.
- Starting state: HEAD `d1cbd4d` or later, `dotnet build` 0 warnings,
  `dotnet test` 419/419.
- **Stale test vectors (seen on the laptop, `DESKTOP-L2J53IK`).** If
  `<tdesktop>\docs\sync-protocol\test-vectors.json` has no `key_wrap`,
  `fingerprint` or `discriminator` section, the `<tdesktop>` checkout is
  behind its remote and ~17 tests fail with a message saying so. Do NOT
  pull `<tdesktop>`. Instead:
  `git -C <tdesktop> show origin/Oybek:docs/sync-protocol/test-vectors.json > <file outside the repo>`
  and set `CUSTOMSYNC_TEST_VECTORS` to that file for every `dotnet test`.
  Read the plan and spec the same way (`git -C <tdesktop> show origin/Oybek:<path>`).
  Never commit these files.
- **PC only (hostname `DESKTOP-5CAUS66`):** a stale Visual Studio NuGet
  config points at a deleted `E:\Application's datas\...` folder, so a
  plain `dotnet build` / `dotnet test` fails with `MSB4018` or `NU1301`.
  Do NOT edit anything under `C:\Program Files (x86)` or the user NuGet
  config. Instead:
  1. write a temporary `nuget.config` **outside the repo** containing
     `<packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>`
     and `<fallbackPackageFolders><clear/></fallbackPackageFolders>`;
  2. `dotnet restore --configfile <that file>` (no new packages are
     needed);
  3. from then on **always** `dotnet build --no-restore` and
     `dotnet test --no-build`; for deliberate breaks rebuild with
     `dotnet build --no-restore --no-incremental`.
  Never commit that file. On the laptop none of this is needed.
- Known intermittent test (unrelated, recorded in `PROGRESS.md` §5):
  `SyncEndpointsTests.Pull_returns_pushed_records_and_advances_cursor`
  fails roughly once in 30 full runs. If it is the only failure, rerun;
  do not modify it. Any other failure is yours.

## WHY THIS TASK MATTERS

The owner's requirement quoted in the plan: manage storage smartly and
watch it constantly. Today nothing bounds the capture service's disk:

- Task 8's downloaded media stay where TDLib put them
  (`Telegram:FilesDirectory`), and `captured_media` rows and files are
  **never** removed — not after upload, not when the message leaves the
  30-day message cache, not when media capture is switched off.
- TDLib's own file cache grows without limit. The plan's answer is
  `optimizeStorage` — but the plan's sample call passes `count = 0` and
  `immunity_delay = 0`. TDLib's documentation
  (https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1optimize_storage.html):
  `count` is the "limit on the total number of files after deletion" and
  `immunity_delay` is "the amount of time after the creation of a file
  during which it can't be deleted". That call deletes **every** eligible
  file, including one downloaded a second ago. Do not copy it.
- Because `optimizeStorage` deletes files inside TDLib's cache, media
  that must survive until a deletion is pushed cannot live there. Each
  downloaded file is therefore copied into a store the capture owns.
- The downloader never looks at free disk space.
- TDLib's internal log verbosity is never set. Its documentation does
  not state the default; levels ≥ 3 are informational/debug output that
  would flood the journal — and after the 2026-09 VPS compromise the
  journal is the security record.
- There is no systemd unit for the capture service.

Lesson from every previous plan 05 task: protection that exists in the
code but is not wired in, or that has a second door around it, passes
its own tests. Here: the retention rules, the deletion guard (never
outside the store), the disk guard and the start-after-invisibility
order must each be proved through the production path (real
registrations, the real `TdClient` over a fake transport).

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else. Do not state TDLib
behaviour you did not confirm from TDLib's documentation or source — say
"unconfirmed" instead.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block, **§2** (the Task 9 hand-over list),
   §3, §4, and the verification sections for plan 05 Tasks 3, 7 and
   **8**. Defects found there will be looked for again: unwired
   protection, a second door around a gate, fakes that do not behave like
   the real component, tests that build their own registrations, logs
   that leak paths through exception messages.
2. Plan (READ-ONLY) `<tdesktop>\docs\superpowers\plans\2026-07-29-multi-device-sync-05-capture-service.md`:
   **Task 9**, the constraints at the top, the "REVIZIYA 2026-08-25"
   block.
3. Spec (READ-ONLY) `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`:
   §0.3 (retention is a local decision and never creates records), §0.9,
   §10 (the "Disk to'lgan" row).
4. TDLib documentation:
   - https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1optimize_storage.html
   - https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1get_storage_statistics_fast.html
   - https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1storage_statistics_fast.html
   - https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1set_log_verbosity_level.html
5. Capture code: `Capture/MessageCache.cs` (migration chain, `user_version`
   is 4 — note that the base `CREATE TABLE IF NOT EXISTS` statements run
   **before** the version steps), `Capture/PeriodicCachePruner.cs`,
   `Capture/CaptureCacheRegistration.cs`, `Capture/CaptureHandlerRegistration.cs`,
   `Media/*.cs`, `Sync/CaptureSyncRunner.cs` (`PrepareMediaAsync`),
   `Tdlib/TdRequestPolicy.cs`, `Tdlib/TdClient.cs`, `Worker.cs`,
   `Program.cs`, `Preflight/CapturePreflight.cs`; `deploy/customsync.service`,
   `deploy/README.md`, `.gitattributes`.
6. Test helpers to reuse: `FakeRecordingTdTransport` (in
   `CaptureInvisibilityTests.cs`; it records `ExecuteCalls` and has
   `OnExecute`/`OnSend`), `CaptureMediaVerificationTests.cs` (`QueueTransport`,
   `RenderingLogger`, the real `TdClient` pattern), `CaptureMediaTests.Test08`
   (a Worker-level test).

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service or any CLI mode, no
  `dotnet run`. `dotnet build` / `dotnet test` only. Capture tests fake
  TDLib with a fake `ITdTransport` under the real `TdClient`.
- **No server changes.** Nothing under `src/CustomSync.Api`,
  `src/CustomSync.Services`, `src/CustomSync.Data`, `src/CustomSync.Core`.
- **Deletion safety.** The capture deletes files ONLY inside the media
  store directory, ONLY top-level regular files whose names match the
  store's patterns (§1). It never deletes a path taken from the database
  that lies outside the store, never deletes directories, never recurses.
- **Media of a pending deletion is never lost.** A `captured_media` row
  whose `deleted` record is still in `capture_outbox` is never pruned and
  its file is never deleted — whatever its status or age.
- **Never log** paths, file names, peer ids, chat ids, message ids, text,
  keys or tokens. Sizes, counts, hash prefixes and exception **type**
  names are fine. Exception messages contain paths: log
  `ex.GetType().Name`, never the exception object.
- **The TDLib allow-list stays the single gate.** Add exactly three
  methods — `optimizeStorage`, `getStorageStatisticsFast`,
  `setLogVerbosityLevel` — each with the strict parameter rules in §4,
  and a comment saying why each is safe for invisibility. No other new
  TDLib methods (no `deleteFile`). Do not change `setTdlibParameters`.
- **K1** — tunables from `IConfiguration` (the capture never reads
  `server_settings`). **K6** — TDD: each test first, seen failing.
  **K7** — one commit, imperative subject, WHY in the body, **no
  `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`.

## 🔴 1. Media store (a directory the capture owns)

New configuration (optional; defaults shown; ranges enforced by
preflight, §6):

| Key | Default | Rule |
|---|---|---|
| `Capture:Media:StorageDirectory` | `/var/lib/customsync-capture/media` | rooted path, see §6 |
| `Capture:Media:MaxTotalBytes` | `1073741824` (1 GiB) | `≥ Capture:Media:MaxBytes`, `≤ 1099511627776` |
| `Capture:Storage:MinFreeBytes` | `2147483648` (2 GiB) | `≥ 0` |

A `MediaStore` (`src/CustomSync.Capture/Media/MediaStore.cs`) used by the
downloader, the maintenance service and preflight:

- File name of a row: `<peer_id>-<msg_id>.bin` — the canonical tdesktop
  peer id and the positive server message id of the row; anything else
  → `ArgumentException`. No original file names ever reach the disk.
  Temporary name during a copy: `<peer_id>-<msg_id>.bin.part`.
- `IsManaged(path)`: true only when the full path's parent directory is
  the store directory itself (no subdirectories) and the name matches
  `^[1-9][0-9]*-[1-9][0-9]*\.bin$`; the temporary pattern is checked
  separately. Compare full paths (`Path.GetFullPath`); case-insensitive
  only on Windows.
- The directory is created lazily by the downloader (and by preflight
  when media is enabled); on Linux with mode `0700`, files with `0600`
  (`UnixCreateMode` — set it only on non-Windows). If the path exists as
  a file → error. The maintenance service never creates the store.
- Copy-in: stream the source into the `.part` file with your **own**
  `FileStream` — never `File.Copy`, which can carry the source's old
  timestamp and make a fresh file look abandoned to the orphan sweep
  (§3) — hashing while copying (`IncrementalHash`, SHA-256). If more
  than `MaxBytes` bytes are read, stop and delete the `.part`. Flush to
  disk, rename to the final name (replacing an existing one) and return
  `(path, sha256, size)` computed from what was written.

Downloader (`Media/MediaDownloader.cs`):

- After the TDLib file is complete and `≤ MaxBytes`: copy it into the
  store and store the **store** path with the sha256 and size from the
  copy (`UpdateMediaDownloaded`). The TDLib copy is left to
  `optimizeStorage`.
- A copy error is a normal failed attempt (attempts + 1, backoff); the
  `.part` is removed; the log carries the exception type only.
- **Disk guard**, checked before any TDLib request for the row: if
  (free space on the store's filesystem − the row's size) <
  `Capture:Storage:MinFreeBytes`, or (sum of the sizes of `downloaded`
  rows that have a local path + the row's size) >
  `Capture:Media:MaxTotalBytes`, then do not touch TDLib, defer the row
  by 300 seconds **without changing `attempts`**, and log a warning at
  most once per 10 minutes (sizes in MB only). If free space cannot be
  determined, do not block; warn once.
- Free space comes from an `IDiskSpaceProbe`
  (`long? GetAvailableFreeBytes(string path)` — for the nearest existing
  ancestor of the path; real implementation `SystemDiskSpaceProbe` via
  `DriveInfo`), registered with `TryAddSingleton` in `AddCaptureHandlers`
  and passed to `MediaDownloader` with `GetRequiredService`.
- `CaptureSyncRunner` is unchanged: it reads whatever `local_path` a row
  has. The existing runner-side Task 8 tests (files outside any store)
  must keep passing **unchanged**.

## 🔴 2. Cache schema v5

- `captured_media` gets `created_at INTEGER NOT NULL` — the queue time in
  unix seconds from the cache's `TimeProvider`. `QueueCapturedMedia` sets
  it; the row type exposes it.
- Migration 4 → 5 in one transaction, following the existing pattern.
  🔴 **Trap:** `Initialize` runs `CREATE TABLE IF NOT EXISTS captured_media (...)`
  before the version steps, so a v1–v3 file already receives the NEW
  definition (with `created_at`) before it reaches the 4 → 5 step. An
  unconditional `ALTER TABLE captured_media ADD COLUMN created_at ...`
  then fails with "duplicate column name" and the service can never
  start again. Add the column only if `PRAGMA table_info(captured_media)`
  does not list it.
- Rows that exist at migration time get `created_at = now` (a full
  retention window — never `0`, which would look ancient). Fresh files
  are created at version 5 directly. An index on `created_at` is created
  after the migration (see the existing comment "Yangi ustunga bog'liq
  indeks migratsiyadan KEYIN").
- Every existing table and row stays intact.

## 🔴 3. Media retention and the orphan sweep

Both run in the maintenance service (§5) **whether or not
`Capture:Media:Enabled` is true** — switching media off must not leave
files behind forever. Retention window = `Capture:CacheRetentionDays`
(existing, default 30): a file is only useful while its message can still
produce a `deleted` record, and that needs the message in
`message_cache`.

"**Orphan**" below means: no `message_cache` row with the same
`(chat_id, message_id)` AND no `capture_outbox` row with
`kind = 'deleted'` and the same `(peer_id, msg_id)`.

Decide and update the rows in **one** transaction; delete files only
**after** it commits:

1. `uploaded`, `failed`, `skipped`: the file is deleted, `local_path` set
   to NULL; the row itself is deleted once `created_at` < now − window.
2. `pending`: deleted when `created_at` < now − window, or when orphan.
3. `downloaded` and orphan: file and row deleted (the message left the
   cache without being deleted, or its record went out before the
   download finished).
4. A row with a `deleted` record still in `capture_outbox`: untouched —
   this overrides rules 1–3.
5. A `local_path` outside the store (Task 8 rows pointed into TDLib's
   cache) is never deleted as a file; the row is still updated by the
   rules above.

A file that cannot be deleted (locked, permission) → log the exception
type and leave it; the orphan sweep retries later.

**Orphan sweep:** top-level files in the store that match the final or
the temporary pattern, are not referenced by any
`captured_media.local_path`, and were last written more than 1 hour ago
→ deleted. Everything else — other names, subdirectories, fresh files —
is left alone. (A misconfigured store pointing at
`/var/lib/customsync-capture` itself must not cost the cache database,
the TDLib session or the master key.)

Nothing here creates records: retention is local (spec §0.3).

## 🔴 4. TDLib: three new allowed methods

Gate rules in `TdRequestPolicy` (violations →
`TdRequestNotAllowedException` before the transport, as for the others):

- `optimizeStorage` — allowed keys: `@type`, `@extra`, `size`, `ttl`,
  `count`, `immunity_delay`, `file_types`, `chat_ids`,
  `exclude_chat_ids`, `return_deleted_file_statistics`, `chat_limit`.
  Required: `size` integer `≥ 16777216`, `ttl` integer `≥ 3600`, `count`
  exactly `-1`, `immunity_delay` integer `≥ 600`. `file_types`,
  `chat_ids`, `exclude_chat_ids`: absent or empty arrays.
  `return_deleted_file_statistics`: absent or boolean. `chat_limit`:
  absent or integer `0..100`. Anything else — an unknown key, a number
  as a string, `null`, `-1` for `size`/`ttl`/`immunity_delay`,
  `count: 0` — is rejected.
- `getStorageStatisticsFast` — only `@type` / `@extra`.
- `setLogVerbosityLevel` — `new_verbosity_level` integer `0..2` only.

The comment next to the list: all three are local operations; they make
no request to Telegram's servers, mark nothing as read or viewed and do
not change the online status.

Configuration:

| Key | Default | Rule | Used as |
|---|---|---|---|
| `Capture:Storage:MaintenanceIntervalMinutes` | `10` | `1..1440` | loop interval |
| `Capture:Storage:TdlibFilesMaxBytes` | `536870912` | `16777216..1099511627776` | `size` |
| `Capture:Storage:TdlibFilesTtlHours` | `24` | `1..8760` | `ttl` = hours × 3600 |
| `Capture:Storage:TdlibImmunitySeconds` | `3600` | `600..604800` and `≥ 2 × Capture:Media:DownloadTimeoutSeconds` | `immunity_delay` |
| `Capture:Tdlib:LogVerbosity` | `1` | `0..2` | `new_verbosity_level` |

- The maintenance call: `size`/`ttl`/`immunity_delay` from configuration,
  `count = -1`, empty `file_types`/`chat_ids`/`exclude_chat_ids`,
  `return_deleted_file_statistics = true`, `chat_limit = 0`, timeout
  120 s. Its `storageStatistics` reply then describes what was deleted:
  use its `size` and `count` for the summary. An `error` reply or a
  `TdException` → log the type/code and continue.
- Log verbosity: `Worker` runs `setLogVerbosityLevel` with the
  configured level through `ITdClient.Execute` right after resolving
  `ITdClient` and **before** authorization (`setTdlibParameters`). Not
  fatal: on failure log a warning and continue.

## 🔴 5. `StorageMaintenance` (`src/CustomSync.Capture/Maintenance/StorageMaintenance.cs`)

- Registered in `CaptureHandlerRegistration.AddCaptureHandlers`
  (`Program.cs` already calls it — do **not** add a new extension that
  `Program.cs` would also have to call); dependencies resolved with
  `GetRequiredService`. Not a hosted service, and not injected into
  anything that is resolved before `Worker` reaches the point below
  (constructing it constructs `TdClient`, which calls native code).
- Started by `Worker` right after `MediaDownloader.Start` — after
  authorization **and** the invisibility step; never before them, not at
  all when they fail.
- Loop: one run immediately, then every `MaintenanceIntervalMinutes`.
  A run executes, in order, each step in its own `try/catch` (one failing
  step never skips the others or stops the loop):
  1. media retention (§3);
  2. orphan sweep (§3);
  3. `optimizeStorage` (§4);
  4. measurement → a `StorageSnapshot`;
  5. one `Information` summary line, plus warnings.
  Cancellation ends the loop quietly; a failing delay logs and ends it
  (as `PeriodicCachePruner` does). `PeriodicCachePruner` stays as it is
  (message cache and pending edits) — do not duplicate it.
- `StorageSnapshot` — a public record; 9b sends it, so keep these names:
  `TakenAt`, `ProcessRssBytes` (`Environment.WorkingSet`),
  `MemoryLimitBytes` (`long?`, cgroup v2 — below), `CacheDatabaseBytes`
  (`MessageCache.Stats().FileSizeBytes`), `MediaStoreBytes` and
  `MediaStoreFiles` (managed files actually on disk), `TdlibFilesBytes`
  and `TdlibDatabaseBytes` (`long?`, `files_size` / `database_size` of
  `getStorageStatisticsFast`), `FreeDiskBytes` (`long?`, the probe on the
  store directory), `OptimizeFreedBytes`, `OptimizeDeletedFiles`,
  `MediaRowsPruned`, `MediaFilesDeleted`. Expose the latest snapshot as a
  property.
- cgroup v2 memory limit: read `/proc/self/cgroup`, take the line
  `0::<path>`, read `/sys/fs/cgroup<path>/memory.max`; a number → bytes;
  `max`, missing, unparseable or not Linux → `null`. Both roots
  injectable for tests.
- Warnings: free disk < `MinFreeBytes`; RSS > 80 % of a known limit.
- The summary line: sizes in MB and counts only — no paths, file names
  or ids.

## 🔴 6. Preflight

- Every new key is validated against the ranges above, with clear
  messages.
- When `Capture:Media:Enabled` is true, `Capture:Media:StorageDirectory`
  must be rooted; must not be equal to, inside, or contain
  `Telegram:DatabaseDirectory` or `Telegram:FilesDirectory`; must not
  contain the cache database file, `Capture:Sync:StatePath` or
  `Capture:Sync:MasterKeyPath` (defaults
  `/var/lib/customsync-capture/device-state.json`, `.../master.key`).
  It is then created through `MediaStore` and checked to be writable,
  like the existing directory checks.
- When media is disabled the store is not checked (maintenance still
  cleans an existing one, §3).
- **Tests never create anything outside the temp folder.** Any existing
  test that enables media and runs preflight or `Worker` (search for
  `Capture:Media:Enabled`) must get a temp `Capture:Media:StorageDirectory`.

## 🔴 7. systemd unit `deploy/customsync-capture.service`

Start from the plan's unit and the existing `deploy/customsync.service`.
Required:

- `[Unit]`: `Description`, `After=network-online.target`,
  `Wants=network-online.target`.
- `[Service]`: `Type=exec` (the API unit explains why not `notify`),
  `User=customsync-capture`, `Group=customsync-capture`,
  `WorkingDirectory=/var/www/customsync-capture`,
  `ExecStart=/usr/bin/dotnet /var/www/customsync-capture/CustomSync.Capture.dll`,
  `Restart=always`, `RestartSec=15`,
  `Environment=DOTNET_ENVIRONMENT=Production`,
  `Environment=LD_LIBRARY_PATH=/var/www/customsync-capture`,
  `StandardOutput=journal`, `StandardError=journal`, `MemoryMax=1200M`,
  `MemoryHigh=900M`, `CPUQuota=100%`, `UMask=0077`,
  `StateDirectory=customsync-capture`, `StateDirectoryMode=0700`,
  `ReadWritePaths=/var/lib/customsync-capture`, `NoNewPrivileges=true`,
  `PrivateTmp=true`, `PrivateDevices=true`, `ProtectSystem=strict`,
  `ProtectHome=true`, `ProtectKernelTunables=true`,
  `ProtectKernelModules=true`, `ProtectControlGroups=true`,
  `RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX`,
  `RestrictSUIDSGID=true`, `LockPersonality=true`,
  `CapabilityBoundingSet=` (empty).
- `[Install]`: `WantedBy=multi-user.target`.
- Forbidden, each with a comment in the file where relevant:
  `MemoryDenyWriteExecute` (breaks the .NET JIT), `Type=notify`,
  `ASPNETCORE_ENVIRONMENT` (the generic host does not read it — it would
  only mislead).
- LF line endings: add `deploy/customsync-capture.service text eol=lf` to
  `.gitattributes`, as for the API unit.
- A short section in `deploy/README.md` (in Uzbek, like the rest):
  the separate system user, `StateDirectory`, running `--login`,
  `--enroll` and `--set-key` as that user, and a clear warning that
  deployment is stopped until the joint security audit.

Deviations from the plan's unit, to be recorded in `PROGRESS.md` §8: a
separate user (the API is internet-facing; the capture holds the Telegram
session and the master key — see "Deploy oldidan xavfsizlik auditi"),
`DOTNET_ENVIRONMENT` instead of `ASPNETCORE_ENVIRONMENT`, no dependency on
`customsync.service` (the capture may run on another host and talks to the
API over HTTPS), `StateDirectory`, the extra hardening.

## TESTS

1. `MediaStore`: `PathFor` rejects a non-canonical peer id and
   `msg_id ≤ 0`; `IsManaged` is true only for a pattern file directly in
   the store — false for another directory, a subdirectory, a `..` path,
   another name, a temporary name.
2. Downloader (real `TdClient`, fake transport whose `getMessage` reply
   points at a completed file in a temp "TDLib files" folder): row
   `downloaded`, `local_path` = the store path, content equal to the
   source, sha256/size from that content, no `.part` left, the source
   untouched.
3. A source larger than `MaxBytes` although TDLib reported a small size →
   `skipped`, nothing in the store (neither final nor `.part`).
4. Linux only (return early on Windows, as existing tests do): the store
   directory is `0700`, a stored file `0600`.
5. Disk guard, free space: a fake probe below `MinFreeBytes` → the
   transport sees no `getMessage`/`downloadFile`, `attempts` unchanged,
   next attempt = now + 300; a second due row within 10 minutes → no
   second warning.
6. Disk guard, budget: existing `downloaded` sizes + the row's size >
   `MaxTotalBytes` → deferred, no attempt spent.
7. Unknown free space (probe returns `null`) → the download proceeds; one
   warning.
8. Retention rule 1: `uploaded`/`failed`/`skipped` → file deleted,
   `local_path` NULL, row kept until the window passes, then deleted
   (drive time with a `TimeProvider`).
9. Retention rule 4: with a `deleted` outbox row present, the media row
   and its file survive for every status and any age.
10. Retention rule 3: `downloaded` orphan → file and row deleted;
    `downloaded` whose message is still cached → kept.
11. Retention rule 2: `pending` older than the window → deleted; `pending`
    orphan → deleted; fresh `pending` with a cached message → kept.
12. Retention rule 5: a `local_path` outside the store → that file still
    exists afterwards.
13. Retention and the sweep run with `Capture:Media:Enabled = false`.
14. Orphan sweep matrix: referenced → kept; unreferenced old final →
    deleted; unreferenced old `.part` → deleted; unreferenced fresh →
    kept; old file with another name → kept; subdirectory → kept.
15. Migration: cache files at versions 1, 2, 3 and 4 (built with the SQL
    those versions used, as the existing migration tests do) →
    `Initialize` → `user_version` 5, `created_at` present, v4
    `captured_media` rows kept with `created_at` = now, all other tables
    and rows intact; a fresh file → 5; `Initialize` twice → no error.
16. `QueueCapturedMedia` sets `created_at` from the `TimeProvider`.
17. Policy (`[Theory]`): the valid `optimizeStorage` shape passes; each of
    `count` 0 / 5, `immunity_delay` 0 / 599, `size` 0 / -1 / 16777215,
    `ttl` 0, `chat_ids: [1]`, `exclude_chat_ids: [1]`, a non-empty
    `file_types`, an unknown key, a number as a string, `null` → rejected
    before the transport.
18. Policy: `getStorageStatisticsFast` without parameters passes, with an
    extra key is rejected; `setLogVerbosityLevel` 0/1/2 pass, 3/5/-1/
    `"1"`/missing are rejected; Task 7's forbidden methods are still
    rejected.
19. Maintenance sends `optimizeStorage` with the configured `size`,
    `ttl`, `immunity_delay`, `count = -1`, empty arrays and
    `return_deleted_file_statistics = true`.
20. Step isolation: `optimizeStorage` answering `error`, and separately
    throwing `TdException` → retention and the sweep still ran, a
    snapshot was still made and logged, and the next run happens (fake
    delay).
21. Snapshot values: `CacheDatabaseBytes` = `Stats().FileSizeBytes`;
    `MediaStoreBytes`/`MediaStoreFiles` = managed files on disk; the TDLib
    fields from the fake `getStorageStatisticsFast` reply;
    `MemoryLimitBytes` from a fake cgroup root; `FreeDiskBytes` from the
    probe.
22. cgroup reader: a number → that value; `max` → null; no `0::` line →
    null; missing files → null.
23. Logs: the summary has sizes and counts and none of the store path,
    file names or ids (put marker strings into names); low free disk →
    warning; RSS above 80 % of the limit → warning.
24. `Worker` with the real `AddMessageCache` / `AddCaptureHandlers` /
    `AddCaptureSyncClient` registrations and a fake transport under the
    real `TdClient`, ordering checked on one shared timeline
    (`OnExecute` + `OnSend`): `setLogVerbosityLevel(1)` is executed before
    the first `setTdlibParameters`; `optimizeStorage` and
    `getStorageStatisticsFast` are sent only after the invisibility
    `getOption`; when invisibility fails neither is ever sent and the
    store is untouched.
25. Registration: `AddCaptureHandlers` resolves `StorageMaintenance`; the
    default `IDiskSpaceProbe` is `SystemDiskSpaceProbe`; it returns a
    positive value for the temp folder and for a not-yet-existing
    subfolder of it.
26. Preflight: each new key out of range → error; `TdlibImmunitySeconds`
    < 2 × `DownloadTimeoutSeconds` → error; `MaxTotalBytes` < `MaxBytes`
    → error; a store that is relative, equal to `FilesDirectory`, inside
    `DatabaseDirectory`, containing the cache database or containing
    `StatePath` → error; a valid temp setup → no error; media disabled →
    the store is not checked.
27. The unit file: no `\r`; every required directive with its value;
    none of the forbidden ones; every default writable path
    (`/var/lib/customsync-capture/tdlib`, `.../files`,
    `.../message-cache.db`, `.../media`, `.../device-state.json`) lies
    under a `ReadWritePaths` entry; `.gitattributes` has the `eol=lf`
    line.

Existing Worker-level tests will now also see `setLogVerbosityLevel`,
`optimizeStorage` and `getStorageStatisticsFast`. Update their
expectations explicitly; do not remove or weaken assertions.

Tests must not depend on the test machine's free disk space (the default
`MinFreeBytes` is 2 GiB): every test that constructs a `MediaDownloader`
and is not about the disk guard passes a fake `IDiskSpaceProbe`
reporting ample space. Adding that argument to the existing downloader
tests is allowed; changing their assertions is not — with one exception:
a downloader test that expected TDLib's path as `local_path` now expects
the store path.

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, then
   **rebuild with `--no-incremental`** before the next one:
   a) maintenance sends the plan's literal call (`count: 0`,
      `immunity_delay: 0`);
   b) the gate accepts `count: 0`;
   c) retention deletes media whose `deleted` record is still pending;
   d) retention deletes a file outside the store;
   e) the sweep deletes an unreferenced file whose name does not match;
   f) the sweep ignores the 1-hour age rule;
   g) maintenance skips retention when media is disabled;
   h) the disk guard spends an attempt;
   i) the downloader stores the TDLib path instead of the store copy;
   j) the 4 → 5 migration runs `ALTER TABLE ... ADD COLUMN` unconditionally;
   k) maintenance starts before the invisibility step;
   l) a failing `optimizeStorage` stops the rest of the run or the loop;
   m) the summary log contains the store path;
   n) the unit file gets `MemoryDenyWriteExecute=true`, or loses
      `ReadWritePaths`;
   o) the gate accepts `setLogVerbosityLevel` 5.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–7 implemented; tests 1–27 pass; all 15 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: a plan 05 Task 9a row whose commit column says
  `(this commit)` — **never invent a hash**; the header test count; the
  new configuration keys; the decisions from "WHY" (the store copy, the
  unsafe sample call, retention tied to the message cache); the §7
  deviations in §8; open items: TDLib's own message database
  (`use_message_database = true`) is measured but not bounded, and there
  is no eviction of old media when the budget is reached (downloads
  pause instead). Do **not** mark the task as verified — the TeamLead
  does that after an independent check.

## FINAL REPORT (six short points)

1. Commit hash (copied from `git log -1` after committing) and files
   changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–o), by test name.
4. Anything done differently from this prompt, and why.
5. TDLib facts this code relies on — the shape of the `optimizeStorage`
   reply with `return_deleted_file_statistics`, the
   `storageStatisticsFast` field names, whether `setLogVerbosityLevel`
   through `td_execute` takes effect before authorization, whether
   `optimizeStorage` can touch a file while `downloadFile` is writing
   it — each with its source, or "unconfirmed".
6. Any remaining way the disk could still fill up, the media of a
   deletion could still be lost, or a file outside the store could still
   be deleted. Report, do not fix.

## OUT OF SCOPE

Sending measurements to the backend (Task 9b), any server change,
evicting old media when the budget is reached, changing
`setTdlibParameters` (`use_message_database` and the growth of TDLib's own
database), SQLite `VACUUM`/WAL truncation, TDLib `deleteFile`, encrypting
the store at rest, Task 10, obtaining `libtdjson`, the owner's login,
deployment.
