Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 5 — capture scope: which chats are cached and captured

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
  §Task 5. The requirements below override the plan where they differ,
  and each difference says why.
- Starting state: HEAD `7f448ba` or later, `dotnet build` 0 warnings,
  `dotnet test` 236/236.

## WHY THIS TASK MATTERS

Today the service captures **nothing**: `NoneCaptureScope` refuses every
chat (fail-closed placeholder from Task 4a). This task decides which chats
are cached and which deletions/edits become records. Two ways to get it
wrong, both bad:
- too wide → every private chat is written to the VPS disk against the
  owner's settings;
- too narrow or different from tdesktop → the owner turns AntiDelete on
  for a chat in tdesktop and the 24/7 service silently ignores it — the
  exact loss this service exists to prevent.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md` §2, §3, §4 and the "Plan 05 Task 4a tekshiruvi" section
   (what was wrong last time — the same classes of defect will be looked
   for again).
2. **The reference implementation — READ-ONLY, do not edit:**
   `<tdesktop>\Telegram\SourceFiles\custom_settings.h` lines ~206–290 and
   `custom_settings.cpp`: `IsInWhitelist`, `IsInBlocklist`,
   `GetPeerType`, `ShouldAntiDelete`, `ShouldAntiEdit`,
   `ShouldBackgroundCache`, `AntiDeleteForPeer`, `AntiEditForPeer`.
   Also `data\data_session.cpp` `Session::updateEditedMessage` and
   `history\history_item.cpp` (search `ShouldAntiEdit`).
3. Existing capture code: `Capture/ICaptureScope.cs`,
   `Capture/CaptureUpdateHandler.cs`, `Capture/CaptureHandlerRegistration.cs`,
   `Preflight/CapturePreflight.cs`; tests `tests/CustomSync.Tests/Capture*.cs`.

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service or `--login`, no
  `dotnet run`. `dotnet build` / `dotnet test` only. No test may need
  `libtdjson`, PostgreSQL or network.
- Never log message text, and never log peer ids of chats in the lists
  (they reveal who the owner talks to). Counts are fine.
- **K1** — capture tunables come from `IConfiguration`.
- **K6** — TDD: each test first, seen failing. **K7** — one commit,
  imperative subject, WHY in the body, **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`.
- Do not change the backend projects (`Api`, `Services`, `Data`, `Core`).
- Do not invent `setting` record keys or payload formats (section 3).

## 🔴 1. Three decisions, not one — copy tdesktop's chain exactly

tdesktop does not have a single "capture this chat" switch. It has three
decisions with one shared chain (`custom_settings.cpp`):

| Decision | tdesktop function | Used by the handler for |
|---|---|---|
| cache | `ShouldBackgroundCache` | storing `updateNewMessage`, fetching the `getMessage` baseline, updating cached text on edit |
| deleted | `ShouldAntiDelete` | emitting `deleted` rows |
| edited | `ShouldAntiEdit` | emitting `edited` rows |

The chain for a peer `p` (tdesktop peer id string, as produced by
`TdIdMapper`):

```
type(p)          = (p >> 48) & 0xFF : 0 user, 1 group, 2 channel, else unknown
InBlocklist(p)   = p ∈ BL  ||  (p ∉ WL && BLcat[type(p)])
InWhitelist(p)   = p ∈ WL  ||  (p ∉ BL && WLcat[type(p)])

ShouldAntiDelete(p) = InBlocklist → false ; InWhitelist → true ;
                      else AntiDeletePerPeer[p] ?? antiDelete (global)
ShouldAntiEdit(p)   = same, with AntiEditPerPeer / antiEdit
ShouldCache(p)      = InBlocklist → false ; InWhitelist → true ;
                      else ShouldAntiDelete-fallback || ShouldAntiEdit-fallback
```

Details that matter and were real bugs in tdesktop (read the comments
there): an **exact** entry beats a **category** in both directions;
unknown type has no category; a per-peer override beats the global flag
in both directions (`false` override disables even when the global flag
is on).

Replace `ICaptureScope.ShouldCapture` with three methods (names up to
you) and update the handler so each place asks the right question. The
edit path: cache updated when `ShouldCache`; `edited` row emitted only
when `ShouldAntiEdit`.

**Known tdesktop inconsistency — do not copy it, report it:** the
background edit path (`updateEditedMessage` → `RecordBackgroundEdit`)
gates only on `ShouldBackgroundCache`, so it records edits even when
AntiEdit is off for that chat; the in-memory path (`history_item.cpp`)
checks `ShouldAntiEdit`. Follow `ShouldAntiEdit`. Confirm or refute this
reading in the report.

## 🔴 2. Two layers — this machine's override, then the owner's settings

The plan's precedence is kept, with tdesktop's chain as the lower layer:

```
1. server Block  (exact peer id)          → false for all three decisions
2. server Allow  (exact peer id)          → true  for all three decisions
3. synced settings snapshot, if present   → the chain of section 1
4. otherwise server DefaultEnabled        → that value for all three
```

Server layer = `IConfiguration`, e.g. `Capture:Scope:Block`,
`Capture:Scope:Allow` (arrays of tdesktop peer id strings),
`Capture:Scope:DefaultEnabled` (bool, **default `false`**). Why the
server layer wins: it expresses a decision about this machine (disk,
legal, "never on a VPS") that the owner's desktop settings must not
override.

