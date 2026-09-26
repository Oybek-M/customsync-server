Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 3 — the local message cache

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
  `<tdesktop>\docs\superpowers\plans\2026-07-29-multi-device-sync-05-capture-service.md`
  §Task 3. The requirements below override the plan where they differ, and
  each difference says why.
- Starting state: `dotnet build` 0 warnings, `dotnet test` 190/190.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md` §2, §3 ("must not break"), §4 (test rules).
2. Spec (READ-ONLY):
   `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`
   — §0.6 (new kinds; message ids may be **negative**) and the payload
   table where **`edited` = `{old_text, new_text, is_out}`**. That payload
   is why §2 below exists.
3. Plan 05 §Task 4 (the update handlers that will consume this cache) — so
   the API you write actually fits its caller. Do NOT implement Task 4.
4. Existing capture code: `src/CustomSync.Capture/` (`Tdlib/`,
   `Preflight/CapturePreflight.cs`, `Worker.cs`) and the capture tests in
   `tests/CustomSync.Tests/Capture*.cs` — match their style and wiring.

## NON-NEGOTIABLE RULES

- 🔴 **Do NOT run the service and do NOT start any server.** No
  `dotnet run`, no `--login`, no TDLib. `dotnet build` / `dotnet test`
  only. Nothing you write may require `libtdjson`.
- 🔴 **Message text is private data at rest.** It must never be logged, and
  never appear in an exception message or in a report you write.
- **K6** — TDD: each test written first and seen failing.
- **K7** — one commit, imperative subject, WHY in the body, **no
  `Co-Authored-By` trailer**.
- Paths and tunables come from `IConfiguration` (this is a separate process
  and does NOT read the server's `server_settings` table).
- Do not write to `<tdesktop>` or `docs/sync-protocol/`.
- Do not change the backend (`CustomSync.Api/Services/Data`) or any
  existing test. The 190 tests must keep passing untouched.

## 🔴 1. Why this class exists (do not lose sight of it)

`updateDeleteMessages` carries only ids. A message whose text was not
already stored is gone permanently. So this cache is the difference
between the service working and the service being pointless. Two
consequences the plan's sketch ignores:

- **Every `Put` must be committed on its own.** No long-lived
  transactions, no batching that holds rows in memory — a crash between
  "message arrived" and "commit" loses exactly the data this service
  exists to keep.
- **A miss is normal, not an error.** Messages older than the cache
  window will not be there; the caller decides what to do (Task 4 skips
  them). `Get` returns null, it does not throw.

## 🔴 2. `Put` MUST return the previous row

The plan's `Put` is `INSERT OR REPLACE` and returns nothing. But an edit
arrives as `updateMessageContent`, and the `edited` record requires
`old_text` **and** `new_text`. With a blind overwrite the old text is
destroyed the instant the new one is written, and the resulting record
would carry `old_text == new_text` — a silent, permanent data loss that
looks like success.

Required API shape (names may differ, behaviour may not):

```
CachedMessage? Put(CachedMessage message)   // returns the row it replaced, or null
CachedMessage? Get(long chatId, long messageId)
int Prune(int olderThanDays)                // returns rows deleted
CacheStats Stats()                          // row count + database file size in bytes
void Initialize()                           // idempotent
```

`Put` must read-and-replace **atomically** (one transaction, or
`RETURNING`), so two updates for the same message cannot interleave and
both report "no previous row".

## 🔴 3. SQLite settings that matter for a 24/7 single process

- Enable **WAL** and a **busy timeout**; without them a `Prune` running
  while updates arrive throws "database is locked" and the service starts
  dropping messages.
- `Put`, `Get` and `Prune` must be safe to call from different threads at
  the same time (the TDLib receive thread writes; the prune timer deletes).
- Parameterised SQL only — never string interpolation into SQL.
- Record a schema version (`PRAGMA user_version`) so a later task can
  migrate the table instead of guessing its shape.
- Wrap SQLite failures so the exception that escapes does **not** contain
  the parameter values (message text).

## 🔴 4. Schema

Start from the plan's table (`chat_id`, `message_id`, `text`, `sender_id`,
`is_out`, `is_media`, `media_id`, `date`, `cached_at`, PK
`(chat_id, message_id)`, index on `cached_at`). Additional requirements:

- **`message_id` may be negative** (spec §0.6 — avatar and story markers).
  Nothing may filter, clamp or `abs()` it.
- `date` and `cached_at` are Unix **seconds**, UTC. `cached_at` comes from
  an injectable clock so `Prune` is testable without waiting 30 days.
- `media_id` holds TDLib's identifier only. **No local filesystem paths in
  this table** (spec §0.8 keeps paths out of synced records; keeping them
  out here too means a later task cannot leak one by accident).
- `text` is nullable: a media-only message has no text, and that is not the
  same thing as an empty edit.

## 🔴 5. Wire it up — no dead code

`Prune` with no caller is dead code. (This already happened once in this
project: `TdRedactor` shipped with tests but no production caller, so the
protection existed only on paper.) Therefore:

- `Capture:CacheDatabasePath` (default under
  `/var/lib/customsync-capture/`), `Capture:CacheRetentionDays` (default
  30), `Capture:CachePruneIntervalHours` (default 6) — all from
  `IConfiguration`.
- `CapturePreflight` also checks the cache path's directory is writable,
  reported like its existing checks.
- `Worker` initializes the cache after preflight and prunes on the
  configured interval. Extract the periodic part so it is testable without
  the host (a small class with an injectable clock/delay, like
  `AuthorizationGate` was extracted).
- The prune loop must not kill the service: a failure is logged (no text)
  and the next cycle still runs.

## WHAT NOT TO ADD

No update handlers, no `ScopeEvaluator`, no sync client, no media
downloads, no TDLib storage maintenance (`optimizeStorage`), no records
pushed anywhere — those are plan 05 Tasks 4–9. No new backend endpoints.
No quota enforcement (Task 9). Do not touch `Tdlib/` except where §5
requires wiring.

## TESTS — `tests/CustomSync.Tests/CaptureCacheTests.cs`

Temp database file per test, deleted afterwards. No PostgreSQL, no TDLib,
no network.

1. `Initialize` twice on the same file is a no-op the second time; the
   schema version is set.
2. Round trip: `Put` then `Get` returns every field unchanged, including
   `text = null` for a media-only row.
3. **`Put` returns the replaced row** with the OLD text, and `Get`
   afterwards returns the NEW one. This is the `edited` payload's
   `old_text`; assert both halves.
4. First `Put` for a message returns null (nothing replaced).
5. A **negative** `message_id` round-trips through `Put`/`Get`/`Prune`.
6. `Get` for an unknown `(chat_id, message_id)` returns null and does not
   throw.
7. `Prune(30)` deletes rows older than the window, keeps rows inside it
   (use the injectable clock, and put one row exactly on the boundary),
   and returns the number deleted.
8. `Stats` reports the row count and a non-zero database size.
9. Concurrency: many parallel `Put`/`Get` calls plus a `Prune` in the
   middle all succeed — no "database is locked", no lost row. Assert the
   final row count exactly.
10. Two `Put` calls for the same message from parallel threads: exactly
    one of them reports the pre-existing row (not both, not neither).
11. A SQLite failure (e.g. point the path at a directory) surfaces a clear
    exception whose message does NOT contain the message text.
12. The periodic prune component calls `Prune` on its interval, and a
    throwing `Prune` does not stop the next cycle.
13. `CapturePreflight` reports an unwritable cache path, and the message
    does not contain any secret.

## HOW TO VERIFY

1. Every test above written first and seen failing.
2. `dotnet build` 0 warnings; `dotnet test` all green (190 + new).
3. `grep` the repo to confirm no message text is logged: every
   `Log*` call near the cache passes ids/counts only. State in the report
   how you checked.
4. Deliberate breaks — do each, confirm at least one test fails, revert:
   a) make `Put` discard the replaced row and return null always;
   b) read the old row in one transaction and write in another;
   c) drop WAL / busy timeout;
   d) make `Prune` use `<=` where the boundary row must survive;
   e) `abs()` the `message_id` on write;
   f) log the cached text at information level;
   g) remove the prune scheduling so `Prune` has no caller.
   If a break does NOT fail a test, add or fix the test.

## DEFINITION OF DONE

- Sections 1–5 implemented; tests 1–13 pass; all 7 breaks caught.
- One commit, pushed to `origin Oybek`, working tree clean.
- `PROGRESS.md`: plan 05 row updated to 3/10 with a Task 3 entry, and the
  deviations from the plan listed with reasons (`Put` returning the
  previous row, WAL/busy timeout, schema version, injectable clock, prune
  wiring, `Stats`).

## FINAL REPORT (seven short points)

1. Commit hash and files changed.
2. Test count before/after; exact `dotnet test` summary line.
3. Which deliberate break failed which test (a–g).
4. Anything from this prompt you did differently, and why.
5. How you verified no message text reaches the logs.
6. What Task 4 will need from this cache that it does not yet have.
7. Anything in the plan text you think is wrong or unsafe for later tasks.

## OUT OF SCOPE

Plan 05 Tasks 4–10, obtaining or building `libtdjson`, the owner's
Telegram login, the systemd unit and session hardening, plan 03, and the
`read_at` protocol gap.
