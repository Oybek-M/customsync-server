Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 11 — Telegram session protection (before any deploy)

**Give this prompt only after the previous task has been verified by the
TeamLead** (see the `PROGRESS.md` hand-off block). This task was added to
plan 05 by the user's decision of 2026-09-16 (`PROGRESS.md`, section
"Plan 05 ga qo'shiladigan alohida vazifa: sessiya himoyasi") and must be
done **before** the capture is ever deployed.

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
- **`<tdesktop>` is READ-ONLY** and not needed for this task.
- Starting state: the TeamLead's latest verification commit (see the
  `PROGRESS.md` hand-off block), `dotnet build` 0 warnings, the full
  `dotnet test` green.
- The stale-test-vectors workaround (laptop), the PC NuGet workaround and
  the known intermittent test are exactly as described in
  `docs/05-task9a-prompt.md`, section REPOSITORY — apply them the same
  way.

## WHY THIS TASK MATTERS

The capture keeps a **complete Telegram session** on the VPS. Whoever
copies TDLib's database directory has full access to the owner's
account; neither the password nor two-step verification stops them. The
VPS has already been compromised once (a crypto miner), which is why
deployment is stopped until a joint security audit. The runbook alone is
not enough: protection has to hold in code, in every mode, and a revoked
session has to be noticed.

The TeamLead found these gaps while preparing this task — each one is a
requirement below:

1. **A revoked session goes unnoticed.** After authorization the
   `Worker` only loops `Task.Delay`; nothing watches later
   `updateAuthorizationState` updates. If the owner terminates the
   session from their phone, the capture keeps running, keeps sending
   health reports and silently captures nothing.
2. **Restart storm.** "Not authorized" exits with code 1 and the unit has
   `Restart=always` / `RestartSec=15`: a revoked session restarts the
   service — and opens a new TDLib connection to Telegram — every 15
   seconds, forever.
3. **`--login` runs outside systemd.** `deploy/README.md` runs it with
   `sudo -u customsync-capture …`, where the umask is usually `0022`;
   preflight creates the TDLib directories with
   `Directory.CreateDirectory(path)` (default mode) and SQLite creates
   the message cache `0644`. The very first copy of the session can be
   world-readable. The unit's `UMask=0077` does not apply to this run.
4. **TDLib's database is not encrypted.** `setTdlibParameters` sends no
   `database_encryption_key`: a backup, snapshot or stray copy of the
   state directory is a working session.
5. **No preflight checks modes or ownership** of the directories that
   hold the session, the message cache and media, nor of
   `appsettings.Production.json` (it holds `api_hash`). Only the device
   key files are checked (`DeviceCredentials`, ≤ 0600).
6. **Permission checks run too late.** `Worker` opens the message cache
   (`CaptureCacheStartup`) and the sync client **before**
   `CapturePreflight.Check`, so files exist before anything verifies
   where they are created.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block; the sections "Plan 05 ga
   qo'shiladigan alohida vazifa: sessiya himoyasi", "Deploy oldidan
   xavfsizlik auditi" and the `libtdjson` decision; §3, §5, §7; the plan
   05 Task 7 and 9a sections and their verification; §8 entries for
   Tasks 7, 9a and the TeamLead's exit-code fix (`Program.cs` now only
   calls `CaptureProgram.RunAsync`, which returns the `Worker`'s exit
   code — before, `return 0` hid every failure from systemd; you extend
   `CaptureProgramTests`).
2. `deploy/customsync-capture.service`, `deploy/README.md` (the capture
   section and "Zaxira").
3. Capture: `Program.cs`, `CaptureProgram.cs`, `Worker.cs`,
   `Tdlib/AuthorizationGate.cs`, `Tdlib/TdAuthenticator.cs`
   (`setTdlibParameters`, `IsClosed`, the interactive / non-interactive
   modes), `Tdlib/TdlibRegistration.cs`, `Tdlib/TdRedactor.cs`,
   `Preflight/CapturePreflight.cs`, `Sync/DeviceCredentials.cs` (the
   existing mode check and atomic 0600 write), `Media/MediaStore.cs` (the
   existing 0700 directory creation), `Capture/CaptureCacheStartup.cs`,
   `Sync/CaptureSyncRegistration.cs` (`CaptureSyncStartup`); tests
   `ProcessExitCodeCollection.cs`, `CaptureProgramTests.cs`,
   `CaptureInvisibilityTests.cs` Test13 (the `Worker`-level pattern).
4. TDLib (`github.com/tdlib/td`, a tagged release or a commit — **cite
   the URL and commit in the report**): `setTdlibParameters`
   (`database_encryption_key`, `bytes` are base64 in JSON), the
   `authorizationState*` types, and in the source (`td/telegram/
   AuthManager.cpp`, `Td.cpp`) what happens when the session is
   terminated from another device (which states are sent, whether local
   data is destroyed) and when the database key is wrong (which error or
   state comes back).

## NON-NEGOTIABLE RULES

