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
  Expected right now: 91 passed, 0 failed. If it is not 91, stop and say
  so. Then read PROGRESS.md — it holds the verified environment facts and
  every deliberate deviation from the plan. Do not re-derive them.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code. Tunable values live in the
      server_settings table.
  K3  Offset pagination is forbidden. Keyset only. Never Skip().
  K4  Writes are idempotent — importing the same file twice must leave
      the same state.
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
  🔴 Do NOT reassign `o.Events` in Program.cs. It already holds TWO
      handlers — OnTokenValidated (revoked-device check) and
      OnMessageReceived (WebSocket token). A fresh JwtBearerEvents object
      silently destroys one of them.

============================================================
YOUR TASK — PLAN 01b, TASK 7 ONLY
============================================================

Base text: the plan file at
C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01b-backend-sync.md
section "## Task 7: `.cmx` almashuv formati".

Do Task 7 and nothing else. Do not start Task 8 (test vectors).

Follow its Steps 1-7, with the five corrections below. The corrections
win wherever they disagree with the plan.

THE CENTRAL IDEA, keep it: import goes through SyncService.PushAsync
rather than having its own merge path. A second implementation would
drift from the sync one and the two would start behaving differently on
the same input. Do not add separate merge logic.

--- CORRECTION 1 (MANDATORY): the record contract has grown ---

The plan predates spec §0.12 and §0.13. SyncRecord now has TWO fields the
plan never mentions:

  AccountHash     required, "" only for kind "activity"
  TargetRecordId  nullable, set only on kind "tombstone"

And RecordId.Compute takes FIVE arguments
(kind, accountHash, peerHash, msgId, occurredAt).

Three places break:

  a) the test helper Make() — four-argument Compute, no AccountHash;
  b) the export endpoint's `new SyncRecord { ... }` projection — it lists
     fields one by one and omits both new ones. AccountHash is `required`
     so this will not compile; TargetRecordId would compile and be
     silently dropped;
  c) StoredRecord must carry both so export can copy them.

🔴 (b) is the dangerous one. A tombstone exported without
TargetRecordId produces a .cmx that CANNOT be imported: PushAsync
rejects it with "missing_target". The deletion is lost, and the file
looks fine until someone tries to restore from it.

Add a test that exports and re-imports a tombstone and asserts it comes
back intact. See tests/CustomSync.Tests/TombstoneOrderingTests.cs for how
tombstones are built.

--- CORRECTION 2 (MANDATORY): export must not load the whole database ---

The plan's export does:

    var page = await sync.PullAsync(0, int.MaxValue);

then filters `since`, `until` and `peerHash` in C#. That reads every
record and every payload in the database into memory before discarding
most of them. The development database is already past 500 records and
only grows.

Filter in the database instead. RecordQueryService already supports
PeerHash, FromOccurredAt and ToOccurredAt — use it, and page through with
its keyset cursor rather than one unbounded read.

Note also that the plan computes `var snapshot = await
query.CurrentSnapshotAsync();` and never uses it. Use it as the query
snapshot so a long export is not disturbed by records arriving mid-run.

--- CORRECTION 3 (MANDATORY): MediaService.StoreAsync signature changed ---

The plan calls:

    await media.StoreAsync(hash, content, new byte[12], ct);

The signature is now
`StoreAsync(string hash, byte[] encryptedContent, byte[] nonce,
string? deviceId = null, CancellationToken ct = default)`, so that
fourth positional argument lands on `deviceId` and will not compile.

Pass the importing device id there — imported blobs count against that
device's quota, and attributing them to nobody would let import bypass
the per-device limit entirely. Pass `ct` by name.

--- CORRECTION 4: import and export are admin operations ---

The plan uses a bare `.RequireAuthorization()`. Export dumps the entire
record store into one file; import injects records wholesale. Both are
management actions performed from the web app, and a sync device has no
reason to do either — it has /sync/push and /sync/pull.

Use `.RequireAuthorization("admin")`, matching /records and /stats.
See PROGRESS.md, "Auth modeli".

--- CORRECTION 5: say out loud that server exports carry no media ---

The plan passes `media: new Dictionary<string, byte[]>()`, so a
server-side export never contains blobs even when records reference them.
Per spec §0.7 media may live in a separate archive, so this is a
defensible choice — but right now it is silent, and someone will restore
from such a file and find every attachment missing.

Keep the behaviour. Add an Uzbek comment at the call site and mention it
in your report. Do not implement media export in this task.

============================================================
TESTS REQUIRED
============================================================

tests/CustomSync.Tests/InterchangeTests.cs. At least these eight:

  1. round trip preserves every record field, INCLUDING AccountHash
  2. import produces the same state as push (the plan's test)
  3. a tombstone survives export and re-import with TargetRecordId intact
     (correction 1)
  4. a manifest whose FormatVersion is higher than supported is rejected
  5. POST /import as admin with a valid .cmx -> 200 with created/
     duplicate/error counts
  6. POST /import with bytes that are not a zip -> 400 invalid_cmx
  7. GET /export as admin returns a .cmx that CmxReader can read and that
     contains the record the test pushed
  8. /import and /export with a device-role token -> 403

Use WebApplicationFactory<Program> for 5-8, as
tests/CustomSync.Tests/AuthorizationTests.cs already does.

🔴 Give each test its own peer hash (a GUID) and assert only on your own
records. Do NOT use `pull?since=0` — the shared development database is
past 500 rows and a record no longer lands on the first page. See
SyncEndpointsTests.SinceBeforePush.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 99 passed, 0 failed  (91 today + 8 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing them (K6)
  - Exporting then re-importing a tombstone works end to end
  - Exactly one commit, K7 style — a WHY body, not a change list

If the count is not 99, STOP and report it. Never adjust a test to match
a number.

============================================================
FINAL REPORT — keep it to these five points
============================================================

  1. The final `dotnet test` summary line only (not the whole log).
  2. `git show --stat HEAD` and your commit message.
  3. Correction 1: confirm a tombstone survives export and re-import,
     and say which test proves it.
  4. Correction 2: what does export do now instead of
     PullAsync(0, int.MaxValue)?
  5. Anything that looked wrong, ambiguous or that you had to guess at —
     name it even if the tests pass.

  Do not claim a behaviour works unless you tested that exact behaviour.

OUT OF SCOPE
  - Do not start Task 8 or any later task in plan 01b.
  - Do not add a separate merge path for import.
  - Do not implement media export.
  - Do not run the application or any server.
