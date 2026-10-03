Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 10b — end-to-end chains against the real API (in-process)

**Give this prompt only after Task 10a has been verified by the
TeamLead.** It reuses 10a's contract-test harness (the real capture
registrations wired to the real API through
`CustomSyncWebApplicationFactory`).

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
  builds there. Read documents and source files with
  `git -C <tdesktop> show origin/Oybek:<path>`.
- Starting state: the TeamLead's Task 10a verification commit or later
  (see the `PROGRESS.md` hand-off block), `dotnet build` 0 warnings, the
  full `dotnet test` green.
- The stale-test-vectors workaround (laptop), the PC NuGet workaround and
  the known intermittent test are exactly as described in
  `docs/05-task9a-prompt.md`, section REPOSITORY — apply them the same
  way.

## WHY THIS TASK MATTERS

Plan 05 Task 10 (manual, blocked until the VPS audit) checks three
things a user would see: a chat outside the scope produces nothing, one
event seen by tdesktop **and** the capture becomes **one** record, and
the media of a deleted message survives. 10a proved the capture and the
server agree on the HTTP contract. 10b drives the same three chains end
to end through the real code on both sides, so that what Task 10 will
check by hand is already known to work.

The second chain is the most valuable one. Dedup works only if the
capture computes **the same `record_id` as tdesktop** for one event
(spec §3.1 property). That depends on the capture turning TDLib chat ids
into tdesktop's canonical peer ids, the server message id, `msg_date` /
`edit_date` and the account id exactly as tdesktop does. If any of them
differs, every event seen by both clients becomes two records and the
archive silently doubles. Today this is checked only against the
capture's own mapping code.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block, §3, §4, §5, the verification
   sections of plan 05 Tasks 4a, 5, 6b, 8 and **10a**, and §8 entries for
   Tasks 5, 8 and 10a.
2. Plan (READ-ONLY): plan 05 **Task 10** (steps 2 and 3, acceptance
   criteria). Spec (READ-ONLY): §0.5, §0.12, §0.14, §3.1, §3.2,
   §3.2.1, §3.4, §5.3. `test-vectors.json`: `account_hash`, `peer_hash`,
   `record_id`.
3. tdesktop (READ-ONLY): `Telegram/SourceFiles/data/data_peer_id.h` —
   tdesktop's canonical peer id is `bare | (shift << 48)` with
   `UserId` shift 0, `ChatId` shift 1, `ChannelId` shift 2. Combined with
   TDLib's chat ids (user → `user_id`; basic group → `−group_id`;
   supergroup or channel → `−1000000000000 − supergroup_id`) this gives
   the expected peer id for every chat type **independently of the
   capture's `TdIdMapper`** — the tests must compute it that way.
4. The 10a harness and tests: `tests/CustomSync.Tests/CaptureContractTests.cs`
   (`CaptureContractCollection` — `DisableParallelization`, shares one
   `CustomSyncWebApplicationFactory`; `CreateCaptureHarnessAsync`,
   `CaptureHarness`, `CreateEnrolledDeviceAsync`, `CreateAdminClientAsync`,
   `GetCurrentMaxSeqAsync`, `LoadVectorCase1`). These helpers are
   `private` today: move them into a shared test helper that both files
   use — do not copy them — and keep every 10a test unchanged and green.
   Put the 10b tests in the same collection. `Capture/CaptureScopeEvaluator.cs`
   (server `Capture:Scope:Block` / `Allow`, synced snapshot,
   `DefaultEnabled`), `Media/*`, `Sync/CaptureSyncRunner.cs` (media
   path), `Api/Endpoints/MediaEndpoints.cs`.

## NON-NEGOTIABLE RULES

All rules of `docs/05-task10a-prompt.md` (section NON-NEGOTIABLE RULES and
WHEN THE TWO SIDES DISAGREE) apply unchanged: no process or port, real
code on both sides, outbox rows only through the real handlers,
verification through the server's own API and decryption, temp
directories only, `occurred_at` near now, setting changes only in a
`DisableParallelization` collection with restore in `finally`, no
`Thread.Sleep`, K6, K7 (**no `Co-Authored-By` trailer**), nothing written
to `<tdesktop>` or `docs/sync-protocol/`. In addition:

- The "tdesktop" side of a test is a second enrolled device that pushes
  records it builds **from the spec** (§3.1, §0.12, §0.14 — the same
  functions the vector tests pin) with peer ids computed as STEP 0
  item 3 says. It never calls the capture's mapping or record-building
  code for the values under test.
