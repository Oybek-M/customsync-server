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
  Expected right now: 47 passed, 0 failed. If it is not 47, stop and say
  so. Then read PROGRESS.md — it holds the verified environment facts and
  every deliberate deviation from the plan. Do not re-derive them.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code. Tunable values live in the
      server_settings table (batch sizes, page sizes, limits, timeouts).
  K3  Offset pagination is forbidden. Keyset (cursor) only. Never Skip().
  K4  Writes are idempotent — any push may be retried with the same result.
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
      migrations.

============================================================
YOUR TASK — PLAN 01b, TASK 2 ONLY
============================================================

Base text: the plan file at
C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01b-backend-sync.md
section "## Task 2: Push va pull endpoint'lari".

Do Task 2 and nothing else. Do not start Task 3 (media storage) or any
later task.

Follow its Steps 1-3, with the three corrections below. The corrections
win wherever they disagree with the plan.

--- CORRECTION 1 (MANDATORY): tombstone handling ---

The plan's Task 2 body never mentions tombstones, but the REVIZIYA block
at the top of the same plan file requires Task 2 to handle them
(spec §0.3). Read that block.

There is a problem the plan did not notice, now resolved in spec §0.13
(read it — same spec file, section 0.13): §0.3 says the server deletes
the original record identified by payload.target_record_id, but payload
is an encrypted byte[] the server cannot read.

The resolution, already decided and recorded, is that a tombstone also
carries its target in CLEARTEXT:

  - add a nullable string TargetRecordId to SyncRecord (Core/Contracts)
    and to StoredRecord (PullResponse.cs);
  - add a nullable target_record_id column to the records table via a
    NEW migration (do not edit existing migrations);
  - carry it through SyncService.PushAsync's upsert SQL and PullAsync.

Server behaviour when kind == "tombstone" (must be idempotent, K4):

  1. delete the row whose record_id equals target_record_id;
  2. store the tombstone record itself;
  3. if the target row does not exist, still store the tombstone —
     another device may push the target later, and it must be deleted
     the moment it arrives.

Point 3 is not optional. Without it a tombstone that arrives before its
target silently loses the deletion.

Reject a tombstone whose TargetRecordId is null with PushOutcome.Error
and message "missing_target". Records of any other kind must have it
null — reject with "unexpected_target" if set.

This leaks nothing: record_id values are already cleartext (they are the
primary key and come back in every pull response).

--- CORRECTION 2: Task 2 must have tests ---

The plan's Step 2 is only `dotnet build`. That would leave push and pull
— the core of the whole system — with no test at all. Write
tests/CustomSync.Tests/SyncEndpointsTests.cs covering at least:

  1. POST /sync/push with valid records -> 200, results say "created"
  2. the same push repeated -> "duplicate", nothing new stored (K4)
  3. push larger than sync.push_batch_size -> 400 "batch_too_large"
  4. GET /sync/pull returns pushed records and advances NextSince
  5. pull honours the limit and clamps it to sync.pull_batch_size
  6. a tombstone deletes its target, and a tombstone whose target has
     not arrived yet is still stored (both halves of correction 1)
  7. push or pull without a token -> 401

Use WebApplicationFactory<Program>, as
tests/CustomSync.Tests/AuthorizationTests.cs already does. Enrol a
device there to get a token.

🔴 Assert only on records your own test created. WebApplicationFactory
connects to the shared development database and it already holds rows
from other tests. Any assertion on a global count or on "the newest row"
WILL fail intermittently — this exact mistake has been made three times
here. Give each test its own peer_hash (a GUID) and filter by it.

--- CORRECTION 3: sync endpoints stay open to the device role ---

Use `.RequireAuthorization()` with NO policy name, exactly as the plan
has it. Do not add the "admin" policy — ordinary sync devices must be
able to push and pull. Revoked devices are already rejected in the JWT
pipeline by DeviceRevocationCache, so do not add a per-request database
check for device liveness; that would put a query on the hot path.

--- NOTES ---

SyncService.PushAsync already validates that record_id matches the
recomputed value and rejects mismatches. Do not repeat that check in
the endpoint.

The NotifyHub stub in Step 2 is correct — the real implementation is
Task 6. Leave it as a stub.

sync.push_max_bytes exists in server_settings and the plan does not use
it. Enforcing it is optional; if you skip it, say so in the report.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 54 passed, 0 failed  (47 today + 7 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing them (K6)
  - A new migration adds target_record_id; existing migrations untouched
  - Exactly one commit, K7 style — a WHY body, not a change list

If the count is not 54, STOP and report it. Never adjust a test to match
a number.

============================================================
FINAL REPORT — keep it to these five points
============================================================

  1. The final `dotnet test` summary line only (not the whole log).
  2. `git show --stat HEAD` and your commit message.
  3. Tombstone: confirm both halves work — target present, and target
     arriving later.
  4. Anything in the plan or this brief that looked wrong, ambiguous or
     that you had to guess at — name it even if the tests pass.
  5. Anything you changed beyond the three corrections, and why.

  Do not claim a file exists unless you created it.

OUT OF SCOPE
  - Do not start Task 3 or any later task in plan 01b.
  - Do not implement NotifyHub for real (Task 6).
  - Do not run the application or any server.
  - Do not "improve" unrelated code you read; report it instead.
