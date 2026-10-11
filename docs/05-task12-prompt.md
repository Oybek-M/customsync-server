Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 12 — move the whole solution to .NET 10 LTS (before the first deploy)

**Give this prompt only after the previous task has been verified by the
TeamLead** (see the `PROGRESS.md` hand-off block). Added by the owner's
decision of 2026-10-11.

## REPOSITORY

- **Paths depend on the computer** (laptop and PC differ). First run
  `hostname`, then take `<server>` from the table in
  `<tdesktop>\docs\MACHINES.md`. Find `<tdesktop>`: whichever of
  `C:\TBuild\tdesktop` / `D:\TBuild\tdesktop` / `D:\Oybek\Telegram\tdesktop`
  exists; otherwise the folder containing `Telegram\SourceFiles\custom_db.cpp`.
  Check every path exists before using it; `git -C <server> remote -v`
  must show `Oybek-M/customsync-server`. Never guess a path — if not found,
  stop and ask.
- Repo: `<server>` (`customsync-server`), branch `Oybek`. Push only to
  `origin Oybek`. **`<tdesktop>` is READ-ONLY** and not needed otherwise.
- **SDK:** `dotnet --list-sdks` must show a `10.0.x` SDK (the laptop has
  `10.0.401`). If it does not, stop and report — the owner installs it.
- Starting state: the TeamLead's latest commit (see the `PROGRESS.md`
  hand-off block), `dotnet build` 0 warnings, full `dotnet test` green
  (592 passed, 2 Linux-only skipped, 594 total).
- The stale-test-vectors workaround (laptop), the PC NuGet workaround and
  the known intermittent tests (`SyncEndpointsTests.Pull_returns_pushed_records_and_advances_cursor`,
  `SyncEndpointsTests.Tombstone_stored_even_when_target_does_not_exist_yet`
  — shared dev database, `PROGRESS.md` §5) are exactly as described in
  `docs/05-task9a-prompt.md`, section REPOSITORY — apply them the same way.

## WHY THIS TASK MATTERS

.NET 8 support ends on **2026-11-10**; after that it gets no security
fixes. Nothing is deployed yet, so moving now is the cheapest moment:
the first production deploy will run on .NET 10 (LTS, supported until
November 2028) instead of being migrated under a running service. The
server is internet-facing and the capture holds a complete Telegram
session — an unpatched runtime is not acceptable for either.

This is a **pure platform upgrade**: behaviour, wire format, database
schema and cryptography stay exactly as they are. The existing suite
(including the machine-checked `test-vectors.json` contract) is the
safety net — it must keep passing without being weakened. The one
place where .NET 10 can silently change what we ship is hosting: §4.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before changing anything

1. `PROGRESS.md`: the hand-off block, §3, §4, §5, §7, and the §8 entries
   for Tasks 7, 9a and 11 (hosting order, preflight, exit codes).
2. `global.json`, every `*.csproj`, `src/CustomSync.Api/Program.cs`,
   `src/CustomSync.Capture/CaptureProgram.cs`, `Worker.cs`,
   `src/CustomSync.Services/Storage/ArchiveJobService.cs`,
   `src/CustomSync.Data/Migrations/SyncDbContextModelSnapshot.cs`,
   `tests/CustomSync.Tests/Fixtures/DatabaseFixture.cs`,
   `tests/CustomSync.Tests/CaptureProgramTests.cs`,
   `src/CustomSync.Capture/Sync/SyncCrypto.cs`, `deploy/README.md`,
   `deploy/*.service`, `README.md`.
3. The official breaking-change lists (Microsoft Learn / Npgsql docs) for:
   .NET 9 and .NET 10 (runtime, libraries, SDK — incl. NuGet audit of
   transitive packages), ASP.NET Core 9 and 10, EF Core 9 and 10,
   Npgsql 9 and 10 and the Npgsql EF Core provider 9 and 10, C# 13 and 14.
   Three you must check explicitly:
   - **Hosting (.NET 10):** `BackgroundService` now runs the whole of
     `ExecuteAsync` as a background task — the part before the first
     `await` no longer runs inside host start-up. `Worker` (capture) and
     `ArchiveJobService` (API) are `BackgroundService`s. See §4.
   - **EF Core 9+:** `Migrate()` throws when the model has changes that
     no migration covers (`PendingModelChangesWarning` is an error by
     default). See §3.
   - **C# 14 (the default for `net10.0`):** first-class `Span` conversions
     change overload resolution — e.g. `array.Reverse()` can bind to
     `MemoryExtensions.Reverse` (reverses in place), and `array.Contains(x)`
     inside an EF query binds to `MemoryExtensions.Contains`. Search for
     such calls on arrays. Also `field` / `extension` used as identifiers.
   Keep a table: item → does it affect this code? → what you did → URL.