- Do NOT run the capture service, TDLib or any CLI mode, no
  `dotnet run`, no `systemd-run`, no `systemctl`. `dotnet build` /
  `dotnet test` only. TDLib is faked at `ITdTransport`.
- No real secret anywhere: tests generate random keys in temp files;
  nothing secret is logged, put in an exception message or committed.
- **Every protection goes through the production path** (the real entry
  point, registrations and `Worker`), with a test that fails when the
  wiring is removed. A hand-built `ServiceCollection` does not count.
- Unix-only behaviour (umask, modes, owners) is decided by pure code
  tested on **every** OS through a seam; the real system calls are
  covered by Linux-only tests that skip elsewhere — and the report says
  which tests did not run on your machine.
- Never weaken an existing check (invisibility gate, `DeviceCredentials`
  0600, preflight). No auto-`chmod` of an existing directory: an
  unexpected mode is reported and the service refuses to start.
- **K6** — TDD: each test first, seen failing. **K7** — one commit,
  imperative subject, WHY in the body, **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. No change to the
  sync protocol, the API, records, push or pull.

## 🔴 1. A revoked or closed session stops the service — once, loudly

- After authorization is ready, the capture keeps watching
  `updateAuthorizationState`. Any state other than
  `authorizationStateReady` while the host is **not** shutting down —
  `LoggingOut`, `Closing`, `Closed`, `WaitPhoneNumber` or any other —
  logs **one** error (the state type only), stops the background loops
  (media downloader, maintenance, sync) and stops the host with **exit
  code 78** (`EX_CONFIG`: the session needs the owner, restarting cannot
  help). States seen during our own shutdown are ignored.
