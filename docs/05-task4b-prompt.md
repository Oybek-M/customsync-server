Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 4b — activity capture (status, name, username changes)

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
- **`<tdesktop>` is READ-ONLY for this task.** No edits, no commits, no
  pushes, no builds there — another session owns it.
- Plan text (READ-ONLY): `<tdesktop>\docs\superpowers\plans\2026-07-29-multi-device-sync-05-capture-service.md`
  §Task 4 (the `updateUserStatus` / `updateUser` rows). The requirements
  below override the plan where they differ.
- Starting state: HEAD `835c15e` or later, `dotnet build` 0 warnings,
  `dotnet test` 249/249.

## WHY THIS TASK MATTERS

`activity` is the last-seen bypass: while the owner's desktop is closed,
nobody records when a watched person came online or renamed themselves,
and Telegram never replays status changes. The 24/7 service closes that
gap — but only if its records are **indistinguishable from the ones
tdesktop writes**. Spec §0.13: activity records from different accounts
and devices are merged on purpose by identical `record_id`. A different
field name, value encoding or `msg_id` means duplicates and a broken
timeline instead of a merged one.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md` §2, §3, §4 and the verification sections for plan 05
   Tasks 4a and 5 (the same classes of defect will be looked for again).
2. Spec (READ-ONLY) `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`:
   §0.3 (retention), §0.12, **§0.13 (activity is merged across
   accounts)**, §0.14, §3.1, §3.2. `docs\sync-protocol\test-vectors.json`
   — the `activity` cases.
3. **The reference implementation — READ-ONLY:** in
   `<tdesktop>\Telegram\SourceFiles` find everything that produces
   activity: `custom_activity_history.*`, the `Kind::Activity` enqueue
   site(s), `CustomSettings::ShouldTrackActivity`,
   `ActivityHistoryTrackAllContacts`, `ActivityBufferMinutes`, the
   activity Include/Exclude lists, and the `BuildActivity` (or
   equivalent) payload builder. **Write down, before coding, a table of
   exactly what tdesktop does** — you will put it in the report:
   - which fields are tracked (names as written in the payload);
   - how each value is encoded as a string (every status kind: online
     with expiry, offline with was_online, recently, last week, last
     month, empty/unknown; names; username);
   - what `msg_id` it passes for activity (see section 3);
   - what `occurred_at` is (observed time, `was_online`, …);
   - the noise filter / buffer: what is suppressed, what is merged, the
     time window and its default;
   - first observation of a user: event with `has_old_value=false`, or
     no event?
   - scope chain and what "contact" means.
4. Existing capture code: `Capture/CaptureUpdateHandler.cs`,
   `Capture/MessageCache.cs` (`capture_outbox`), `Capture/PayloadBuilder.cs`,
   `Capture/CaptureScopeEvaluator.cs`, `Capture/CaptureHandlerRegistration.cs`,
   `Preflight/CapturePreflight.cs`; tests `tests/CustomSync.Tests/Capture*.cs`.

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service or `--login`, no
  `dotnet run`. `dotnet build` / `dotnet test` only. No test may need
  `libtdjson`, PostgreSQL or network.
- Never log names, usernames, status values or peer ids of watched
  users (this is surveillance data about third parties). Counts and
  update types are fine.
- **K1** — capture tunables come from `IConfiguration`.
- **K4** — replaying the same TDLib update must not create a second row.
- **K6** — TDD: each test first, seen failing. **K7** — one commit,
  imperative subject, WHY in the body, **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`.
- Do not change the backend projects (`Api`, `Services`, `Data`, `Core`).
- Do not invent protocol: no new `setting` keys, no new payload fields.

## 🔴 1. Byte-for-byte parity with tdesktop's activity records

Everything in the step-0 table is copied, not redesigned: field names,
value strings for every status kind, the payload shape
`{account_id, peer_id, field, old_value, has_old_value, new_value}`
(plus anything else tdesktop actually writes), `occurred_at`, and the
noise filter. Where TDLib and MTProto expose the same fact differently
(TDLib `userStatusOnline.expires`, `userStatusOffline.was_online`,
`userStatusRecently`, `usernames.active_usernames`, …), map TDLib to the
value tdesktop would have written for the same moment, and put the
mapping in a pure, separately tested class.

Only fields that TDLib delivers in `updateUser` / `updateUserStatus` are
in scope. If tdesktop tracks something that needs an extra request
(e.g. bio via full info), list it in the report as not covered — do not
add requests.

`peer_id` is the watched user's tdesktop peer id (`TdIdMapper` form).
Payloads use the existing `capture_outbox` table and `PayloadBuilder`
escaping rules (strings never `null`, UTF-8 unescaped, no hand-rolled
JSON that skips escaping).

## 🔴 2. Scope — its own chain, NOT the message scope

tdesktop keeps activity lists completely separate from WL/BL
(`custom_settings.h`: "BUTUNLAY MUSTAQIL"). Do not reuse
`ICaptureScope` decisions for activity. Mirror `ShouldTrackActivity`:

```
Exclude → false ; Include → true ; trackAllContacts && isContact → true ; else false
```

(Confirm the exact chain from the code; report any difference.)

Layers, same idea as Task 5:

```
1. server Exclude (Capture:Activity:Exclude)             → false
2. server Include (Capture:Activity:Include)             → true
3. synced activity settings snapshot, if present         → the chain above
4. otherwise Capture:Activity:TrackAllContacts && isContact (default false)
```

- **The protocol defines no `setting` keys for activity lists yet**
  (spec §3.2.1 covers only message scope). Build the seam exactly like
  Task 5 (`ISynced…Source` returning `null`, registered through the
  production registration, read on every decision), and nothing more.
