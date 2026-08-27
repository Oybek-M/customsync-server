Execute the task described below. It is an implementation job in an
existing C# repository — not a document to review, summarise or score.
The only output that counts is committed source code.

REPOSITORY
  C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server
  Branch: Oybek. Never push to a remote named "upstream".

HOW TO REPORT
  Do NOT narrate each step as you go. Do not paste long build or test
  logs. Work quietly, then give ONE short final report in the format at
  the bottom of this brief.

STEP 0 — BEFORE YOU WRITE ANYTHING
  From the repository root run `dotnet test`.
  Expected right now: 41 passed, 0 failed. If it is not 41, stop and say
  so. Then read PROGRESS.md — it holds the verified environment facts and
  every deliberate deviation from the plan text. Do not re-derive them.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code. Tunable values live in the
      server_settings table (batch sizes, page sizes, limits, timeouts).
  K3  Offset pagination is forbidden. Keyset (cursor) only. Never Skip().
  K4  Writes are idempotent — any push may be retried with the same result.
  K6  TDD in this order: write the failing test -> run it -> SEE IT FAIL
      -> write the minimal implementation -> run it -> see it pass.
  K7  ONE commit. Imperative subject. The body explains WHY, not what.
      Do NOT write a "Key changes:" bullet list — that has been the most
      repeated review finding on this project. No Co-Authored-By trailer.
  Language: code comments in Uzbek, matching the surrounding files.
      See src/CustomSync.Core/RecordId.cs for the house style.
      Identifiers stay in English.
  Never run the application (dotnet run) or any server. Tests only.
  Never edit anything under C:\TBuild\tdesktop\.
  Do not modify PROGRESS.md, scripts/db-bootstrap.ps1, or existing
      migrations.

============================================================
YOUR TASK — PLAN 01b, TASK 1 ONLY
============================================================

Base text: the plan file at
C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01b-backend-sync.md
section "## Task 1: Cursor ajratish va monotonlik kafolati".

Do Task 1 and nothing else. Do not start Task 2 (push/pull endpoints)
or any later task.

Follow its Steps 1-7, with the four corrections below. The corrections
win wherever they disagree with the plan.

WHY THIS TASK MATTERS: seq is allocated from a locked counter row, not
BIGSERIAL. With BIGSERIAL two concurrent transactions can take 5 and 6
and commit out of order; a reader polling in between advances its cursor
past 5 and never sees it. The record is lost silently and nobody notices
for weeks. Do not "simplify" this to a sequence or an identity column.

--- CORRECTION 1 (MANDATORY): account_hash ---

The plan predates spec §0.12. Since then every record carries an
account_hash, the records.account_hash column exists and is NOT NULL, and
RecordId.Compute takes FIVE arguments:

    RecordId.Compute(kind, accountHash, peerHash, msgId, occurredAt)

The plan's UpsertSql does not mention account_hash at all, so it will
fail at runtime against the real schema. You must:

  - add account_hash to the INSERT column list, to the SELECT list and
    as an @account_hash parameter in UpsertSql;
  - use the five-argument RecordId.Compute in the push validation;
  - add AccountHash to StoredRecord in PullResponse.cs and select it in
    PullAsync;
  - set AccountHash in the test helper Make().

Read src/CustomSync.Core/Contracts/SyncRecord.cs before you start — it
already has the field and explains the rule: account_hash is "" (empty
string) only for kind "activity", and a real hash for every other kind.
Use "" in the test helper only if the test uses kind activity.

--- CORRECTION 2: the raw SQL column names are already correct ---

Database columns are snake_case (record_id, peer_hash, occurred_at). The
plan's SQL matches. Do NOT change them to quoted "PeerHash" style — the
project moved to snake_case deliberately so that the raw SQL in this very
task needs no quoting.

--- CORRECTION 3: pin the package version ---

    dotnet add src/CustomSync.Services package Npgsql --version 8.0.*

Unversioned dotnet add resolves a package built for net10.0 and fails
with NU1202. This project has hit that five times.

--- CORRECTION 4: the two tests must not see each other's records ---

Both tests in SeqMonotonicityTests share one database (IClassFixture
gives one DatabaseFixture per class, not per test). The plan's helper
uses the same peer_hash "peer01" in both, and
Concurrent_pushes_are_never_skipped_by_a_polling_reader asserts
seen.Count == 60. If the other test runs first its 20 records are counted
too and the assertion fails — an order-dependent failure that will look
random.

Give each test its own peer_hash (pass it into Make) and count only
records belonging to that test. Do not weaken the assertion to make it
pass.

--- ON STEP 6 (prove the bug is real) ---

Step 6 tells you to swap the counter CTE for BIGSERIAL and watch the test
fail. Keep the intent, but BIGSERIAL is not directly available — seq is a
plain bigint column with no sequence attached. Use this substitute
instead: temporarily replace the CTE's allocated value with

    SELECT COALESCE(MAX(seq), 0) + 1 FROM records

which has the same race, run the concurrency test, and confirm it fails.

🔴 THEN REVERT IT and re-run until green. Do not commit the broken
version. If you skip this step, say so in the report rather than
claiming you did it.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 43 passed, 0 failed  (41 today + 2 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing them (K6)
  - The concurrency test was confirmed to fail without the counter lock
    (Step 6), then reverted
  - Exactly one commit, K7 style — a WHY body, not a change list

If the count is not 43, STOP and report it. Never adjust a test to match
a number.

============================================================
FINAL REPORT — keep it to these five points
============================================================

  1. The final `dotnet test` summary line only (not the whole log).
  2. `git show --stat HEAD` and your commit message.
  3. Step 6: did the concurrency test actually fail without the counter
     lock? Yes / No / skipped.
  4. Anything in the plan or this brief that looked wrong, ambiguous or
     that you had to guess at — name it even if the tests pass. A silent
     guess costs far more to find later than a flagged one.
  5. Anything you changed beyond the four corrections, and why.

  Do not claim a file exists unless you created it.

OUT OF SCOPE
  - Do not start Task 2 or any later task in plan 01b.
  - Do not run the application or any server.
  - Do not add packages beyond Npgsql.
  - Do not "improve" unrelated code you read; report it instead.