Validation — **fail loudly at startup** (extend `CapturePreflight`, exit
non-zero like the other preflight errors): an entry that is not a
decimal int64, or a peer present in both Block and Allow. Reason: a
mistyped Block entry silently ignored means a chat the owner explicitly
excluded is written to the VPS.

Config is read once at startup; a change needs a restart. Say so in a
comment and in `PROGRESS.md`.

## 🔴 3. Synced settings do not exist yet — build the seam, not the data

The plan says the synced White/Block lists arrive as `setting` records
through pull. **Checked: tdesktop does not emit any `setting` record
today**, and the protocol does not define the keys or the value format
for these lists. Pull and decryption are Task 6.

So:
- Define an immutable snapshot type holding exactly what section 1 needs:
  WL, BL, WL categories, BL categories, AntiDelete per peer, AntiEdit per
  peer, global antiDelete, global antiEdit.
- Define a source interface returning the current snapshot or `null`.
  Register an implementation that returns `null`, clearly commented:
  *filled by Task 6 once the protocol defines the setting keys*.
- With `null`, layer 3 is skipped and layer 4 decides — i.e. with default
  config the service still captures nothing (fail-closed preserved).
- The snapshot may be replaced at runtime later (Task 6) — the evaluator
  must read the current one on every decision, not copy it at startup,
  and must be safe to call from the receive thread while another thread
  swaps the snapshot.

Do not guess key names, do not parse anything, do not add pull code.

## 🔴 4. Wired in production, tested through production wiring

Same rule as Task 4a: the evaluator is registered by the production
registration path (`AddCaptureHandlers` or a method it calls) and
`NoneCaptureScope` is removed from production registration. Tests that
prove wiring build the provider with the real registration methods and
an in-memory `IConfiguration`, resolve only `ITdClient` (like `Worker`),
and drive `FakeTdTransport`. A hand-built evaluator does not prove
wiring.

## WHAT NOT TO ADD

No `activity` scope (`ShouldTrackActivity`: Include/Exclude lists,
contacts) — that is Task 4b. No pull, decryption, `setting` parsing
(Task 6). No config hot-reload. No UI/admin endpoint. No changes to
`occurred_at` of `edited` (a separate protocol proposal exists:
`docs/proposal-edited-edit-date.md` — do not implement it).

## TESTS

1. Chain truth table (pure evaluator, snapshot present): exact BL beats WL
   category; exact WL beats BL category; category applies by type for
   user/group/channel; type ≥ 3 gets no category; per-peer `true`
   overrides global `false` and per-peer `false` overrides global `true`
   — for delete and edit independently; cache = delete-fallback OR
   edit-fallback.
2. Use real peer ids: `"7053823996"` (user), a group (`id | 1<<48`),
   `"562952781246744"` (channel), and one with type byte 3.
3. Layers: server Block beats synced WL; server Allow beats synced BL;
   snapshot `null` → `DefaultEnabled` (false and true cases).
4. Snapshot swapped at runtime changes the next decision.
5. Preflight: malformed entry → preflight error; same peer in Block and
   Allow → preflight error; valid config → no error.
6. Handler: AntiDelete on + AntiEdit off for a chat → message cached,
   deletion emitted, edit NOT emitted, but the cache holds the new text
   (a later deletion carries the edited text).
7. Handler: AntiEdit on + AntiDelete off → edit emitted, deletion not
   emitted.
8. Handler: out-of-scope chat edit with cache miss → **no** `getMessage`
   request is sent (inspect `FakeTdTransport.OutgoingRequests`).
9. Production wiring, default config → a new message and its deletion
   write nothing.
10. Production wiring, `Capture:Scope:Allow` contains the chat → deletion
    row written.
11. Logs captured during tests 9–10 contain no peer id from the lists.

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; `dotnet test` all green (236 + new), run
   the full suite **three times** — report all three summary lines
   (earlier tasks had tests that failed only under full-suite load).
3. Deliberate breaks — do each, confirm a test fails, revert. **After
   reverting, rebuild with `--no-incremental`** (restoring a file can
   leave its old timestamp and a stale binary):
   a) check the category before the exact entry;
   b) drop per-peer overrides (global flag only);
   c) cache decision = delete-fallback only;
   d) check server Allow before server Block;
   e) consult the synced snapshot before the server layer;
   f) silently skip a malformed config entry;
   g) emit `edited` when only `ShouldCache` is true;
   h) send `getMessage` for an out-of-scope chat;
   i) keep `NoneCaptureScope` in production registration;
   j) compute the type without `& 0xFF`.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–4 implemented; tests 1–11 pass; all 10 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 Task 5 row; deviations from the plan with
  reasons (three decisions, tdesktop chain, synced snapshot is a seam
  only, preflight validation, restart on config change); header test
  count updated.

## FINAL REPORT (seven short points)

1. Commit hash and files changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–j).
4. Anything done differently from this prompt, and why.
5. Confirm or refute the tdesktop background-edit inconsistency
   (section 1), with file:line.
6. Anything in tdesktop's chain you found that this prompt describes
   differently.
7. Anything in the plan text you think is wrong or unsafe for Tasks
   4b, 6–10.

## OUT OF SCOPE

Plan 05 Task 4b (activity), 4c (edit_date), 6–10, pull/decryption,
`setting` key design, config hot-reload, obtaining `libtdjson`, the
owner's login, deployment.
