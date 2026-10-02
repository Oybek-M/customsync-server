Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 10a — capture ↔ real API contract tests (in-process)

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
  builds there. Read the plan, the spec and `test-vectors.json` with
  `git -C <tdesktop> show origin/Oybek:<path>`.
- Starting state: HEAD `ac32f79` or later (see the `PROGRESS.md` hand-off
  block), `dotnet build` 0 warnings, full `dotnet test` 532/532.
- The stale-test-vectors workaround (laptop), the PC NuGet workaround and
  the known intermittent test are exactly as described in
  `docs/05-task9a-prompt.md`, section REPOSITORY — apply them the same
  way.

## WHY THIS TASK MATTERS

Plan 05 Task 10 is the manual end-to-end check: tdesktop closed, a
message deleted, the text still reaches the archive; 48 hours without
memory growth. It needs the real Telegram session, tdesktop and a
deployed server, so it is **blocked** until the VPS security audit.

One part of it can be proven now, and it is the riskiest part: **the
capture service and the server have never talked to each other.** Every
capture test fakes HTTP with a handler written by hand "after the real
endpoint". A mismatch — a field name, a status code, a header, an error
body — would surface only on the VPS, inside a service that has to run
unattended. `WebApplicationFactory` runs the real API pipeline
(authentication, revocation, validation, PostgreSQL) in memory inside
`dotnet test`: no port, no process, nothing is "run" in the sense of the
no-servers rule. This task wires the **real** capture code to that
**real** server and pins the contract.

10a covers the device lifecycle and the record contract. 10b (separate
prompt) covers the end-to-end chains: tdesktop scope settings → capture
handlers → push, media upload, and dedup against a record built the way
tdesktop builds it (from the `record_id` vectors).

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block, §3, §4, §5 and the verification
   sections of plan 05 Tasks 6a, 6a-2, 6b, 8, 9a and **9b**. The defects
   found there will be looked for again — above all **tests that build
   their own world** (rows inserted by hand in a shape production never
   writes, fakes that differ from the real server) and **unwired
   protection**.
2. Plan (READ-ONLY): plan 05 Task 6 and **Task 10**. Spec (READ-ONLY):
   §0 (revision), §3.1 (`record_id`), §3.2 and §3.2.1 (kinds, payloads,
   scope keys), §3.2.1a, §3.4 (dedup: the smaller `observed_at` wins,
   ties by the smaller `device_id`), §4.4.0 (key wrap format), §5.1–5.3
   (endpoints, `seq` cursor, push). `test-vectors.json` — its
   `key_wrap` section is the cross-platform contract for wraps.
3. Server: `Api/Program.cs` (snake_case JSON), `Api/Endpoints/DeviceEndpoints.cs`,
   `SyncEndpoints.cs`, `KeyEndpoints.cs`, `Api/Auth/*` (revocation
   cache), `Services/DeviceService.cs`, `Services/SyncService.cs`,
   `tests/CustomSync.Tests/Fixtures/*` (the factory uses ONE shared dev
   database for every test class), `DedupConflictTests.cs`,
   `DeviceHealthEndpointsTests.cs` (how tests enroll devices and issue
   tokens).
4. Capture: `Sync/SyncCliCommands.cs` (`EnrollAsync`, `SetKeyAsync` —
   both take an `HttpClient` and an `IConsolePrompt`),
   `Sync/CaptureSyncHttpClient.cs`, `Sync/CaptureSyncRunner.cs`
   (`SyncCycleAsync`, `PushCycleAsync`, `PullCycleAsync`,
   `ReportHealthAsync`, the token handling and its lock),
   `Sync/CaptureSyncRegistration.cs` (`AddCaptureSyncClient` builds its
   `HttpClient` from a registered `HttpMessageHandler`),
   `Sync/SyncCrypto.cs`, `Sync/DeviceCredentials.cs`,
   `Capture/CaptureHandlerRegistration.cs`.
   `tests/CustomSync.Tests/CaptureStorageVerificationTests.cs` Test01 and
   `CaptureHealthVerificationTests.cs` Test01 show how the TeamLead
   builds the capture service from its **real registrations** with a
   fake `ITdTransport` that pushes TDLib updates — use that style.

## NON-NEGOTIABLE RULES

- Do NOT run the API, the capture service or any CLI mode as a process;
  no `dotnet run`, no ports. `dotnet build` / `dotnet test` only. The
  server side of every test is `CustomSyncWebApplicationFactory`; the
  capture's `HttpClient` is built on `factory.Server.CreateHandler()`.
- **Real code on both sides.** Enrollment through
  `SyncCliCommands.EnrollAsync`, key setup through
  `SyncCliCommands.SetKeyAsync`, sync through `CaptureSyncRunner` /
  the registered services, health through `ReportHealthAsync`. No
  hand-written request where the capture already has code for it, no
  fake server responses, no test-only switch in `src/`.
- **Outbox rows reach the outbox through the real capture handlers**
  (TDLib updates pushed through a fake `ITdTransport`), never by
  inserting rows directly. Verify on the server through its own API
  (a second device's `GET /api/v1/sync/pull`, or the admin
  `GET /api/v1/records`) and decrypt the payload with the master key —
  do not read capture tables to decide what the server holds.
- Every capture file (cache database, state, master key, media store)
  lives in a per-test temp directory that is deleted afterwards. Never
  the default `/var/lib/...` paths.
- **Shared database.** All server test classes run in parallel against
  one database. Use `occurred_at` / message dates near **now** (purge
  and retention tests in other classes delete old records — `PROGRESS.md`
  §5). Unique device names per test. A test that changes a server
  setting lives in a test collection with `DisableParallelization = true`
  and restores the setting in `finally`.