- A mismatch between the capture and tdesktop's definition is fixed in
  the capture (tdesktop is the canonical producer, spec §3.2.2 says so
  for activity and the same holds for ids), with a failing test first.
  If the spec and tdesktop's code disagree, stop and report.

## SCENARIOS (one test each)

1. **Scope chain** (plan Task 10 step 2). The "tdesktop" device pushes the
   eight message scope settings (§3.2.1), chosen so that tdesktop's own
   chain (Task 5's `ShouldAntiDelete`, see its prompt and §8 entry)
   enables AntiDelete for chats A and B and not for an unlisted chat C;
   the capture's server config blocks chat B (`Capture:Scope:Block`) and
   leaves `DefaultEnabled` false. After a capture pull, a message received
   and deleted in each of A, B and C (fake TDLib updates) gives exactly
   one record on the server — chat A's (the server block wins over the
   synced settings). An edit in a chat whose AntiEdit the settings turn
   off gives no `edited` record.
2. **One event, one record** (plan Task 10 step 3). For a private chat, a
   basic group and a supergroup (and a channel if the handlers accept
   one): the "tdesktop" device pushes the `deleted` record of a message
   with an **earlier** `observed_at`; the capture then receives and
   deletes the same message through TDLib updates and pushes. The server
   holds **one** record per event, the capture counts each as a
   duplicate, and the stored payload's `peer_id` and `account_id` equal
   tdesktop's values. The same for one `edited` event with the same
   `edit_date`.
3. **Media of a deleted message.** Media capture is enabled for a chat; a
   document message arrives, the downloader stores the file (fake
   `downloadFile` answer pointing at a temp file); the message is
   deleted; one sync cycle sends `HEAD` (404), `PUT` with `X-Nonce` and
   the record with its media reference. A second device's
   `GET /api/v1/media/{hash}` returns the blob and its `X-Nonce`; it
   decrypts to the original bytes, whose SHA-256 is the referenced hash
   (spec §0.5: hash of the plaintext). A second deleted message with the
   same file is pushed after `HEAD` 200 with **no** second `PUT`, using
   the stored nonce.
4. **Media limits** (serial collection). With the server's
   `media.max_upload_bytes` below the file size the real 413 makes the
   capture push the record **without** media and mark the media
   `skipped`; with the device's storage quota exhausted the real 507
   keeps the outbox row for a later cycle and nothing is pushed without
   its media. Both exactly as Task 8 decided (`PROGRESS.md` §8, Task 8
   entry) — the point is that the real server's refusals are the ones
   the capture recognises.
5. **Health from the registered maintenance.** One
   `StorageMaintenance.RunOnceAsync` from the real registrations, with
   sync enabled against the real API, makes the admin
   `GET /api/v1/devices/health` list the capture device with that run's
   snapshot.

## HOW TO VERIFY

1. Every test written first and seen failing (or, for behaviour that
   already works, seen failing under the matching break).
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, rebuild
   with `--no-incremental` before the next one:
   a) `TdIdMapper` gives channels shift 1 instead of 2;
   b) the capture's `deleted` record uses the deletion time instead of
      `msg_date` as `occurred_at`;
   c) the payload's `peer_id` carries the TDLib chat id instead of the
      canonical peer id;
   d) the synced whitelist is ignored (`DefaultEnabled` decides);
   e) a synced whitelist overrides the server block;
   f) the media `PUT` is sent without `X-Nonce`;
   g) the media reference carries the SHA-256 of the ciphertext;
   h) the capture `PUT`s again after `HEAD` 200;
   i) a 413 keeps the outbox row waiting instead of pushing without
      media;
   j) the registered `StorageMaintenance` gets no reporter.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Scenarios 1–5 implemented as tests and passing; all 10 breaks caught;
  every mismatch handled as the rules say.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: a plan 05 Task 10b row whose commit column says
  `(this commit)` — **never invent a hash**; the header test count; §8
  entry with every mismatch found. Do **not** mark the task as verified.

## FINAL REPORT (six short points)

1. Commit hash (copied from `git log -1` after committing) and files
   changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–j), by test name.
4. Anything done differently from this prompt, and why.
5. Every capture ↔ tdesktop or capture ↔ server mismatch found (chat
   types, ids, times, media): symptom, the defining source (spec section
   or tdesktop file), the fix and its test — or "none" with the
   scenarios that prove it.
6. What Task 10 still has to check by hand that these tests cannot
   (real TDLib update shapes, invisibility on a real account, 48-hour
   memory, anything else you noticed). Report, do not fix.

## OUT OF SCOPE

Real TDLib or tdesktop; running any server; activity-signal changes
(`photo`, Task 4d); session protection (Task 11); nginx, TLS,
deployment; the web app; protocol changes.