- Exit code 78 also for: "not authorized" at startup (the gate), a
  TDLib parameters or database-key failure (§3), and a preflight
  failure. Every other abnormal stop (for example the gate's timeout)
  keeps exit code 1, so systemd retries it.
- The unit gets `RestartPreventExitStatus=78`; `Restart=always` and
  `RestartSec=15` stay. A test reads `deploy/customsync-capture.service`
  and pins that line, so the code and the unit cannot drift apart.
- The service mode never tries to log in again by itself (it never
  prompts); only `--login` does.

## 🔴 2. Files that hold the session — permissions in code, fail closed

- **umask `0077`** is set at process start on Unix in **every** mode
  (service, `--login`, `--enroll`, `--set-key`), before the host is built
  and before any file is opened.
- Every directory the capture creates is created with mode **0700**:
  `Telegram:DatabaseDirectory`, `Telegram:FilesDirectory`, the directory
  of `Capture:CacheDatabasePath`, the media store (already 0700) and the
  directory of the device key files.
- Preflight on Unix: each of those directories, when it exists, must be
  owned by the current user and have **no group or other bits**;
  `appsettings.<Environment>.json` in the content root, when present,
  must have no bits for others and no group write. Otherwise preflight
  fails (exit 78, §1). Messages name the setting and the expected mode —
  never file contents.
- **Order:** the permission checks run before anything creates or opens
  files in those directories — move the cache and sync start after
  preflight if that is what it takes, with a test that fails if the
  order is reversed.

## 🔴 3. TDLib database encryption key

- `setTdlibParameters.database_encryption_key` carries the key read from
  the file named by `Telegram:DatabaseEncryptionKeyFile`, or — when that
  setting is absent — from `$CREDENTIALS_DIRECTORY/tdlib-db-key`
  (systemd `LoadCredential`). The file holds base64 text of **at least
  32 random bytes** (surrounding whitespace ignored); it is sent in
  TDLib's base64 `bytes` form.
- Preflight, in every mode that creates a TDLib client: no key, an
  unreadable file, invalid base64, fewer than 32 bytes, or a file with
  group/other bits outside `$CREDENTIALS_DIRECTORY` → error (exit 78).
- `--login` and the service send **the same** key (a test drives both).
- `database_encryption_key` is added to `TdRedactor`'s sensitive keys
  (test), and appears in no log or exception message.
- A wrong key: handle the answer TDLib actually gives (from your source
  research, cited) — exit 78 with a message telling the owner to check
  the key; nothing is deleted.
- The unit gets `LoadCredential=tdlib-db-key:/etc/customsync-capture/tdlib-db-key`.
- No database exists yet, so there is no migration of an unencrypted
  one. Changing the key later means a new `--login` (or TDLib's
  `setDatabaseEncryptionKey`, which this task does not add) — the
  runbook says so.

## 4. The session's name in Telegram

The owner finds the session in Telegram → Settings → Devices by its
name. Pin `device_model` ("CustomSync Capture") and
`application_version` with a test, so a refactor cannot make the session
anonymous.

## 🔴 5. Runbook — `deploy/README.md`, capture section

Text only; mark every command "not yet run — verify during the audit".

- **Inventory table**: path → what it holds → owner:group and mode →
  backed up? Rows at least: TDLib database directory (the session; full
  account access), TDLib files directory, message cache (message texts),
  media store, device key files, the database key file,
  `appsettings.Production.json` (`api_id`, `api_hash`, server URL). The
  capture's state is **never** in backups; a re-login is the recovery
  path. Contabo snapshots contain all of it — treat them as secrets and
  delete them after use.
- **Login** with the same sandbox as the service, so §2's umask, the
  state directory and §3's credential are the same: give the exact
  `systemd-run --pty …` command (user, `LoadCredential`, `UMask=0077`,
  `StateDirectory`, working directory, `DOTNET_ENVIRONMENT`), or explain
  why `sudo -u` is kept and how the key file is made readable for it.
  The same for `--enroll` and `--set-key`.
- **Never put a secret in `Environment=`** — `systemctl show` prints it
  to every local user.
- **Generating and installing the database key** (`openssl rand -base64
  32`, owner and mode, never in backups).
- **Revocation**: terminate "CustomSync Capture" from another Telegram
  client **first** (on a compromised VPS nothing done on the VPS can be
  trusted); what the journal then shows, that the service stays stopped
  with 78, how to wipe the session directories, how to log in again.
  "Terminate all other sessions" on the phone ends this session too.
- **After a suspected VPS compromise**: revoke from the phone, revoke the
  capture device on the server (admin), replace the database key, log in
  again only on a clean system.
- Enable two-step verification before the first login.

## TESTS

Minimum — through the real entry point / registrations / `Worker`,
TDLib faked at the transport:

1. After ready: `LoggingOut` then `Closed`; `Closed` alone;
   `WaitPhoneNumber` → one error line, loops stopped, the process exit
   code is 78, "running" not logged again.
2. A state change during shutdown → no error, exit code 0.
3. Startup not authorized → 78; the gate's timeout → 1; preflight
   failure → 78. The exit code is the one `CaptureProgram.RunAsync`
   returns (extend `CaptureProgramTests`), not only
   `Environment.ExitCode`.
4. The unit file contains `RestartPreventExitStatus=78` and the
   `LoadCredential` line.
5. umask: the seam is called before the host is built and before any
   file is opened, in all four modes; a Linux-only test shows a file
   created afterwards is 0600.
6. Directory creation with 0700 for every directory in §2 (seam on every
   OS; Linux-only real check).
7. Preflight decisions: 0700 own → ok; 0750, 0705, another owner →
   error; config file 0640 → ok, 0644 or 0660 → error; absent config file
   → ok.
8. Order: preflight failure → the cache file and the TDLib directories
   were not created.
9. Key: from the setting; from `$CREDENTIALS_DIRECTORY` when the setting
   is absent; missing, bad base64, 31 bytes, file with group bits →
   error; the same key in `--login` and service mode; the key never in
   any captured log line, with `TdRedactor` redacting it.
10. Wrong-key answer (the shape you found in TDLib's source) → 78,
    nothing deleted.
11. `device_model` / `application_version` pinned.

## HOW TO VERIFY

1. Every test written first and seen failing (or, for behaviour that
   already works, seen failing under the matching break).
2. `dotnet build` 0 warnings; full `dotnet test` **three times** —
   report all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, rebuild
   with `--no-incremental` before the next one:
   a) the post-ready watcher is not subscribed;
   b) a revoked session exits with 1;
   c) the process returns 0 whatever the `Worker` decided;
   d) `RestartPreventExitStatus` removed from the unit;
   e) umask not set in `--login`;
   f) the TDLib database directory created with the default mode;
   g) preflight accepts a 0750 directory;
   h) preflight accepts a directory owned by another user;
   i) a world-readable config file accepted;
   j) the cache opened before preflight;
   k) `database_encryption_key` missing from `setTdlibParameters`;
   l) the key not redacted;
   m) a 31-byte key accepted;
   n) a wrong-key answer exits with 1.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- §1–§5 implemented; tests 1–11 pass; all 14 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: a plan 05 Task 11 row whose commit column says
  `(this commit)` — **never invent a hash**; the header test count; §8
  entry with the TDLib findings; the "Deploy oldidan xavfsizlik auditi"
  section gets the items only a real system can check (marked open). Do
  **not** mark the task as verified.

## FINAL REPORT (six short points)

1. Commit hash (copied from `git log -1` after committing) and files
   changed.
2. Test count before/after; the three `dotnet test` summary lines; which
   Linux-only tests were skipped on your machine.
3. Which deliberate break failed which test (a–n), by test name.
4. Anything done differently from this prompt, and why.
5. TDLib findings with evidence (file, line, commit): the states sent
   when the session is terminated elsewhere, whether local data is
   destroyed, the wrong-key behaviour, the `bytes` encoding.
6. What the audit must still check on the real VPS (the `systemd-run`
   login, `LoadCredential`, real modes and owners, snapshots, anything
   else you noticed). Report, do not fix.

## OUT OF SCOPE

A `--logout` mode (revocation must work from outside a possibly
compromised VPS — Telegram's Devices screen is the authoritative path);
encrypting the message cache (SQLCipher); a session-state field in the
health report (an API change — a stopped service already shows as
stale); nginx, TLS, firewall and the rest of the audit; running anything;
protocol changes; the web app.