- Preflight validates `Capture:Activity:*` the way Task 5 validates
  `Capture:Scope:*`: only canonical positive tdesktop peer ids
  (`ScopeConfigReader.IsCanonicalPeerId`), no overlap between Include and
  Exclude, `TrackAllContacts` must parse as a bool if present.
- `isContact` comes from TDLib's `user.is_contact`, kept current from
  `updateUser`.
- Default config → no activity is recorded (fail-closed).

## 🔴 3. `msg_id` for activity — the protocol contradicts itself

Spec §3.1 says the activity `msg_id` is `SHA256(field)[0:8]` as int64;
the §3.2 table and every `activity` case in `test-vectors.json` use `0`.
Do what **tdesktop's code** does (it is the implementation that is
already producing records), store that value in `capture_outbox.msg_id`,
and quote the exact line in the report. If tdesktop uses the
discriminator, reuse the existing `CustomSync.Core` helper (do not
reimplement it) and add a test that pins the value for one field name.

This matters: with `0`, two different fields changing in the same second
share one `record_id` and one of them is lost.

## 🔴 4. Durable state — a restart must neither lose nor invent events

- **Last known value per (user, field) is persisted** in the same SQLite
  file as the cache (new table). Otherwise every restart sees every user
  "for the first time" — TDLib sends thousands of `updateUser` at
  startup — and either floods the outbox or loses `old_value`.
- **The noise-filter buffer is durable too.** If tdesktop holds a change
  for N minutes before writing it, a crash inside that window must not
  lose it: persist pending changes (or prove by test that no observed
  transition is lost across a restart).
- Reading last-known, updating it and inserting the outbox row happen in
  **one transaction** (same rule as deletions in Task 4a).
- Time comes only from `TimeProvider`; tests use a fake clock for the
  buffer window.
- Replaying the same `updateUserStatus` / `updateUser` creates nothing
  new (K4).

## 🔴 5. Handler integration and wiring

- Handle `updateUserStatus` and `updateUser` in `CaptureUpdateHandler`,
  in order, on the same single consumer as messages. Add them to the
  types buffered before `account_id` is known.
- Registered through the production path (`AddCaptureHandlers` or a
  method it calls). At least one test builds the provider with the real
  registration methods and an in-memory `IConfiguration`, resolves only
  `ITdClient` (like `Worker`), drives `FakeTdTransport`, and asserts an
  outbox row.
- Removing the activity subscription/branch must fail a test.

## WHAT NOT TO ADD

No `setting` parsing or pull (Task 6), no `account_hash`/`peer_hash`
computation (Task 6 applies §0.13: activity uses an empty
`account_hash` and the account-less `peer_hash`), no retention job for
activity rows (Task 9), no extra TDLib requests (`getUserFullInfo` etc.),
no `setOption online` (Task 7), no changes to message capture.

## TESTS

1. Mapper: every TDLib status kind → the exact tdesktop string; names
   and username (including multiple/none active usernames, non-ASCII).
2. First observation behaves as tdesktop (event or no event, as found).
3. A real change produces one row with exact payload keys, types and
   `has_old_value`, `old_value`, `new_value`, `occurred_at`, `msg_id`.
4. Noise filter: changes inside the window are suppressed/merged exactly
   as tdesktop does; a change after the window is written (fake clock).
5. Restart: new handler + same DB file → no duplicate event, `old_value`
   preserved; a change buffered before the "crash" is not lost.
6. Replay of the same update → no new row.
7. Two different fields changing in the same second → both rows exist
   (or, if tdesktop uses `msg_id=0`, a test that documents the collision
   and the report says so).
8. Scope: Exclude beats Include; Include beats not-a-contact;
   `TrackAllContacts` only for contacts; default config records nothing;
   server layer beats a synced snapshot; activity lists do not affect
   message scope and vice versa.
9. Preflight: malformed / negative / overlapping `Capture:Activity:*`
   entries and a non-bool `TrackAllContacts` are errors.
10. Production wiring through the real registration (section 5).
11. Updates before `account_id` is known are not lost.
12. Logs captured during all of the above contain no names, usernames,
    status values or watched peer ids.

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; `dotnet test` all green (249 + new), full
   suite **three times** — report all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, then
   **rebuild with `--no-incremental`** before the next one:
   a) encode one status kind differently from tdesktop;
   b) keep last-known values only in memory;
   c) write the outbox row outside the last-known transaction;
   d) drop the noise filter;
   e) use `ICaptureScope` (message scope) for activity;
   f) ignore `is_contact` for `TrackAllContacts`;
   g) accept a negative id in `Capture:Activity:Exclude`;
   h) remove the activity branch from the handler;
   i) take the time from `DateTime.UtcNow` instead of `TimeProvider`;
   j) log the new status value.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–5 implemented; tests 1–12 pass; all 10 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 Task 4b row; deviations with reasons; header
  test count updated; the step-0 parity table (short form).

## FINAL REPORT (eight short points)

1. Commit hash and files changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–j).
4. The step-0 parity table (fields, encodings, occurred_at, filter,
   first observation, scope chain) with tdesktop file:line for each row.
5. Activity `msg_id`: what tdesktop uses, with file:line, and what that
   means for the §3.1 vs §3.2/test-vectors contradiction.
6. Fields tdesktop tracks that this task could not cover, and why.
7. Anything done differently from this prompt, and why.
8. Anything in the plan or spec you think is wrong or unsafe for Tasks
   4c, 6–10.

## OUT OF SCOPE

Plan 05 Task 4c (edit_date), 6–10, pull/decryption, `setting` key
design, hashing, activity retention, obtaining `libtdjson`, the owner's
login, deployment.
