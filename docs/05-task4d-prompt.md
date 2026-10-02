Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 4d — profile-photo and story activity signals (tdesktop parity)

**Give this prompt only after the previous task has been verified by the
TeamLead** (see the `PROGRESS.md` hand-off block). It extends Task 4b's
activity capture; it does not depend on Task 10a/10b code.

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
  builds there. Read its files with
  `git -C <tdesktop> show origin/Oybek:<path>`.
- Starting state: the TeamLead's latest verification commit (see the
  `PROGRESS.md` hand-off block), `dotnet build` 0 warnings, the full
  `dotnet test` green.
- The stale-test-vectors workaround (laptop), the PC NuGet workaround and
  the known intermittent test are exactly as described in
  `docs/05-task9a-prompt.md`, section REPOSITORY — apply them the same
  way.

## WHY THIS TASK MATTERS

Many contacts hide their last-seen time: their status is only
`recently`, which says nothing about *when* they were online. tdesktop
gets exact online moments from two facts Telegram still reveals:

- a **new profile photo** — it can only be set while online, and
  Telegram keeps its upload date;
- a **posted story** — every active story carries its posting date.

tdesktop writes each such moment as a `status` entry `online:<date>`
whose `observed_at` is **that date**, not the time it noticed it. Activity
records carry an empty account hash (spec §0.12) and their `record_id`
uses `observed_at` (§3.1), so the same moment written by tdesktop and by
the capture is **one** record — but only if both write the same field,
the same value and the same second.

The capture writes neither signal today. It runs 24/7 on the VPS; the
user's PC does not. Every photo change and story posted while the PC is
off is lost — exactly the gap the capture exists to close. tdesktop also
keeps two plain history fields, `photo` (which photo the contact has) and
`story` (when they last posted); the capture has neither.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block, §3, §4, §5, the plan 05 Task 4b
   section and its verification, the Task 7 section (invisibility,
   allow-list), §8 entries for Tasks 4b and 7, and §7 (repository
   facts).
2. Spec (READ-ONLY): §0.12, §0.14, §3.1, §3.2 (the `activity` row and
   `DiscriminatorFor`), §3.2.2 (its last bullet names these signals),
   §3.4. `test-vectors.json`: `discriminator`, `peer_hash`, `record_id`.
3. tdesktop (READ-ONLY) — **the canonical producer** (spec §3.2.2):
   - `Telegram/SourceFiles/custom_activity_history.cpp`: `RecordField`,
     `RecordPhotoOnlineMoment`, the `Flag::Photo` branch of `Init`, the
     `onStoriesChanged` lambda in `Init`, `RecordCurrentState`;
   - `Telegram/SourceFiles/custom_db.cpp`: `SaveActivityHistoryEntry`
     (the "A19" comment — which entries may replace the latest value)
     and `HasActivityEntryAt`;
   - `Telegram/SourceFiles/custom_sync_payload.cpp`: `BuildActivity`;
   - `Telegram/SourceFiles/data/data_types.h`: `PhotoId` is `uint64`.
4. Capture: `Capture/CaptureUpdateHandler.cs` (dispatch, `IsCapturedType`
   and the early queue used before `my_id` is known, `HandleUpdateUser`,
   `HandleUpdateUserStatus`, `_contactMap`), `Capture/MessageCache.cs`
   (`RecordActivity`, `activity_latest`, the `PRAGMA user_version`
   migrations v2–v5, the `capture_outbox` unique key and its
   `INSERT OR REPLACE`), `Capture/ActivityMapper.cs`,
   `Capture/PayloadBuilder.cs` (`BuildActivity`),
   `Capture/ActivityScopeEvaluator.cs`, `Capture/PeriodicCachePruner.cs`,
   `Tdlib/TdRequestPolicy.cs` and its tests, `Tdlib/TdClient.cs`
   (**`UpdateReceived` is raised on the receive thread**),
   `Media/MediaDownloader.cs` (its own loop) and `Worker.cs` (which
   starts that loop).
5. TDLib API (`td/generate/scheme/td_api.tl`, and the source files named
   in §3 below) from `github.com/tdlib/td` at a tagged release or a
   commit — **cite the URL and commit in the report**. No TDLib version
   is pinned yet (`PROGRESS.md`, the `libtdjson` decision); Task 10 pins
   it. Relevant: `user.profile_photo` (`profilePhoto`: `id`,
   `is_personal`), `getUserProfilePhotos` → `chatPhotos` /
   `chatPhoto.added_date`, `updateChatActiveStories` /
   `chatActiveStories` / `storyInfo.date`, `loadActiveStories`. TDLib's
   JSON interface writes `int64` values as JSON **strings**.

