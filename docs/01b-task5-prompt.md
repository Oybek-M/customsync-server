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
  Expected right now: 76 passed, 0 failed. If it is not 76, stop and say
  so. Then read PROGRESS.md — it holds the verified environment facts and
  every deliberate deviation from the plan. Do not re-derive them.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code. Tunable values live in the
      server_settings table (page sizes, limits, timeouts).
  K3  Offset pagination is FORBIDDEN. Keyset only. Never Skip().
      This task is where that rule earns its keep.
  K4  Writes are idempotent.
  K6  TDD in this order: write the failing test -> run it -> SEE IT FAIL
      -> write the minimal implementation -> run it -> see it pass.
  K7  ONE commit. Imperative subject. The body explains WHY, not what.
      Do NOT write a "Key changes:" bullet list. No Co-Authored-By trailer.
  Language: code comments in Uzbek, matching the surrounding files.
      Identifiers stay in English.
  Never run the application (dotnet run) or any server. Tests only.
  Never edit anything under C:\TBuild\tdesktop\.
  Do not modify PROGRESS.md, scripts/db-bootstrap.ps1, or existing
      migrations.

============================================================
YOUR TASK — PLAN 01b, TASK 5 ONLY
============================================================

Base text: the plan file at
C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01b-backend-sync.md
section "## Task 5: Keyset pagination va statistika".

Do Task 5 and nothing else. Do not start Task 6 (WebSocket).

Follow its Steps 1-7, with the five corrections below. The corrections
win wherever they disagree with the plan.

--- CORRECTION 1 (MANDATORY): account_hash ---

The plan predates spec §0.12. Its test helper calls

    RecordId.Compute(RecordKind.Deleted, "peerP", index, occurredAt)

with four arguments. RecordId.Compute now takes FIVE
(kind, accountHash, peerHash, msgId, occurredAt), and SyncRecord has a
required AccountHash field. Fix the helper. Read
src/CustomSync.Core/Contracts/SyncRecord.cs first; there are existing
examples in tests/CustomSync.Tests/DedupConflictTests.cs.

--- CORRECTION 2 (MANDATORY): the tests contaminate each other ---

Both plan tests use the peer hash "peerP" and share one database
(IClassFixture gives one DatabaseFixture per test class, not per test).
Worse, the first test asserts `Assert.Equal(40, seen.Count)` while
querying with NO peer filter — so it also counts the other test's 15
records whenever that one runs first. An order-dependent failure that
will look random.

Give each test its own peer hash (a GUID) and pass it as
RecordQuery.PeerHash so each test only ever sees its own rows.

--- CORRECTION 3 (MANDATORY): the seq tiebreaker is untested ---

RecordQueryService compares the tuple (occurred_at, seq), and the plan's
own comment says seq is required as a tiebreaker or rows sharing a
timestamp get skipped or repeated. But BOTH plan tests use
`1_700_000_000 + i`, so every record has a distinct occurred_at and the
tiebreaker never runs. The most delicate line in the file is uncovered.

Add a test that pages through records which all share ONE occurred_at
value — say 20 records, page size 5 — and assert no duplicates and no
missing rows. Then convince yourself it is not vacuous: temporarily drop
the `|| (r.OccurredAt == key && r.Seq < seq)` half of the comparison, see
the test fail, and put it back. Say in your report whether you did this.

--- CORRECTION 4 (MANDATORY): these are admin surfaces ---

The plan puts /records and /stats behind a bare `.RequireAuthorization()`,
so any sync device can browse every record in the system and read
storage statistics.

Both are web-app features (plan 03 is the web controller). Sync devices
get their data through /sync/pull and have no reason to browse. Use
`.RequireAuthorization("admin")` on both groups, matching how /settings
and device management are already gated. See PROGRESS.md, "Auth modeli".

--- CORRECTION 5: page limits must come from settings ---

StatsService.PeersAsync declares `int limit = 100` and StatsEndpoints
passes `limit ?? 100`. Two hardcoded literals, and K1 forbids exactly
this — `api.default_page_size` and `api.max_page_size` already exist.

Read both from SettingsService in the endpoint and clamp, the way
RecordEndpoints already does in the plan's own code. Do not leave a
default of 100 in the service signature.

--- NOTES, NOT TASKS ---

CurrentSnapshotAsync runs two queries (AnyAsync then MaxAsync) to avoid
MaxAsync throwing on an empty table. `MaxAsync(r => (long?)r.Seq) ?? 0`
would do it in one. Optional; mention it in the report either way.

Peer statistics group by peer_hash. That is deliberate and correct: the
hash is a deterministic HMAC, so COUNT/SUM/ORDER BY work fine while the
server still cannot resolve it to a person. Do not try to "improve" this
by storing names.

============================================================
TESTS REQUIRED
============================================================

tests/CustomSync.Tests/PaginationTests.cs and a stats test file.
At least these nine:

  1. paging under concurrent inserts — no duplicates, no gaps (plan)
  2. ascending and descending are exact reverses (plan)
  3. paging when every record shares one occurred_at (correction 3)
  4. filters work: peerHash, kind, and the from/to occurred_at range
  5. GET /records as admin -> 200, as a device role -> 403
  6. GET /stats as admin -> 200, as a device role -> 403
  7. limit is clamped to api.max_page_size
  8. /stats/peers sorting by bytes, count and recent
  9. /stats/storage counts match what the test pushed

For 5-7 use WebApplicationFactory<Program>, as
tests/CustomSync.Tests/AuthorizationTests.cs already does.

🔴 Assert only on rows your own test created — the development database
is shared and already holds rows from every earlier task. Test 9 in
particular must not assert on global totals: push a known set under your
own peer hash and assert on that, or compare before/after deltas.
Assertions on global counts have broken this project three times.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 85 passed, 0 failed  (76 today + 9 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing them (K6)
  - No `Skip(` anywhere in the new code (K3)
  - Exactly one commit, K7 style — a WHY body, not a change list

If the count is not 85, STOP and report it. Never adjust a test to match
a number.

============================================================
FINAL REPORT — keep it to these five points
============================================================

  1. The final `dotnet test` summary line only (not the whole log).
  2. `git show --stat HEAD` and your commit message.
  3. Correction 3: did you temporarily break the seq tiebreaker and see
     the new test fail? Yes / No / skipped.
  4. Authorization: confirm a device-role token gets 403 on both
     /records and /stats.
  5. Anything that looked wrong, ambiguous or that you had to guess at —
     name it even if the tests pass.

  Do not claim a behaviour works unless you tested that exact behaviour.

OUT OF SCOPE
  - Do not start Task 6 or any later task in plan 01b.
  - Do not run the application or any server.
  - Do not add packages.
  - Do not "improve" unrelated code you read; report it instead.