## NON-NEGOTIABLE RULES

- Do NOT run the server, the capture, TDLib or any CLI mode: no
  `dotnet run`, no `systemctl`. `dotnet build` / `dotnet test` only.
  No database commands by hand (`dotnet ef database update` is forbidden;
  only the tests touch the dev database, as today).
- **Nothing observable changes:** HTTP routes, status codes and JSON
  shapes, sync record format, push/pull, crypto (key derivation, wrap,
  fingerprints), log redaction, exit codes, file modes. If an upgrade
  forces a behaviour change, stop and report it. The only allowed change
  is the exit-code fix in §4, if the tests there show it is needed.
- **No warning is silenced to reach 0.** Fix the cause. A `NoWarn`,
  `#pragma` or `<NuGetAudit>false` is acceptable only when there is no
  fix, one instance at a time, with a comment and a line in the report.
  `PendingModelChangesWarning` is never suppressed.
- Never delete or weaken a test. If a test must change because of the
  platform (not because of a behaviour change), list it with the reason.
- **K6** — TDD: the framework guard test first, seen failing on `net8.0`;
  the §4 exit-code tests before any fix in `Worker`. **K7** — one commit,
  imperative subject, WHY in the body, **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>`, `docs/sync-protocol/`, `CLAUDE.md` or the
  old prompts in `docs/`. No change to the sync protocol.

## 1. Target framework and SDK

- All six projects (`CustomSync.Api`, `.Capture`, `.Core`, `.Data`,
  `.Services`, `CustomSync.Tests`) target `net10.0`.
- `global.json`: `"version": "10.0.100"`, `rollForward: latestFeature`
  stays — it then works with any installed 10.0 SDK on either computer
  (pinning `10.0.401` would reject a PC that has an older 10.0 band).

## 2. Packages

- Every package moves to the **latest stable** version that supports
  `net10.0` (EF Core / Npgsql / provider / naming conventions as one
  matching set). **Exact versions** — no floating `8.0.*` wildcards: the
  build must be reproducible, and NuGet audit tells us when to update.
- **Remove packages nothing uses.** `Swashbuckle.AspNetCore` and
  `Microsoft.AspNetCore.OpenApi` are referenced by `CustomSync.Api` but no
  code calls Swagger/OpenAPI — confirm with a search and a build, then
  remove them (less attack surface on an internet-facing server). Any
  other unused reference you find: same rule.
- `dotnet list package --vulnerable --include-transitive` (or
  `dotnet package list …`) → **0** for every project; put the output in
  the report.
- Test packages: stay on xUnit v2 and VSTest (latest 2.x / compatible
  `Microsoft.NET.Test.Sdk`); no move to xUnit v3 or Microsoft.Testing.Platform.

## 3. EF Core migrations

- API start-up (`Program.cs`) and `DatabaseFixture` call `MigrateAsync()`;
  on EF Core 9+ that throws if the model has changes no migration covers.
- Add a permanent test: `SyncDbContext` configured exactly as production
  configures it (provider **and** naming convention — if that setup is
  inline in `Program.cs`, extract one shared method instead of copying
  it), no connection opened, `Database.HasPendingModelChanges()` is false.
- If it reports changes after the upgrade, explain each one. Add a
  migration **only** for a real model difference caused by the upgrade,
  review its SQL and say in the report why it is needed; for that use
  `dotnet-ef` 10.x as a repo-local tool (`.config/dotnet-tools.json`,
  exact version), never a global install. The snapshot's
  `ProductVersion` annotation alone is not a reason for a migration.

## 4. Hosting and exit codes (the .NET 10 `BackgroundService` change)

The capture's preflight runs in `Worker.ExecuteAsync` before its first
`await`. On .NET 8 that part ran inside host start-up; on .NET 10 it runs
on a background task while the host finishes starting.

- **Order.** Inside `Worker` the order stays: preflight → cache/sync →
  TDLib client.
  `CaptureSessionProtectionTests.Test08_Preflight_Failure_Leaves_Cache_And_Tdlib_Directories_Uncreated`
  guards it. Check that nothing else — another hosted service, a
  singleton resolved during start-up — creates or opens files before the
  preflight has finished.
- **Exit codes.** `CaptureProgram.RunAsync` returns `Environment.ExitCode`,
  and systemd decides restarts from the process exit code
  (`CaptureProgramTests` explains why). An exception escaping `Worker` is
  handled by the host (`BackgroundServiceExceptionBehavior.StopHost`:
  logged, host stopped) — on .NET 10 also one thrown before the first
  `await`, which on .NET 8 failed start-up instead. Add tests (in
  `ProcessExitCodeCollection`) where an injected fake makes `Worker`
  throw an unexpected exception **(a)** before its first `await` (e.g.
  during preflight) and **(b)** later, after start-up: `RunAsync` must
  return non-zero or throw — **never 0**. Normal shutdown (cancellation)
  still returns 0. If (a) or (b) returns 0, fix it in `Worker` (log the
  exception type, `Fail(1)`), and say in the report whether .NET 8 already
  had the problem. A capture that stops with exit 0 is not restarted and
  nobody notices.
- **API.** Check `ArchiveJobService` and the tests that rely on its
  timing; if something relied on the old behaviour, fix it properly — no
  `Task.Delay`/sleep. Report (do not change) what exit code the API
  process ends with when `ArchiveJobService` throws.

## 5. Docs

- `deploy/README.md`: runtime requirement — ASP.NET Core Runtime 10.0 for
  `customsync.service`, .NET Runtime 10.0 for `customsync-capture.service`.
  The VPS also runs other .NET 8 applications: installing 10.0 must not
  remove or replace 8.0. **The installation method is decided during the
  VPS deploy** — write it as an open item (`# not yet run — decided at
  deploy`), not as verified commands.
- `README.md`: the stack line (`.NET 8` → `.NET 10`).

## TESTS

1. **Framework guard (write first, see it fail on `net8.0`):** every
   `CustomSync.*` assembly (Api, Capture, Core, Data, Services, Tests)
   carries `TargetFrameworkAttribute` `.NETCoreApp,Version=v10.0`, and the
   test process runs on runtime 10 (`Environment.Version.Major == 10`).
2. The pending-model-changes test (§3).
3. The exit-code tests (§4 a, b).
4. The existing suite unchanged and green.

## HOW TO VERIFY

1. `dotnet build` 0 warnings (NuGet audit included); full `dotnet test`
   **three times** — report all three summary lines and the count
   (594 + your new tests).
2. Deliberate breaks — do each, confirm a test fails, revert, rebuild
   with `--no-incremental` before the next one:
   a) one project left at `net8.0` → the framework guard;
   b) the PBKDF2 iteration count changed in `SyncCrypto` → the crypto
      vector tests (proves the contract still runs on .NET 10);
   c) one JSON property of a pushed sync record renamed → a contract test
      (name it);
   d) the cache started before preflight in `Worker` → `Test08`, under
      .NET 10 hosting;
   e) a new property on an entity without a migration → the
      pending-model-changes test, and `DatabaseFixture`'s `MigrateAsync()`
      refuses (proves the EF guard is active, not suppressed);
   f) only if §4 needed a fix: revert it → an exit-code test.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- §1–§5 done; new tests pass; every break caught; 0 warnings;
  0 vulnerable packages.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: a plan 05 Task 12 row whose commit column says
  `(this commit)` — **never invent a hash**; the header test count; a §8
  entry with the version table, the breaking-change table (short) and the
  §4 exit-code finding. Do **not** mark the task as verified.

## FINAL REPORT (seven short points)

1. Commit hash (copied from `git log -1` after committing) and files changed.
2. Package table: name, old version → new version; removed packages.
3. Breaking-change table: item → affects us? → action → URL.
4. Test counts; the three `dotnet test` summary lines; skipped tests.
5. Which break (a–f) failed which test, by test name.
6. §4 exit codes: what (a) and (b) returned before any fix, what was
   fixed, what the API does when `ArchiveJobService` throws;
   `dotnet list package --vulnerable --include-transitive` output;
   anything suppressed, with the reason.
7. Anything done differently from this prompt and why; what the VPS
   deploy must still do for .NET 10 (report, do not do).

## OUT OF SCOPE

Other applications on the VPS; installing anything on the VPS; Docker;
xUnit v3 or Microsoft.Testing.Platform; Native AOT, trimming,
`LibraryImport` rewrites (unless needed for 0 warnings); new features;
API, protocol or schema changes; the web app.