## NON-NEGOTIABLE RULES

- Do NOT run the API, the capture service, TDLib or any CLI mode, no
  `dotnet run`. `dotnet build` / `dotnet test` only. TDLib is faked at
  the existing transport seam (`ITdTransport`); time comes from
  `TimeProvider` (the tests' existing `TestTimeProvider` in
  `CaptureCacheTests.cs`). No `Thread.Sleep`.
- **tdesktop decides every value, second and skip rule** (§1). A
  difference you find is fixed in the capture, test first. If the spec
  and tdesktop's code disagree, stop and report.
- **Invisibility is not negotiable.** No request that can tell the
  contact anything: no story view, no read receipt, no online status, no
  "typing". Every request added to `TdRequestPolicy` needs the written
  argument of §3 and a strict shape check. Nothing else is added.
- No TDLib request is ever awaited on the update path (§2).
- Logs carry counts and error types only — never a peer id, photo id,
  story id, date value or name (same rule as the rest of the capture).
- **Every new part is wired into the production path** — the real
  registrations and `Worker`, with a test that fails when the wiring is
  removed. A hand-built `ServiceCollection` does not count.
- **K6** — TDD: each test first, seen failing. **K7** — one commit,
  imperative subject, WHY in the body, **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. No change to the
  sync protocol, record kinds, payload shape, push or pull.

## 🔴 1. What tdesktop writes — the contract

All entries below are `activity` records built by the existing path
(`MessageCache` → `capture_outbox`, `msg_id = DiscriminatorFor(field)`,
`occurred_at = observed_at`, payload from `PayloadBuilder.BuildActivity`,
account hash empty in the record). Only users are tracked, and only when
`ShouldTrackActivity(peer, isContact)` is true — the same gate as
`name` / `username` / `status`. A private chat's TDLib id is the user id,
which is also tdesktop's peer id (shift 0).

### 1.1 Field `photo` (an ordinary field, like `name`)

- `new_value`: the photo id as an **unsigned** 64-bit decimal string
  (tdesktop's `PhotoId` is `uint64`; reinterpret TDLib's `int64` as
  `ulong` — an id with the high bit set must not print with a minus
  sign), or `"empty"` when the user has no photo (`profile_photo` absent,
  `null`, or with id `0`).
- `observed_at` = now. Equal to the latest value → nothing. No 60-second
  rule (that is `status` only).
- The first observation is recorded with `has_old_value = false`,
  `"empty"` included (tdesktop's update path has no empty check).
- tdesktop never writes while the photo is *unknown*. TDLib sends the
  full `user` object in every `updateUser`, so an absent photo means "no
  photo". If you find a TDLib case where the photo is unknown, report it
  and skip that case.

### 1.2 Online moment from a new photo (field `status`)

- Only when 1.1 has just recorded a change to a **non-zero** photo id —
  the first observation included.
- `observed_at` = `occurred_at` = the photo's upload date (MTProto
  `photo.date`, TDLib `chatPhoto.added_date`); `new_value`
  `online:<date>`; `has_old_value = false`, `old_value` null.
- Skipped when, at the time the date is known (now = `TimeProvider`):
  the user's current photo is no longer that id; `date > now + 60`;
  `now − date > 2 592 000` (30 days — **exactly** 30 days is still
  written, as in tdesktop); an entry for (peer, `status`, date) already
  exists (§1.5).
- tdesktop asks the server once (`photos.getUserPhotos`) and gives up if
  the date is still unknown 15 s later. The capture sends **one**
  request per (peer, photo id) and never retries it (§2).
- A **personal** photo (`profilePhoto.is_personal`, set by the account
  owner for the contact) records the owner's action, not the contact's;
  TDLib does not return it from `getUserProfilePhotos`, so the id check
  skips it. tdesktop does not make this distinction — list it in the
  report as a known difference; do not change anything else for it.

### 1.3 Story signals (on a change of a user's active stories)

- For every active story with `date > 0`: `status` `online:<storyDate>`,
  `observed_at` = `occurred_at` = storyDate, `has_old_value = false` —
  unless an entry for (peer, `status`, storyDate) already exists. No age
  rule (tdesktop has none; active stories are at most 48 hours old).
