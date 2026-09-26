Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 4a — deleted and edited messages into a durable outbox

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
- Plan text (READ-ONLY, another repo):
  `<tdesktop>\docs\superpowers\plans\2026-07-29-multi-device-sync-05-capture-service.md`
  §Task 4. The requirements below override the plan where they differ, and
  each difference says why.
- Starting state: `dotnet build` 0 warnings, `dotnet test` 212/212.

## WHY THIS TASK MATTERS

`docs/plan05-real-world-evidence.md`: six deleted messages were lost on
the desktop client because it was offline when they were deleted —
Telegram does **not** resend `updateDeleteMessages`. This service exists
to catch exactly that event. **A deletion event cannot be re-requested.
Losing one is permanent data loss.** Every requirement below follows from
that.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md` §2 "Task 4 promptiga majburiy kiritiladigan shartlar"
   (five conditions — all apply), §3, §4, and the three verification
   write-ups for plan 05 Tasks 1–3.
2. `docs/plan05-real-world-evidence.md`.
3. Spec (READ-ONLY): `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`
   §0.12, §0.13, §0.14, §3.1, §3.2, §3.4, §4.3.
4. **The other implementation of this contract — READ-ONLY, do not edit:**
   `<tdesktop>\Telegram\SourceFiles\custom_sync_payload.cpp`
   (`BuildDeleted`, `BuildEdited`), `custom_sync_outbox.cpp` (`Enqueue`),
   `custom_db.cpp` (search `Outbox::Enqueue`), `data\data_peer_id.h`
   (`ChatIdType`, `kChatTypeMask`). Your output must be byte-for-byte
   compatible with what tdesktop produces for the same event.
5. Existing capture code: `src/CustomSync.Capture/Capture/*`,
   `Tdlib/TdClient.cs`, `Tdlib/AuthorizationGate.cs`, `Worker.cs`,
   `Program.cs`; tests `tests/CustomSync.Tests/Capture*.cs`.

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service or `--login`, no
  `dotnet run`. `dotnet build` / `dotnet test` only. No test may need
  `libtdjson`, PostgreSQL or network.
- Never log or put into an exception message any message text, caption,
  or name — at any log level. IDs are fine.
- **K1** — tunables come from `IConfiguration` (separate process; no
  `server_settings` keys).
- **K4** — replaying the same TDLib update must not duplicate anything.
- **K6** — TDD: each test first, seen failing.
- **K7** — one commit, imperative subject, WHY in the body,
  **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`.
- Do not change the backend projects (`Api`, `Services`, `Data`, `Core`).

## 🔴 1. Output is a durable local outbox — NOT a `SyncRecord`

The plan's sample calls `_sync.EnqueueDeletedAsync(...)`. There is no sync
client yet (Task 6), and a real `SyncRecord` needs `peer_hash`,
`account_hash` and an encrypted payload — i.e. keys this service does not
have yet (key sharing comes later).

tdesktop solves "no keys" with `if (!KeysAvailable()) return;` in
`Outbox::Enqueue` — **it silently drops the event.** Do NOT copy that. Here
it would drop every deletion.

Required instead:
- A `capture_outbox` table **in the same SQLite file as the message
  cache**, holding the plaintext logical event: `kind`, `account_id`,
  `peer_id` (decimal string), `msg_id`, `occurred_at`, `observed_at`,
  `payload_json`, plus bookkeeping you need. Task 6 will turn rows into
  `SyncRecord`s (hash + encrypt + push) and remove them after a
  successful push. You do not build that.
- Unique on `(kind, account_id, peer_id, msg_id, occurred_at)` — the exact
  pre-image of `record_id` (spec §0.12), so a replayed update cannot
  create a second row.
- Same file as the cache so the cache read and the outbox write are **one
  transaction** (section 4).

## 🔴 2. IDs must match tdesktop exactly — otherwise dedup and peers break

The same deletion seen by tdesktop and by this service must produce the
same `record_id`, and the same chat must produce the same `peer_hash`.
TDLib's identifiers are NOT Telegram's:

**Peer.** tdesktop uses `QString::number(peer->id.value)` where the type
lives in the high bits (`data_peer_id.h`: `UserId = ChatIdType<0>`,
`ChatId = <1>`, `ChannelId = <2>`, shifted `<< 48`). TDLib uses
`chat_id`: user `= user_id`, basic group `= -group_id`,
supergroup/channel `= -(1000000000000 + channel_id)`. Required: a pure,
static `TdIdMapper` converting TDLib → tdesktop form. Secret chats → no
mapping (they are not captured). Check against the test vector:
TDLib `-1002827825432` → `"562952781246744"` (= `2827825432 + 2^49`), the
`peer_id` in `test-vectors.json` §peer_hash.

**Message.** TDLib's `message_id` for a server message is the server id
shifted left by 20 (TDLib `MessageId::SERVER_ID_SHIFT`). tdesktop uses the
server id. Required: `msg_id = tdlib_id >> 20`, and only when the low 20
bits are zero. Anything else (local, pending, scheduled, failed-to-send) is
not a server message: do not cache it, do not emit anything.

**Sender.** `sender_id` = the sender's peer id in the same tdesktop form
(`messageSenderUser` → user id; `messageSenderChat` → mapped chat).

**Account.** `account_id` = the logged-in user id (decimal). Obtain it
before any event is written (`updateOption` `my_id`, or `getMe`) — say
which in the report. **Updates that arrive before it is known must not be
dropped**; queue them or otherwise prove none is lost.

State in the report if any of these facts turned out different from what
is written here. They will be checked end-to-end in Task 10.

## 🔴 3. `occurred_at` and payloads — copy tdesktop, not the plan

From `custom_db.cpp`:
- `deleted`: `occurred_at = message date > 0 ? message date : now`.
  The **original send date**, not the deletion time.
- `edited`: same — the original send date.

Payloads (plaintext JSON, spec §3.2 + §0.14), exactly these keys and types
as `BuildDeleted` / `BuildEdited` emit them:
- `deleted`: `account_id` (string), `peer_id` (string), `text` (string),
  `sender_id` (string), `is_out` (bool), `is_media` (bool).
- `edited`: `account_id`, `peer_id`, `old_text`, `new_text` (strings),
  `is_out` (bool).
- String fields are never JSON `null`: absent text is `""` (Qt serialises
  an empty `QString` as `""`). Text is the message text or the media
  caption.

"Now" and `observed_at` come only from `TimeProvider`. Never put a
TDLib-supplied date into `CachedMessage.CachedAt` (PROGRESS §2 condition
1): that field drives cache retention, and a message sent a year ago would
be pruned the moment it is cached.

## 🔴 4. Handlers — atomic, ordered, scoped

**`updateNewMessage`** — map ids; skip non-server messages; consult scope
(section 5) **before** caching; cache with `CachedAt` left to the clock.

**`updateDeleteMessages`** — ignore unless `is_permanent == true` and
`from_cache == false` (the plan explains why). For the listed ids, in
**one SQLite transaction**: read the cached rows, insert one outbox event
per cached message, remove those cache rows. Add `GetMany`/`Delete` (or an
equivalent single-transaction operation) to the cache for this — per-id
`Get` + separate writes is not atomic (PROGRESS §2 condition 2). A crash
may lose nothing: either the outbox rows and the cache removal both
commit, or neither does. Ids not in the cache (sent before capture began)
emit nothing and are counted.

**`updateMessageContent`** — new text/caption from the update, old text
from the cache, in one transaction:
- old text present and different → insert `edited` event, update the cache
  to the new text;
- text unchanged (reactions, media refresh…) → nothing;
- **not in the cache** → no event (a record with unknown "before" is
  useless), **but** store the new text so the next edit has a baseline.
  Count it.
- A second edit of the same message before Task 6 has sent the first one
  hits the same unique key (same `occurred_at`). Do what tdesktop does
  (`INSERT OR REPLACE` in `Enqueue`): the pending row takes the latest
  payload. Say in the report that both implementations lose the
  intermediate version this way — that is a protocol question for the
  owner, not something to fix here.

**Ordering.** `TdClient` raises `UpdateReceived` on its receive thread in
TDLib order. A new message followed immediately by its deletion must see
the cached text. Process updates **in order, one at a time** — no
`async void` fan-out, no `Task.Run` per update, no parallel consumers. If
you move work off the receive thread, use a single-reader queue.

**Failures.** A handler exception must not stop later updates, and must
not vanish: log at Error with update type and ids (never text) and
increment a counter.

## 🔴 5. Scope is not built yet — fail closed

Scope evaluation is Task 5. Define `ICaptureScope` (`ShouldCapture(peer)`)
and register a placeholder that captures **nothing**, clearly named and
commented as such. Capturing everything by default would write every
private chat to the VPS disk before anyone decided what should be kept.
Tests inject an allow-all fake. The handler consults scope for new
messages AND at emit time for deletions and edits.

## 🔴 6. Wired in production, tested through production wiring

Three tasks in a row arrived with a safeguard that existed but was not
connected (`TdRedactor`, authorization, cache registration — PROGRESS §2
condition 5). Here:
- One registration method (e.g. `AddCaptureHandlers`) called by
  `Program.cs`; the handler subscribes to the real `ITdClient`.
- At least one test builds the service provider **with that same
  registration method**, drives a `TdClient` over the existing
  `FakeTdTransport` with raw TDLib JSON, and asserts outbox rows. Removing
  the subscription line must make it fail. A hand-assembled
  `ServiceCollection` does not satisfy this.

## WHAT NOT TO ADD

No `SyncRecord` building, no hashing, no encryption, no HTTP, no outbox
draining (Task 6). No `activity` (`updateUserStatus`, `updateUser`) —
tdesktop encodes status values and filters last-seen noise
(`custom_activity_history.cpp`), and porting that exactly is its own task
(4b). No media download or `media_index` (Task 8). No real scope rules
(Task 5). No changes to cache pruning.

## TESTS — `tests/CustomSync.Tests/CaptureUpdateHandlerTests.cs` (+ mapper tests)

1. Mapper: user `7053823996` → `"7053823996"`; basic group; supergroup
   `-1002827825432` → `"562952781246744"`; secret chat → none;
   `395278 << 20` → `395278`; `(5 << 20) + 1` → none.
2. New in-scope message is cached; `CachedAt` comes from the fake clock,
   not the message date. Out-of-scope → not cached.
3. Permanent delete of a cached message → exactly one outbox row with the
   exact payload keys/types of section 3, `occurred_at` = original date;
   cache row gone.
4. `is_permanent: false` → nothing. `from_cache: true` → nothing.
5. Delete of an uncached id → nothing written, counter incremented.
6. Multi-id delete is atomic: force a failure after the first insert →
   no outbox rows and no cache rows removed.
7. The same delete update delivered twice → one outbox row.
8. Edit with cached text → `edited` row, cache now holds new text,
   `occurred_at` = original date.
9. Edit with unchanged text → nothing.
10. Edit with cache miss → no row, new text cached; a following edit then
    produces a row whose `old_text` is that text.
11. Two edits before sending → one pending row carrying the latest payload.
12. Ordering: new message and its deletion enqueued back-to-back through a
    real `TdClient` → the deletion row carries the text.
13. Production wiring (section 6).
14. Logs captured during all of the above contain no message text.
15. A handler failure on one update is logged at Error and the next update
    is still processed.
16. Non-ASCII text (Uzbek, Russian, emoji) arrives unchanged in
    `payload_json`.
17. Updates received before `account_id` is known are not lost.

## HOW TO VERIFY

1. Every test above written first and seen failing.
2. `dotnet build` 0 warnings; `dotnet test` all green (212 + new).
3. Deliberate breaks — do each, confirm a test fails, revert:
   a) use the TDLib `chat_id` directly as `peer_id`;
   b) drop the `>> 20`;
   c) set `occurred_at` to now instead of the message date;
   d) remove the `is_permanent` check;
   e) insert delete events one transaction per id;
   f) process each update with `Task.Run`;
   g) cache new messages before consulting scope;
   h) pass the TDLib date as `CachedAt`;
   i) remove the handler subscription from the registration method.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–6 implemented; tests 1–17 pass; all 9 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 Task 4a row, deviations from the plan with
  reasons (outbox instead of `_sync`, id mapping, fail-closed scope,
  activity split into 4b), and the header test count updated.

## FINAL REPORT (seven short points)

1. Commit hash and files changed.
2. Test count before/after; exact `dotnet test` summary line.
3. Which deliberate break failed which test (a–i).
4. Anything done differently from this prompt, and why.
5. How `account_id` is obtained and how early updates are kept.
6. Any fact in section 2 or 3 that turned out different from what is
   written here.
7. Anything in the plan text you think is wrong or unsafe for Tasks 4b–10.

## OUT OF SCOPE

Plan 05 Task 4b (activity), 5–10, `SyncRecord`/encryption/push, key
sharing, obtaining `libtdjson`, the owner's login, deployment.
