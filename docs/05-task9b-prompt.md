Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 9b — capture health report to the backend

**Give this prompt only after Task 9a has been verified by the
TeamLead.** It builds on 9a's `StorageMaintenance` and `StorageSnapshot`.

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
  builds there.
- Starting state: the TeamLead's Task 9a verification commit or later
  (see the `PROGRESS.md` hand-off block), `dotnet build` 0 warnings, the
  full `dotnet test` green.
- The stale-test-vectors workaround (laptop), the PC NuGet workaround and
  the known intermittent test are exactly as described in
  `docs/05-task9a-prompt.md`, section REPOSITORY — apply them the same
  way.

## WHY THIS TASK MATTERS

Plan 05 Task 9, step 3: the capture service measures its own memory and
reports it to the backend through a **separate health endpoint — not as
a `setting` record** — so that the web app can show something like
"Capture service: 640 MB / 1200 MB". Task 9a already measures (and
logs) memory, disk and TDLib storage in a `StorageSnapshot` on every
maintenance run. 9b carries the latest snapshot to the server, keeps the
latest one per device, and gives the control plane (spec §9.1: "server
salomatligi") an admin endpoint to read it. The web page itself is plan
03.

Records are end-to-end encrypted user data; a health report is
operational data — sizes and counts only, never names, paths, ids or
anything from a message. That is why it travels outside the sync
protocol, in plaintext, and is never stored as a record.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block, §3, §4, the verification sections
   of plan 05 Tasks 6a, 6b, 8 and **9a**. Defects found there will be
   looked for again (unwired protection, a second door around a check,
   fakes that differ from the real server, logs that leak data).
2. Plan (READ-ONLY): plan 05 **Task 9**, step 3. Spec (READ-ONLY): §5.1,
   §5.7, §9.1.
3. Server: `Api/Program.cs` (snake_case JSON for HTTP and for EF),
   `Api/Endpoints/DeviceEndpoints.cs`, `Api/Endpoints/HealthEndpoints.cs`
   (the public `/api/v1/health` — leave it as it is), `Api/Auth/*`
   (roles, revocation), `Services/SettingsService.cs` (`CreateDefaults`),
   `Services/DeviceService.cs`, `Data/SyncDbContext.cs`,
   `Data/Entities/DeviceEntity.cs`, `Data/Migrations/` (how migrations
   are generated and applied), the device/auth endpoint tests and their
   fixtures.
4. Capture: `Maintenance/StorageMaintenance.cs` and `StorageSnapshot`
   (9a), `Sync/CaptureSyncRunner.cs` (token handling: access token,
   refresh-and-persist, the single retry after 401),
   `Sync/CaptureSyncHttpClient.cs`, `Sync/CaptureSyncRegistration.cs`,
   `Capture/CaptureHandlerRegistration.cs`, and how
   `ISyncedScopeSettingsSource` gets a null implementation in
   `AddCaptureHandlers` and the real one through `services.Replace` in
   the sync registration (Task 6b) — the same pattern is used here.

## NON-NEGOTIABLE RULES

- Do NOT run the API, the capture service or any CLI mode, no
  `dotnet run`. `dotnet build` / `dotnet test` only. Server tests use the
  existing PostgreSQL fixture; capture tests fake HTTP with an
  `HttpMessageHandler` whose responses follow the **real** endpoint
  defined in §1.
- A report carries **only** the fields listed in §1 — sizes and counts.
  Never a path, file name, peer/chat/message id, account id, text, key or
  token.
- The device id comes **only** from the caller's token, never from the
  body. The report time is the **server's** clock, never the client's.
- Health reporting never breaks the capture: no exception from it leaves
  the maintenance loop (except cancellation), and it never blocks
  maintenance for longer than one HTTP timeout.
- **K1** — the server's tunable lives in `server_settings`; the capture's
  in `IConfiguration`. **K6** — TDD: each test first, seen failing.
  **K7** — one commit, imperative subject, WHY in the body, **no
  `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. No change to the
  sync protocol, records, push or pull.

## 🔴 1. Server

1. `server_settings` default (in `SettingsService.CreateDefaults`):
   `health.stale_after_seconds` = `1800` (int, category `health`) — three
   times the capture's default maintenance interval.
2. Table `device_health` (EF entity + a migration named
   `AddDeviceHealth`, generated the way the existing migrations were):
   one row per device — `device_id` (primary key, foreign key to
   `devices.device_id`), `reported_at` (UTC), `rss_bytes`,
   `memory_limit_bytes` (nullable), `cache_db_bytes`, `media_store_bytes`,
   `media_store_files`, `tdlib_files_bytes` (nullable),
   `tdlib_database_bytes` (nullable), `free_disk_bytes` (nullable).
3. `POST /api/v1/devices/health` — any authenticated device (a revoked
   device is already rejected by the existing authentication). Body
   (snake_case, as the rest of the API):
   `rss_bytes`, `memory_limit_bytes`, `cache_db_bytes`,
   `media_store_bytes`, `media_store_files`, `tdlib_files_bytes`,
   `tdlib_database_bytes`, `free_disk_bytes`. Unknown properties are
   ignored (so a `device_id` or `reported_at` in the body has no effect).
   - Byte counts: integers `0..9007199254740991`; `media_store_files`:
     `0..1000000000`. `memory_limit_bytes`, `tdlib_files_bytes`,
     `tdlib_database_bytes`, `free_disk_bytes` may be `null`; the others
     are required. Anything else → `400`, nothing stored.
   - A body larger than 4096 bytes → `413`, nothing stored. Enforce it
     while reading (a missing or false `Content-Length` must not bypass
     it).
   - Upsert the caller's row; `reported_at` = server time. Response
     `204`.
4. `GET /api/v1/devices/health` — `admin` only. One entry per stored
   report: `device_id`, `name`, `platform`, `revoked` (bool),
   `reported_at`, `stale` (`now − reported_at > health.stale_after_seconds`),
   and the metrics. Ordered by device name. No audit entries (frequent,
   not sensitive).

## 🔴 2. Capture

1. `CaptureSyncHttpClient.PostHealthAsync(serverUrl, token, snapshot, ct)`
   → `Success` / `Unauthorized` / `Error`, sending exactly the §1 body
   built from 9a's `StorageSnapshot` (`ProcessRssBytes` → `rss_bytes`,
   `MemoryLimitBytes` → `memory_limit_bytes`, `CacheDatabaseBytes` →
   `cache_db_bytes`, `MediaStoreBytes` → `media_store_bytes`,
   `MediaStoreFiles` → `media_store_files`, `TdlibFilesBytes` →
   `tdlib_files_bytes`, `TdlibDatabaseBytes` → `tdlib_database_bytes`,
   `FreeDiskBytes` → `free_disk_bytes`). The per-run counters
   (`Optimize*`, `MediaRowsPruned`, `MediaFilesDeleted`) and `TakenAt`
   are not sent.
2. An `ICaptureHealthReporter` (`Task<bool> ReportAsync(StorageSnapshot, CancellationToken)`):
   - `AddCaptureHandlers` registers a null implementation with
     `TryAddSingleton` (reports nothing, returns false);
   - `AddCaptureSyncClient` replaces it with the real one through
     `services.Replace` — the same pattern as `ISyncedScopeSettingsSource`;
   - the real one goes through `CaptureSyncRunner`'s existing token
     handling (no second copy of it): it does nothing and returns false
     when `Capture:Sync:Enabled` is not true or the credentials cannot be
     loaded; on `401` it forces one refresh and retries once; any other
     failure returns false; it never throws except on cancellation; logs
     carry the exception type only.
3. `StorageMaintenance`: after step 5 of every run, call the reporter
   with that run's snapshot — as step 6, in its own `try/catch`. The
   reporter is resolved with `GetRequiredService`.

## TESTS

Server (existing fixture, real HTTP pipeline):

1. A valid `POST` with a device token → `204`; the row is stored under the
   token's device id; `reported_at` is within a few seconds of the
   server's now even though the body carries a `reported_at` far in the
   past.
2. A body naming another device's `device_id` → stored under the caller
   only; the other device's row is absent or unchanged.
3. Two `POST`s → one row, holding the second values.
4. `[Theory]` invalid bodies — a negative value, `9007199254740992`, a
   missing required field, a number as a string, `media_store_files`
   above the limit → `400` and the stored row unchanged.
5. A body of more than 4096 bytes → `413`, nothing stored.
6. No token → `401`; a revoked device's token → `401`.
7. `GET` as admin → the entry has `name`, `platform`, `revoked` and the
   metrics; after moving `reported_at` back past `health.stale_after_seconds`
   (a database update in the test) `stale` is true, otherwise false;
   changing the setting changes the result.
8. `GET` with a device-role token → `403`.
9. `health.stale_after_seconds` exists with default `1800`.

Capture:

10. `PostHealthAsync` sends `POST /api/v1/devices/health` with the bearer
    token and a body whose property set is **exactly** the §1 names, with
    the snapshot's values (nulls as `null`).
11. `401` → one refresh and one retry; a second `401` → false; count the
    requests (no loop).
12. `5xx` and a network error → false, no exception; the log has the
    exception type and no URL, token or path.
13. `Capture:Sync:Enabled` false → no request at all.
14. Real registrations: `AddCaptureHandlers` + `AddCaptureSyncClient` →
    the reporter is the real one; `AddCaptureHandlers` alone → the null
    one.
15. A `StorageMaintenance` run sends exactly one report carrying that
    run's snapshot; a reporter that throws does not stop the loop (the
    next run happens).

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, rebuild
   with `--no-incremental` before the next one:
   a) the server takes the device id from the body;
   b) the server stores a client-supplied `reported_at`;
   c) the server accepts a negative value;
   d) the 4096-byte limit is checked only through `Content-Length`;
   e) `GET` is allowed for any authenticated device;
   f) `stale` uses a hard-coded number instead of the setting;
   g) the capture reports while sync is disabled;
   h) the capture retries `401` without refreshing, or without a limit;
   i) `AddCaptureSyncClient` does not replace the null reporter;
   j) an exception from the reporter escapes the maintenance loop;
   k) the body gains an extra field (for example the store directory).
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–2 implemented; tests 1–15 pass; all 11 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: a plan 05 Task 9b row whose commit column says
  `(this commit)` — **never invent a hash**; the header test count; the
  new endpoints and the `health.stale_after_seconds` setting. Do **not**
  mark the task as verified.

## FINAL REPORT (six short points)

1. Commit hash (copied from `git log -1` after committing) and files
   changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–k), by test name.
4. Anything done differently from this prompt, and why.
5. The migration: its name, the SQL it creates, and how you checked it
   applies on the test database.
6. Anything a compromised or misbehaving device could still do through
   the new endpoint (load, misleading numbers, another device's row).
   Report, do not fix.

## OUT OF SCOPE

The web page (plan 03), alerting, keeping a history of reports (only the
latest is kept), health reports from tdesktop or other devices, the sync
protocol, deployment.