- Then field `story`: `new_value` = the newest active story's date as a
  decimal string, `observed_at` = **now**, ordinary field rules (equal →
  nothing).
- An empty active list writes nothing — no "stories gone" entry.
- Only private chats with a user (a positive chat id); a group or
  channel's stories are ignored. `is_contact` comes from `_contactMap`,
  as in `HandleUpdateUserStatus`.
- `updateChatActiveStories` that arrives before `my_id` is known is
  buffered and processed afterwards like the other captured updates (the
  early queue and `IsCapturedType`).

### 1.4 Which entry becomes the latest value (tdesktop "A19")

An entry replaces `activity_latest` for (peer, field) only when its
`observed_at` is **≥** the stored one. A retroactive moment never
overwrites a newer current state. A moment newer than the stored status
**does** become the latest status, so the next real status change
carries it as `old_value` — exactly as tdesktop. Moments skip the
equality check and the 60-second rule (tdesktop writes them with
`SaveActivityHistoryEntry` directly, not through `RecordField`).

### 1.5 "An entry already exists at that second"

tdesktop's `HasActivityEntryAt` looks at its whole history. The capture
deletes outbox rows once they are sent, so it must keep its own durable
record of the (peer, field, `observed_at`) of at least every `status`
entry it has created, kept for at least 31 days, surviving restarts, and
consult it before writing a moment. Without it a restart re-sends old
moments, and `capture_outbox`'s `INSERT OR REPLACE` on (kind, account,
peer, `msg_id`, `occurred_at`) would silently replace an unsent status
row that happens to share the second. The schema change is migration
v6 in the existing style; a v5 database upgrades in place. Old rows are
purged by `PeriodicCachePruner`.

## 🔴 2. Threading

`TdClient.UpdateReceived` runs on the receive thread, and responses are
delivered by that same thread. An update handler that waits for a TDLib
response therefore deadlocks the whole capture. The update handler only
**queues** a photo-date lookup; a separate loop sends
`getUserProfilePhotos` and records the moment — the same pattern as
`MediaDownloader`, started from `Worker` next to it.

- The queue is bounded (choose and justify a size); on overflow the
  lookup is dropped and counted, never blocking the handler.
- One request per (peer, photo id), even if the same `updateUser`
  arrives again before the answer; an error or timeout ends that lookup.
- The loop survives any exception except cancellation and stops with the
  service.

## 🔴 3. TDLib requests and invisibility

`TdRequestPolicy`'s allow-list is the capture's invisibility guarantee.

- **`getUserProfilePhotos`** — allowed only as `{user_id: positive
  integer, offset: 0, limit: 1}` plus the transport's own keys, with the
  same strict key validation as `optimizeStorage`. Before adding it, find
  the MTProto method it maps to in TDLib's source and state in the report
  why the contact cannot see it.
- **`loadActiveStories`** — add it **only** if TDLib's source shows that
  `updateChatActiveStories` is not sent for a user's new story without
  it. Then: `story_list` exactly `storyListMain` (and `storyListArchive`
  only if you show it is needed), called when you show it must be (for
  example once after authorization, until TDLib answers 404), with the
  same written argument. If you cannot settle the question from TDLib's
  source, do **not** add it: implement the update handler, and put the
  open question in report point 6 so Task 10 measures it on a real
  session.
- Must stay rejected, each with a test: `openStory`, `closeStory`,
  `getStory`, `getChatActiveStories`, `getUserFullInfo`,
  `viewMessages`, `openChat`. The capture never downloads avatar or story
  files in this task (`downloadFile` stays message media only).

## TESTS

Minimum — through the real update handler, cache and outbox, with TDLib
faked at the transport and a fake clock:

1. `photo` values: a normal id; an id given as a negative `int64` string
   (unsigned output); a number instead of a string; `profile_photo`
   absent, `null`, id `0` → `"empty"`; the first observation `"empty"`
   is recorded; an equal value writes nothing; a change carries the old
   value.
2. The outbox row of a `photo` entry: kind, `msg_id =
   DiscriminatorFor("photo")`, `occurred_at` = now, payload fields
   including §0.14's `account_id` / `peer_id`.
3. A photo change to id X makes the loop send exactly one
   `getUserProfilePhotos{user_id, offset 0, limit 1}`; the answer (X,
   date D) writes `status` `online:D` with `observed_at` D.
