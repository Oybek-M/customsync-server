Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 4c — `edited` records use Telegram's `edit_date`

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
- **`<tdesktop>` is READ-ONLY.** No edits, commits, pushes or builds
  there — another session owns it.
- Starting state: HEAD `a764da4` or later, `dotnet build` 0 warnings,
  `dotnet test` 265/265.

## WHY THIS TASK MATTERS

The owner wants every intermediate version of an edited message kept.
Until now `edited.occurred_at` was the message's original send date, so
all edits of one message shared one `record_id`: the local outbox kept
only the last edit and the server only the first one it saw. The
protocol has changed (spec §3.1/§3.2, `CHANGELOG.md` 2026-09-27,
`test-vectors.json`): **`occurred_at` of an `edited` record is the
Telegram `edit_date` of that edit.** Two devices seeing the same edit
get the same `edit_date`, so dedup still works, and A→B, B→C become two
records. tdesktop already writes it this way; the capture service must
match or the same edit shows up twice.

Background: `docs/proposal-edited-edit-date.md`.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md` §2, §3, §4 and the verification sections for plan 05
   Tasks 4a, 5 and 4b (the defects found there will be looked for
   again: fail-open defaults, protections not wired, untested
   atomicity, stale incremental builds during break testing).
2. Spec (READ-ONLY) `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`
   §3.1, §3.2 (the `occurred_at` column), and
   `<tdesktop>\docs\sync-protocol\CHANGELOG.md` entry 2026-09-27;
   `test-vectors.json` — the two `edited` cases for `msg_id` 390234.
3. Existing code: `Capture/CaptureUpdateHandler.cs` (`HandleMessageContent`,
   the `getMessage` baseline fetch), `Capture/MessageCache.cs`
   (`UpdateMessageContent`, `capture_outbox`), `PeriodicCachePruner`,
   `CaptureCacheStartup`; tests `tests/CustomSync.Tests/Capture*.cs`.

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service or `--login`, no
  `dotnet run`. `dotnet build` / `dotnet test` only. No test may need
  `libtdjson`, PostgreSQL or network.
- Never log message text, at any level, in any exception message.
- **K1** — tunables come from `IConfiguration`. **K4** — replaying an
  update creates nothing new. **K6** — TDD: each test first, seen
  failing. **K7** — one commit, imperative subject, WHY in the body,
  **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. Do not change
  the backend projects (`Api`, `Services`, `Data`, `Core`). Do not add
  payload fields.

## 🔴 1. TDLib splits one edit into two updates — pair them durably

TDLib reports an edit as **two** updates for the same
`(chat_id, message_id)`:
- `updateMessageContent` — the new content (the text we need);
- `updateMessageEdited` — `edit_date` (and `reply_markup`).

The record needs data from both: `old_text`/`new_text`/`is_out` from the
first, `occurred_at = edit_date` from the second. Their order is not
something to rely on. Required:

- When `updateMessageContent` changes the cached text of an in-scope
  message (`ShouldCache`) and `ShouldAntiEdit` is true: in **one SQLite
  transaction** read the old text, update the cache to the new text, and
  store a **pending edit** row `(chat_id, message_id, old_text,
  new_text, is_out, msg_date, observed_at)`. Do not emit yet.
- When `updateMessageEdited` arrives: in one transaction take the
  pending edit for that `(chat_id, message_id)`, insert the `edited`
  outbox row with `occurred_at = edit_date`, delete the pending row.
- If `updateMessageEdited` arrives **first**, remember its `edit_date`
  (same pending table) and emit when the content arrives.
- The pairing key is `(chat_id, message_id)` — message ids are
  per-chat, the same id in another chat is a different message.
- Pending rows live in the **same SQLite file** and survive a restart:
  the cache already holds the new text, so an in-memory pending edit
  lost in a crash means the old text is gone for good.

## 🔴 2. Unpaired halves — never silently dropped, never kept forever

- A text change with no `updateMessageEdited` within
  `Capture:EditPairingTimeoutSeconds` (default 60): emit it with the
  protocol fallback `occurred_at = msg_date` (tdesktop:
  `editDate > 0 ? editDate : msgDate`), and count it. `edit_date = 0` in
  `updateMessageEdited` uses the same fallback.
- An `updateMessageEdited` whose content never changes the text
  (reply-markup-only edits, reactions): its pending `edit_date` is
  discarded after the same timeout, no record.
- The sweep runs at startup, on each handled update, and in the
  existing periodic maintenance loop; it uses `TimeProvider` only.
- A second content change for the same message while a pending edit
  exists: emit the first one (with its `edit_date` if known, otherwise
  the fallback) before storing the second — two edits must never merge
  into one.

## 🔴 3. Everything else stays as it is

- Scope gates exactly as after Task 5: cache/baseline by `ShouldCache`,
  emission by `ShouldAntiEdit`. With `ShouldAntiEdit` false the cache is
  still updated and no pending row is created.
- The `getMessage` baseline path is unchanged: an edit of an uncached
  message emits nothing; the fetched message becomes the baseline.
- Payload shape unchanged: `{account_id, peer_id, old_text, new_text,
  is_out}`. `msg_id` = server message id.
- Two edits with the same `edit_date` (same second) collide by protocol
  design: the unique key keeps one. Keep the existing `INSERT OR REPLACE`
  behaviour and document it in a test.
- Wired through the production registration; no new
  `GetRequiredService` line in `Worker` is allowed to be the only thing
  connecting it.

## TESTS

1. Content then edited → one row, `occurred_at` = `edit_date`, exact
   payload.
2. Edited then content → the same row.
3. A→B (`edit_date` 1787000010) then B→C (1787000020), `msg_id` 390234
   → two rows, `old_text`/`new_text` chained, occurred_at values as in
   `test-vectors.json`.
4. Restart between the two halves (new handler, same DB file) → the row
   is still emitted with the right old text.
5. Content with no edited: nothing before the timeout, one fallback row
   (`occurred_at` = msg date) after it (fake clock).
6. Edited with no text change: no row; its pending state is gone after
   the timeout.
7. `edit_date = 0` → fallback.
8. Replaying either update → no second row.
9. Same message id in two chats edited at once → not cross-paired.
10. A second content change before the first is paired → two rows.
11. `ShouldAntiEdit` false → cache updated, no pending row, no record.
12. Cache miss → `getMessage` baseline, no row; the next edit pairs and
    emits.
13. Same-second collision documented (one row, latest payload).
14. Production wiring: real registration, resolve only `ITdClient`, raw
    TDLib JSON through `FakeTdTransport` → row with `edit_date`.
15. Atomicity: an outbox insert forced to fail (e.g. a `RAISE(ABORT)`
    trigger) leaves the pending row in place.
16. Logs contain no message text.

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, then
   **rebuild with `--no-incremental`** before the next one:
   a) keep `occurred_at = msg_date` always;
   b) keep pending edits in memory;
   c) write the pending row outside the cache-update transaction;
   d) ignore the edited-before-content order;
   e) never run the sweep;
   f) take the sweep time from `DateTime.UtcNow`;
   g) pair on `message_id` only;
   h) overwrite a pending edit with a second content change;
   i) create the pending row when `ShouldAntiEdit` is false;
   j) delete the pending row outside the outbox-insert transaction.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–3 implemented; tests 1–16 pass; all 10 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 Task 4c row, pending-edit design and timeout
  in §8, header test count updated. Do not delete the open protocol
  questions under §2.

## FINAL REPORT (six short points)

1. Commit hash and files changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–j), by test name.
4. Anything done differently from this prompt, and why.
5. Anything about TDLib's update order you found (docs, tests) that
   changes the design.
6. Anything unsafe you noticed for Task 6 (outbox drain) in how
   `edited` rows are now produced.

## OUT OF SCOPE

Task 6–10, pull/decryption/hashing, the activity `long_ago` question,
activity `setting` keys, obtaining `libtdjson`, the owner's login,
deployment.
