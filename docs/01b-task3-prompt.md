Execute the task described below. It is an implementation job in an
existing C# repository — not a document to review, summarise or score.
The only output that counts is committed source code.

REPOSITORY
  C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server
  Branch: Oybek. Never push to a remote named "upstream".

HOW TO REPORT
  Do NOT narrate each step as you go. Do not paste long build or test
  logs. Work quietly, then give ONE short final report in the format at
  the bottom.

STEP 0 — BEFORE YOU WRITE ANYTHING
  From the repository root run `dotnet test`.
  Expected right now: 58 passed, 0 failed. If it is not 58, stop and say
  so. Then read PROGRESS.md — it holds the verified environment facts and
  every deliberate deviation from the plan. Do not re-derive them.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code. Tunable values live in the
      server_settings table (sizes, limits, quotas, timeouts).
  K3  Offset pagination is forbidden. Keyset only. Never Skip().
  K4  Writes are idempotent — any upload may be retried with the same result.
  K6  TDD in this order: write the failing test -> run it -> SEE IT FAIL
      -> write the minimal implementation -> run it -> see it pass.
  K7  ONE commit. Imperative subject. The body explains WHY, not what.
      Do NOT write a "Key changes:" bullet list. No Co-Authored-By trailer.
  Language: code comments in Uzbek, matching the surrounding files.
      See src/CustomSync.Core/RecordId.cs for the house style.
      Identifiers stay in English.
  Never run the application (dotnet run) or any server. Tests only.
  Never edit anything under C:\TBuild\tdesktop\.
  Do not modify PROGRESS.md, scripts/db-bootstrap.ps1, or existing
      migrations. New migrations are fine and expected.

============================================================
YOUR TASK — PLAN 01b, TASK 3 ONLY
============================================================

Base text: the plan file at
C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01b-backend-sync.md
section "## Task 3: Media saqlash".

Do Task 3 and nothing else. Do not start Task 4 (key wraps) or later.

Follow its Steps 1-6, with the four corrections below. The corrections
win wherever they disagree with the plan.

--- CORRECTION 1 (MANDATORY): quota is missing entirely ---

The REVIZIYA block at the top of the same plan file requires Task 3 to
enforce quota (spec §0.9), but the Task 3 body never mentions it. It only
checks media.max_upload_bytes and returns 413.

Add quota enforcement to PUT /api/v1/media/{hash}:

  - read storage.quota_total_mb and storage.quota_per_device_mb from
    SettingsService. Both default to 0, which means UNLIMITED — treat 0
    as "no limit", not as "nothing allowed";
  - if storing this blob would push total stored bytes over
    quota_total_mb, return 507 Insufficient Storage;
  - same for the uploading device against quota_per_device_mb;
  - 507 specifically, not 413. The client keeps the item in its outbox
    and retries later, so this must be distinguishable from "this file
    is too big", which will never succeed.

Per-device quota needs to know who uploaded a blob, and media_blobs has
no such column. Add a nullable uploaded_by_device_id via a NEW migration.
Blobs are content-addressed and deduplicated, so attribute a blob to the
first device that stored it and leave it unchanged on later duplicate
uploads.

--- CORRECTION 2 (MANDATORY): record_media is never written ---

PullAsync reads the record_media table to fill StoredRecord.MediaHashes,
but nothing ever writes to it. SyncService.PushAsync silently discards
record.Media. Media attached to a record therefore never survives a round
trip — pull always returns an empty list.

Fix it in this task, since it is what makes media usable at all:

  - PushAsync persists record.Media into record_media for records it
    stored or superseded;
  - it must be idempotent (K4) — re-pushing the same record must not
    duplicate rows or fail. record_media's primary key is
    (record_id, hash), so insert with ON CONFLICT DO NOTHING;
  - a record that is rejected (error, or suppressed by a tombstone)
    must not get media links.

Write a test that pushes a record carrying two media hashes and then
pulls it back with both hashes present.

--- CORRECTION 3: the plan's tests are not enough ---

The plan writes three MediaService tests and no endpoint tests at all.
Add tests for the HTTP surface too, using WebApplicationFactory<Program>
as tests/CustomSync.Tests/AuthorizationTests.cs already does.

Cover at least:

  1. PUT then GET returns the same bytes; HEAD says it exists
  2. HEAD for an unknown hash -> 404
  3. PUT larger than media.max_upload_bytes -> 413
  4. PUT over storage.quota_total_mb -> 507
  5. PUT over storage.quota_per_device_mb -> 507
  6. any media request without a token -> 401
  7. the record_media round trip from correction 2

Quota defaults are 0 (unlimited), so tests 4 and 5 must set the setting
first via SettingsService.SetAsync and restore it afterwards in a
try/finally — other tests share this database and will break if you
leave a quota in place. This has already gone wrong three times here.

🔴 Assert only on rows and files your own test created. Give each test
its own hash (a GUID) and filter by it. Never assert on a global count.

--- CORRECTION 4: the media root path ---

appsettings.json gets "Storage": { "MediaRoot": "/var/lib/customsync/media" },
which is a Linux path that does not exist on this Windows machine. The
service tests pass their own temp directory, so they are fine, but the
endpoint tests from correction 3 go through Program.cs and would try to
write there.

Override MediaRoot to a temp directory in the endpoint tests (via
WebApplicationFactory's configuration), and delete it afterwards. Do not
change the production default in appsettings.json.

MediaService must create the directory if it does not exist.

--- NOTES, NOT TASKS ---

The server cannot verify that the hash matches the content: the bytes it
receives are already encrypted, and per spec §0.5 the hash is computed
over the PLAINTEXT before encryption. Verification is therefore the
client's job after decryption. Add a short Uzbek comment saying so, so
nobody later "fixes" this by hashing the ciphertext — that would break
deduplication across devices completely.

Both PUT and GET currently buffer the whole blob in memory, and
media.max_upload_bytes defaults to 50 MB. Streaming would be better.
Do NOT change it in this task; mention it in your report instead.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 67 passed, 0 failed  (58 today + 9 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing them (K6)
  - A new migration adds uploaded_by_device_id; existing migrations
    untouched
  - No test leaves a quota setting behind
  - Exactly one commit, K7 style — a WHY body, not a change list

If the count is not 67, STOP and report it. Never adjust a test to match
a number.

============================================================
FINAL REPORT — keep it to these five points
============================================================

  1. The final `dotnet test` summary line only (not the whole log).
  2. `git show --stat HEAD` and your commit message.
  3. Quota: confirm 507 (not 413) for both total and per-device, and
     that 0 still means unlimited.
  4. record_media: confirm a pushed record's media hashes come back on
     pull, and that re-pushing does not duplicate them.
  5. Anything that looked wrong, ambiguous or that you had to guess at —
     name it even if the tests pass.

  Do not claim a file or a behaviour exists unless you verified it.
  Answering "both work" when you only tested one has already happened
  once on this project and cost a review cycle.

OUT OF SCOPE
  - Do not start Task 4 or any later task in plan 01b.
  - Do not convert media I/O to streaming.
  - Do not run the application or any server.
  - Do not "improve" unrelated code you read; report it instead.