4. Each skip rule of §1.2 on its own: D one second past now + 60 (and
   exactly now + 60 written); D exactly 30 days old written and one
   second older skipped; the answer names another id; an error answer;
   no answer before the timeout; an existing entry at D — a normal
   status entry, and a moment from before a restart.
5. No lookup when the photo is unchanged, `"empty"`, or out of scope.
6. Threading: with a transport that never answers, handling the
   `updateUser` returns at once and the next update is processed; the
   same `updateUser` three times sends one request; queue overflow drops
   and counts.
7. Stories: an in-scope user with stories at D1 < D2 → two moments and
   `story` = D2 at now; the same update again → nothing; a later D3 →
   one moment and `story` D3 with old value D2; an empty list → nothing;
   a group or channel chat id → nothing; out of scope → nothing; date 0
   skipped; an update before `my_id` is processed after it arrives.
8. A19: a moment older than the stored status leaves `activity_latest`
   unchanged; a newer one becomes the latest, and the next status update
   carries it as `old_value`.
9. Persistence: §1.5's record survives a new `MessageCache` on the same
   file (a repeated story update after "restart" writes nothing); a v5
   database migrates in place; the pruner deletes records older than 31
   days and keeps younger ones.
10. Policy: `getUserProfilePhotos` accepted only in the exact shape
    (other keys, `limit` ≠ 1, `offset` ≠ 0, `user_id` ≤ 0 or a string
    rejected); every request listed as "must stay rejected" in §3 is
    rejected; `loadActiveStories`, if added, only with the allowed lists.
11. Wiring: through the real registrations and `Worker`, the lookup loop
    is started — the test fails when the start call is removed.
12. One record across clients: for a photo moment and a story moment,
    the `record_id` that the capture's sync code computes for the outbox
    row equals the `record_id` computed from the spec function the
    vector tests pin, for (`activity`, empty account, the user's peer,
    `DiscriminatorFor("status")`, D) — i.e. what tdesktop computes for
    the same moment.

## HOW TO VERIFY

1. Every test written first and seen failing (or, for behaviour that
   already works, seen failing under the matching break).
2. `dotnet build` 0 warnings; full `dotnet test` **three times** —
   report all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, rebuild
   with `--no-incremental` before the next one:
   a) the photo id printed as a signed `int64`;
   b) `"empty"` not written on the first observation;
   c) the photo moment uses now instead of the photo date;
   d) the 30-day rule uses `>=` instead of `>`;
   e) the `+ 60` future rule removed;
   f) the current-photo id check removed;
   g) the existing-entry record kept only in memory;
   h) every moment overwrites `activity_latest` (A19 removed);
   i) the update handler waits for the photo-date answer;
   j) `Worker` does not start the lookup loop;
   k) only the newest story gets a moment;
   l) the `story` field uses the story date as `observed_at`;
   m) the policy accepts `getUserProfilePhotos` with `limit` 100;
   n) the story handler skips the scope check.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- §1–§3 implemented; tests 1–12 pass; all 14 breaks caught; every
  tdesktop difference handled as the rules say.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: a plan 05 Task 4d row whose commit column says
  `(this commit)` — **never invent a hash**; the header test count; §8
  entry with the TDLib findings of §3 and every difference found. Do
  **not** mark the task as verified.

## FINAL REPORT (six short points)

1. Commit hash (copied from `git log -1` after committing) and files
   changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–n), by test name.
4. Anything done differently from this prompt, and why.
5. TDLib findings with evidence (file, line, commit): which MTProto
   method each added request maps to and why it is invisible; whether
   `updateChatActiveStories` arrives without `loadActiveStories`; whether
   `chatPhoto.added_date` is MTProto `photo.date`; anything that differs
   from tdesktop (the personal-photo case at least).
6. What Task 10 must still check on a real session (story updates
   arriving at all, photo dates, invisibility on a real account, anything
   else you noticed). Report, do not fix.

## OUT OF SCOPE

Avatar and story media backups (tdesktop `MaybeBackupUserpic`,
`MaybeBackupStoryMedia`); tdesktop's buffer for untracked peers
(`PushToActivityBuffer`) and its snapshot when tracking starts
(`RecordCurrentState`); groups' and channels' photos and stories; real
TDLib; running any server; session protection (Task 11); protocol
changes; the web app.
