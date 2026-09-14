Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 04 Task 7 — scheduled archive jobs, dry-run and disk thresholds

## REPOSITORY

- **Paths depend on the computer** (laptop and PC differ). First run
  `hostname`, then take `<server>` and `<tdesktop>` from the table in
  `<tdesktop>\docs\MACHINES.md` (find `<tdesktop>`: whichever of
  `C:\TBuild\tdesktop` / `D:\Oybek\Telegram\tdesktop` exists; otherwise the
  folder containing `custom_db.cpp`). Check every path exists before using
  it; `git -C <server> remote -v` must show `Oybek-M/customsync-server`.
  Never guess a path — if not found, stop and ask.
- Repo: `<server>` (`customsync-server`), branch `Oybek`. Push only to
  `origin Oybek`.
- Plan text (READ-ONLY, another repo):
  `<tdesktop>\docs\superpowers\plans\2026-07-29-multi-device-sync-04-storage-lifecycle.md`
  §Task 7. **Do not copy the plan blindly** — the requirements below
  override it where they differ, and each difference says why.
- Starting state: `dotnet build` 0 warnings, `dotnet test` 152/152.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md` §2 (Task 6 review + "known, deliberately left limits"),
   §3 ("must not break"), §4 (test rules).
2. `src/CustomSync.Services/Storage/PurgeService.cs` — `PreviewAsync`,
   `ExecuteAsync`, `ConfirmAsync`, `SweepOrphanedMediaAsync`,
   `ResolvePolicyAsync` (it already accepts implicit `retention.<kind>_days`
   ids).
3. `Storage/RetentionPolicy.cs` (`GetValidationError`), `Storage/Targets/*`,
   `StatsService.cs`, `SettingsService.cs`, `AuditService.cs`,
   `Data/Entities/ArchiveRunEntity.cs`, `src/CustomSync.Api/Program.cs`.
4. `tests/CustomSync.Tests/PurgeSafetyTests.cs` and `Fixtures/DatabaseFixture.cs`
   — copy their test wiring style.

## NON-NEGOTIABLE RULES

- **K1** — no config literals. Every tunable lives in `server_settings`
  (seed via `New(...)` in `SettingsService`) and is re-read **every cycle**,
  so a change through `SetAsync` takes effect without restart.
- **K4** — everything idempotent; running the job twice must not do
  twice the work.
- **K6** — TDD: write each failing test first, see it fail, then implement.
- **K7** — one commit (or a few focused ones), imperative subject, WHY in
  the body, **no `Co-Authored-By` trailer**.
- Never run `dotnet run` or start any server. `dotnet build` / `dotnet test`
  only.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`.
- Do not change the purge safety logic in `PurgeService` (keyset loop,
  seq-conditioned delete, orphan quarantine, confirmation). Task 7 is a
  **caller** of it. If you believe `PurgeService` needs a change, stop and
  report.

## 🔴 1. Split: pure schedule + testable runner + thin host

Timer code inside a `BackgroundService` cannot be tested reliably. Create:

- `Storage/ArchiveSchedule.cs` — pure static logic, no I/O:
  `IsDue(DateTime nowUtc, int hour, int minute, DateOnly? lastRunDate)`.
  Due when today's `hour:minute` UTC has passed AND `lastRunDate != today`.
  Invalid hour/minute (outside 0..23 / 0..59) → not due; the runner audits
  the config error instead of throwing.
- `Storage/ArchiveJobRunner.cs` — does one run:
  `Task<ArchiveJobReport> RunOnceAsync(DateTime nowUtc, CancellationToken ct)`.
  Takes `IServiceScopeFactory`, `IDiskProbe` (see §6) and nothing static.
- `Storage/ArchiveJobService.cs : BackgroundService` — only a loop:
  create a scope, read settings, ask `ArchiveSchedule`, call the runner,
  wait. Poll interval is not a business setting: a `private const` of one
  minute with a comment is acceptable here.

**Why scopes:** the hosted service is a singleton; `SyncDbContext`,
`SettingsService`, `PurgeService` are scoped. Never capture them in the
singleton.

Register in `Program.cs`: runner scoped, `IDiskProbe` singleton,
`AddHostedService<ArchiveJobService>()`. **Ten endpoint test classes
build the whole app with `WebApplicationFactory`** (`StatsTests`,
`AuditTests`, …), so the hosted service WILL start inside them, against the
same test database. It must never act there by accident: the first cycle
may start only after the host has fully started, it must tolerate a
database that is not migrated/seeded yet (log and retry next cycle, never
crash the host), and with default settings (`jobs_enabled = false`) its only
side effect is the daily threshold audit + day row.
Because "due" depends on the wall clock (after 03:30 UTC it is due on the
first cycle), leaving it on in those tests makes audit-counting tests
time-of-day flaky. So: **remove `ArchiveJobService` from every
`WebApplicationFactory` used by tests** (one shared helper, not ten copies)
— the job is tested through `ArchiveJobRunner` directly. Existing test
assertions must not be edited to tolerate job rows.

## 🔴 2. "Once per UTC day" must survive restarts and double instances

An in-memory "last run" flag re-runs the job after every restart past
03:30. With `ManualDownloadTarget` that creates a new staged archive each
time. And `AuditService` stamps `DateTime.UtcNow`, so the audit log cannot
serve as a clock-injectable marker.

- New entity + migration: `archive_job_runs(run_date date PK, started_at,
  finished_at null, status, summary jsonb/text null)`.
- The run **claims** its day atomically:
  `INSERT ... ON CONFLICT (run_date) DO NOTHING`, check affected rows.
  0 rows → another run owns today → return a "skipped" report, do nothing.
- Crash mid-run leaves `finished_at = NULL`; the day is NOT retried. That is
  the safe direction (fewer deletions). Document it in a comment.
- Missed schedule: server down at 03:30, up at 10:00 → runs once that day.
  Missed previous days are not replayed.

## 🔴 3. What a run does, in order

Settings (add to seeds, category `storage`):

```
storage.jobs_enabled              bool  false  Scheduled purge is on
storage.jobs_dry_run              bool  true   Only preview and audit, delete nothing
storage.jobs_hour                 int   3      UTC hour
storage.jobs_minute               int   30     Minute
storage.jobs_max_batches          int   10     ExecuteAsync repeats per policy per run
storage.disk_capacity_mb          int   0      Capacity in MB (0 = detect from disk)
storage.warn_percent              int   80     Warning threshold
storage.critical_percent          int   92     Critical threshold
```

`disk_capacity_mb`, not the plan's `disk_capacity_bytes int`: bytes
overflow `int` at ~2 GiB and `GetIntAsync` would throw. MB matches the
existing `storage.quota_total_mb`.

**Why `jobs_dry_run` defaults to true:** turning on `jobs_enabled` first
shows, in the audit log, what WOULD be deleted. Real deletion needs a
second, deliberate switch.

Run:

1. **Thresholds (§6)** — always, even when `jobs_enabled` is false (it is
   read-only). Scheduling runs daily regardless; only steps 2–5 are gated.
2. If `jobs_enabled` is false → finish the day row, stop.
3. **Policy list**, deterministic:
   - stored policies with `Enabled`, ordered `Priority desc, PolicyId ordinal`;
   - then implicit `retention.<kind>_days` for every kind whose value is > 0,
     in `RecordKind` order. Without this the server-side retention promised
     by spec §0.3 (e.g. `retention.activity_days = 90`) never happens.
   - skip `never_delete` (nothing to execute — it only protects);
   - skip invalid policies (`GetValidationError(minDays) != null`) with
     audit `archive_job.policy_skipped` + reason.
4. **For each policy, in its own fresh scope:**
   - `PreviewAsync`. If `MatchedCount == 0` → audit and continue.
   - Dry-run → audit `archive_job.dry_run` with the preview numbers
     (if `MatchedCount == limit`, mark `at_least: true`) and continue.
     No `ExecuteAsync`, no `archive_runs` row.
   - Policy target requires explicit confirmation AND the policy already has
     an `awaiting_confirmation` run → skip with reason
     `pending_confirmation`. Otherwise every night stages a duplicate
     archive of the same records.
   - `ExecuteAsync`; repeat while `Status == completed` and
     `DeletedCount > 0` and `MatchedCount == limit`, at most
     `jobs_max_batches` times (catches up a large backlog without an
     unbounded loop).
   - Audit `archive_job.policy_result` (policy id, status, matched, deleted,
     freed bytes, run id).
5. **Orphan sweep** — `SweepOrphanedMediaAsync(nowUtc)` once per run, own
   scope, even if no policy matched (PROGRESS §2: otherwise quarantined
   blobs are only cleaned in runs that happen to delete records).
   **Not in dry-run.**
6. Finish the day row (`finished_at`, status, summary) and audit
   `archive_job.finished`.

## 🔴 4. Failure isolation — and why a fresh scope per policy

Plan: "on error move to the next policy". Catching the exception is not
enough: a failed `SaveChanges` leaves entities tracked, and the NEXT
policy's `SaveChanges` re-sends them and fails too. So:

- new `IServiceScope` (fresh `SyncDbContext` + `PurgeService`) per policy,
  and a separate fresh scope for writing the failure audit;
- `ExecuteAsync` returning `Success == false` (`failed_upload`,
  `failed_verification`) is a failure in the report, not an exception —
  continue;
- catch `Exception` per policy → audit `archive_job.policy_failed`
  (policy id, exception type, message) → continue;
- **`OperationCanceledException` when `ct` is cancelled must propagate** —
  shutdown stops the run, it is not "the next policy".

## 🔴 5. Things the job must NEVER do

- Call `ConfirmAsync`. Confirmation is a human action (plan 03 UI).
- Delete anything in dry-run (no execute, no sweep, no staging change).
- Create tombstones (spec §0.3).
- Delete staged archives. `DeleteStagedAsync` stays without a caller: an
  `awaiting_confirmation` archive may be the only copy, and "confirmed"
  does not prove the user downloaded it. Instead, audit
  `archive_job.staging_report` with file count and total bytes of
  `ListStagedAsync`. Deletion is a user decision in plan 03.
- Delete MORE because a threshold is exceeded. Thresholds only report.

## 🔴 6. Disk thresholds

- `IDiskProbe` interface: `(long TotalBytes, long FreeBytes)? Probe(string path)`
  with a `DriveInfo` implementation for `Storage:MediaRoot`. Tests use a fake
  — never the real disk.
- `disk_capacity_mb > 0` → used = `StatsService.SummaryAsync(...).TotalBytes`
  (CustomSync's own budget), capacity from the setting.
  `disk_capacity_mb == 0` → used = `Total - Free` of the probe (real disk,
  other files count). Probe returns null → audit `storage.threshold_unknown`.
- Validate `1 <= warn < critical <= 100`; invalid → audit config error,
  skip the check, do not throw.
- `>= critical` → audit `storage.threshold_critical` only;
  `>= warn` → `storage.threshold_warning`. Detail: percent, used, capacity,
  source (`setting` / `disk`), and `DaysUntilFull` from `SummaryAsync`.
- Web banner: out of scope (plan 03).

## 7. Small cleanup assigned to this task

`StatsService.SummaryAsync` duplicates the aggregation in `StorageAsync`.
Extract one private helper used by both. The JSON of existing endpoints
must not change; `StatsTests` / `StorageMetricsTests` stay green unchanged.

## WHAT NOT TO ADD

No new HTTP endpoints, no Web UI, no S3/SFTP/Telegram targets, no advisory
locks (the day-claim row covers overlap), no persisted keyset cursor
(the 50 000 scan starvation limit stays documented, not fixed here), no
change to `PurgeService` behaviour.

## TESTS — `ArchiveJobTests.cs` (each test its own `peer_hash`)

1. `ArchiveSchedule`: before 03:30 not due; 03:30 due; ran today → not due;
   next day due; first check at 10:00 → due; changed hour picked up;
   hour 24 / minute 60 → not due.
2. Day claim: two `RunOnceAsync` same day (new runner instances = restart)
   → second skipped, nothing executed twice.
3. `jobs_enabled = false` → no `archive_runs`, nothing deleted, threshold
   audit still written.
4. Dry-run: matching old records stay, no `archive_runs` row, an orphan blob
   older than 24 h still exists, `archive_job.dry_run` audit has counts.
5. Real run, `delete_only` implicit `retention.activity_days` → old activity
   deleted, young stays, tombstone stays.
6. `never_delete` protecting a record → record survives the job.
7. Isolation: policy A's target throws in `UploadAsync`, policy B
   (`delete_only`) still deletes; `archive_job.policy_failed` written.
8. Poisoned context: make A fail at DB level (e.g. a target id that makes
   `ExecuteAsync` fail after tracking changes, or a fault you can justify)
   and prove B still commits — this is the fresh-scope test.
9. Manual target over two days → exactly one `awaiting_confirmation` run,
   one staged file, `pending_confirmation` skip on day 2, no deletion.
10. Batching: backlog > limit → multiple `ExecuteAsync` rounds, capped by
    `jobs_max_batches` (use a small limit; see PROGRESS §2 on page-size
    clamps hiding loops).
11. Cancellation during the policy loop → `OperationCanceledException`
    escapes, later policies not run.
12. Thresholds with fake probe: 85 % → warning, 95 % → critical only, probe
    null → unknown, warn ≥ critical → config error, no throw; setting-based
    capacity path uses `SummaryAsync`.
13. Settings re-read: change `jobs_dry_run` via `SetAsync` between two days
    → second day behaves differently without new service instances.

## HOW TO VERIFY

1. Every test above written first and seen failing.
2. `dotnet build` 0 warnings; `dotnet test` all green (152 + new).
3. Deliberate breaks — do each, confirm at least one test fails, revert:
   a) remove the per-policy scope (share one scope);
   b) drop the `pending_confirmation` skip;
   c) call `SweepOrphanedMediaAsync` in dry-run;
   d) swallow `OperationCanceledException` in the per-policy catch;
   e) replace the day claim with an in-memory `lastRunDate`;
   f) skip implicit `retention.<kind>_days` policies.
   If a break does NOT fail a test, add or fix the test.

## DEFINITION OF DONE

- All sections 1–7 implemented; tests 1–13 pass; all 6 breaks caught.
- Migration generated with the pinned 8.0.x tooling, applies on a fresh
  `DatabaseFixture` database.
- `PROGRESS.md`: plan 04 Task 7 row filled; deviations from the plan listed
  with reasons (split files, `archive_job_runs`, `disk_capacity_mb`,
  `jobs_dry_run`, `jobs_max_batches`, implicit policies, no staging
  deletion); §5 open items updated (staging cleanup → plan 03 user action,
  `SummaryAsync` duplication resolved).
- Committed, pushed to `origin Oybek`, working tree clean.

## FINAL REPORT (seven short points)

1. Commit hash(es) and files changed.
2. Test count before/after; exact `dotnet test` summary line.
3. Which deliberate break failed which test (a–f).
4. Anything from this prompt you did differently, and why.
5. Audit action names you emitted (exact strings).
6. Anything in `PurgeService` you found suspicious but did NOT change.
7. Open risks you see for enabling this on the VPS.

## OUT OF SCOPE

Plan 04 Task 4 (S3/SFTP), Task 5 (Telegram bot), Task 8 (Web UI, banner),
confirmation UI, staging deletion, keyset cursor persistence, deploy.