- No `Thread.Sleep`; wait on conditions with a timeout.
- **K6** — TDD: each test first, seen failing (or, for a test that pins
  behaviour that already works, seen failing under the matching break
  below). **K7** — one commit, imperative subject, WHY in the body,
  **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. No protocol
  change.

## 🔴 WHEN THE TWO SIDES DISAGREE

A contract test that fails because capture and server disagree is the
point of this task — **do not bend the test to pass**. Decide which side
is wrong by the spec and `test-vectors.json`:

- the side that contradicts them is fixed, with a failing test first;
- if the server matches the spec, fix the capture (tdesktop talks to the
  same server — the server contract does not move for the capture);
- a server change must leave every existing server test green;
- if the spec is silent or ambiguous, **stop and report** — no protocol
  decision in this task.

Every mismatch goes into report point 5, fixed or not.

## SCENARIOS (one test each, server = real API in memory)

1. **Enroll.** An admin enrollment code (created through `DeviceService`,
   as the server tests do) is redeemed by `SyncCliCommands.EnrollAsync`
   (code and device name from a fake `IConsolePrompt`). The state file
   holds the server's `device_id` and refresh token; the admin
   `GET /api/v1/devices` lists the device with the given name, platform
   `service` and role `device`. A used code is refused on a second
   enrollment.
2. **Key setup.** A second device ("tdesktop", enrolled through
   `DeviceService`) uploads a password key wrap with
   `POST /api/v1/keys/wraps`, built from a `key_wrap` vector of
   `test-vectors.json`. `SyncCliCommands.SetKeyAsync` (wrap choice,
   vector password and confirmation from the fake prompt) writes a
   master key file equal to the vector's master key (and `0600` on
   Linux). A wrong password writes nothing.
3. **Refresh and rotation.** After enrollment the runner's first cycle
   refreshes against the real server and persists the rotated refresh
   token; the old refresh token is now refused by the real server
   (`RefreshTokenAsync` with it returns null). A runner rebuilt from the
   state file (a restart) still syncs.
4. **Push.** A `deleted`, an `edited` and an `activity` outbox row are
   produced by the real handlers (fake TDLib updates). One
   `SyncCycleAsync` pushes them; the server holds three records from the
   capture's `device_id` with the expected `kind`, `msg_id`,
   `occurred_at` and `peer_hash`; each payload decrypts with the master
   key to what the spec prescribes for that kind (the deleted text, the
   edited old/new text, the status encoding of §3.2.2, and `account_id`
   / `peer_id` of §0.14). The capture marks the rows delivered.
5. **Duplicate.** A second capture device (own enrollment, same master
   key) records the same event — same chat, message and time — and
   pushes it: the server keeps **one** record (spec §3.4), the second
   capture counts it as a duplicate and does not retry it. With a fake
   clock, make the later push carry the **smaller** `observed_at` and
   show the stored record is replaced as §3.4 says.
6. **Batch limit** (serial collection). With the server's
   `sync.push_batch_size` lowered below the capture's batch, the real
   server's refusal is understood: the capture shrinks its batch and all
   rows arrive over the next cycles, none lost or duplicated.
7. **Pull of settings.** The "tdesktop" device pushes `setting` records
   (all eight message scope keys of §3.2.1 — the snapshot stays null
   until every one is present, Task 6b — encrypted with the same master
   key) through the real `POST /api/v1/sync/push`. `PullCycleAsync` stores them, the
   capture's scope snapshot reflects them, the cursor is persisted, and a
   second pull applies nothing again.
8. **Health.** `ReportHealthAsync` with a snapshot → the admin
   `GET /api/v1/devices/health` lists the capture device with exactly the
   snapshot's values.
9. **Revocation.** The admin revokes the capture device
   (`DELETE /api/v1/devices/{id}`). On the next cycle the push is refused,
   the refresh is refused, the runner stops (`IsStopped`) and sends no
   further requests — count them with a `DelegatingHandler` around the
   TestServer handler. A health report afterwards returns false.

## HOW TO VERIFY

1. Every test written first and seen failing (see K6 above).
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a scenario test fails, revert,
   rebuild with `--no-incremental` before the next one:
   a) the capture's enrollment body sends the device name under another
      property name;
   b) `RefreshAndPersistTokenAsync` persists the old refresh token instead
      of the rotated one;
   c) one property of a pushed record is serialised in camelCase;
   d) the capture treats the server's duplicate status as an error;
   e) the capture ignores the server's batch-limit refusal;
   f) the pull cursor is not persisted (every pull starts from 0);
   g) the health body sends `rss` instead of `rss_bytes`;
   h) the runner keeps retrying after the refresh is refused;
   i) `SetKeyAsync` requests the wrap by its label instead of its id;
   j) the server keeps the later observation instead of the earlier one
      (§3.4).
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Scenarios 1–9 implemented as tests and passing; all 10 breaks caught;
  every mismatch handled as the section above says.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: a plan 05 Task 10a row whose commit column says
  `(this commit)` — **never invent a hash**; the header test count; a
  §8 entry describing the contract-test harness and every mismatch
  found. Do **not** mark the task as verified.

## FINAL REPORT (six short points)

1. Commit hash (copied from `git log -1` after committing) and files
   changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–j), by test name.
4. Anything done differently from this prompt, and why.
5. Every capture ↔ server mismatch found: symptom, which side was wrong
   by which spec section, the fix and its test — or "none" with the
   scenarios that prove it.
6. What these tests still do NOT prove (real TDLib, real network and
   TLS, nginx, clock skew, more than one API process, anything else you
   noticed). Report, do not fix.

## OUT OF SCOPE

The scope chain from tdesktop settings to handler decisions, media
upload and dedup against a tdesktop-built record of the same event (10b); real TDLib or
tdesktop; running any server; nginx, TLS, deployment; the web app;
protocol changes.
