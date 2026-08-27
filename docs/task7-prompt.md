Execute the task described below. It is an implementation job in an
existing C# repository — not a document to review, summarise or score.
The only output that counts is committed source code.

REPOSITORY
  C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server
  Branch: Oybek. Never push to a remote named "upstream".

STEP 0 — BEFORE YOU WRITE ANYTHING
  From the repository root run:

      dotnet test

  Expected right now: 37 passed, 0 failed. Report that number before
  continuing. If it is not 37, stop and report it — something changed
  under this brief.

  Then read PROGRESS.md in that repository. It carries the verified
  environment facts and every deliberate deviation from the plan text.
  Do not re-derive any of it.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code. Tunable values live in the
      server_settings table. The ONE exception is bootstrap values that
      must exist before the database is reachable — those go in
      appsettings.json. See correction 3 below, which is exactly such a
      case.
  K4  Writes are idempotent.
  K6  TDD, in this order: write the failing test -> run it -> SEE IT
      FAIL -> write the minimal implementation -> run it -> see it pass.
      Never write the implementation first.
  K7  ONE commit. Imperative subject line. The body explains WHY, not
      what — the diff already shows what.
      Do NOT write a "Key changes:" bullet list of what you did. That is
      the single most repeated review finding on this project; three
      commits in a row have failed on it.
      Never add a Co-Authored-By trailer.
  Language: code comments and user-facing strings in Uzbek, matching the
      surrounding files. See src/CustomSync.Core/RecordId.cs for the
      house style. Identifiers stay in English.
  Never run the application (dotnet run) or any server. Tests only.
  Never edit anything under C:\TBuild\tdesktop\.
  Do not modify PROGRESS.md, scripts/db-bootstrap.ps1, or existing
      migrations.

============================================================
TASK 7 — SERILOG AND THE AUDIT TRAIL
============================================================

Base text: the plan file at
C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01a-backend-foundation.md
section "## Task 7: Serilog va audit log".

Follow its Steps 1-4 and 6. Steps 5 and its acceptance criteria are
superseded by the corrections below.

--- 1. PACKAGES: PIN THE VERSION ---

    dotnet add src/CustomSync.Api package Serilog.AspNetCore --version 8.0.*
    dotnet add src/CustomSync.Api package Serilog.Sinks.File --version 5.0.*

Unversioned dotnet add resolves a package built for net10.0 and fails
with NU1202. This project has hit that four times already.

--- 2. 🔴 SKIP STEP 5 — WRITE TESTS INSTEAD ---

Step 5 says to start the server and check the table by hand with psql.
Do not. This project never starts a server, and leaving Task 7 without
tests would make the audit trail the only unverified surface in the
backend — an audit trail nobody checks is worse than none, because it
is trusted.

New file: tests/CustomSync.Tests/AuditTests.cs. Cover:

    enroll writes a device.enrolled row
    revoke writes a device.revoked row
    a settings change writes a settings.changed row   (see 4)
    RecentAsync returns newest first and honours its limit

Use WebApplicationFactory<Program> for the endpoint-level ones, as
tests/CustomSync.Tests/AuthorizationTests.cs already does, and the
DatabaseFixture for any service-level one.

🔴 Assert only on rows your own test created. WebApplicationFactory
connects to the shared development database and it already holds rows
from other tests. A test asserting on a global count or on "the newest
row in the table" WILL fail intermittently — this exact mistake has
already been made twice here. Filter by the deviceId or action value
your test produced.

--- 3. LOG RETENTION BELONGS IN appsettings.json, NOT IN CODE ---

The plan hardcodes the rolling interval and retainedFileCountLimit: 14
inside Program.cs. Those are retention settings, which K1 forbids as
literals.

They cannot live in server_settings either: Serilog is configured before
the DI container and the database exist. They are bootstrap values, like
the connection string, so put them in appsettings.json under a "Logging"
or "Serilog" section and read them from configuration. State that
reasoning in a short Uzbek comment so the next reader does not "fix" it
back into server_settings.

--- 4. AUDIT SETTINGS CHANGES TOO, AND RECORD THE ACTOR ---

The plan only audits enroll and revoke. Two additions:

(a) PUT /api/v1/settings/{key} must write an audit row. It is the most
    destructive endpoint in the system: setting retention.activity_days
    to 1 discards the central archive. Record the key, the old value and
    the new value.

(b) Every audit row must record WHO acted, not only what was acted on.
    Task 6b introduced roles: DELETE /devices/{id} can be called by an
    admin revoking someone else, or by a device revoking itself. The
    plan's snippet logs only the target device. Log the caller's
    deviceId (from ClaimTypes.NameIdentifier) as well — an audit trail
    that cannot answer "who did this" does not do its job.

Choose sensible action names in the existing dotted style
(device.enrolled, device.revoked, settings.changed).

--- 5. THE logs/ DIRECTORY MUST BE GITIGNORED ---

Serilog.Sinks.File will create logs/customsync-*.log at the repository
root the first time any test spins up the host. Add the directory to
.gitignore in the same commit. If you forget, the next `git add -A`
commits log files — and request logs contain device ids.

--- 6. AuditService AND THE SHARED DbContext ---

AuditService takes the scoped SyncDbContext and calls SaveChangesAsync.
That commits any other pending tracked changes in the same context, so
an audit write is not isolated from whatever the endpoint was doing.

For this task that is acceptable — write the audit row AFTER the
operation it records has been committed, never before. Do not wrap them
in one transaction and do not make a failed audit write roll back a
successful revoke. Add a one-line Uzbek comment noting the ordering
requirement, because the reverse order would let a failed audit undo a
security action.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 41 passed, 0 failed  (37 today + 4 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing them (K6)
  - .gitignore covers logs/
  - git status shows no .log files and no appsettings.Development.json
  - Exactly one commit, K7 style — a WHY body, not a change list

If the count is not 41, STOP and report it. Never adjust a test to match
a number.

NOTE ON THE PLAN'S ACCEPTANCE CRITERIA
  The plan's "Qabul qilish mezonlari (1a)" section says 14 tests and
  tells you to run the app with curl. Both are stale — that text predates
  Tasks 4-6b. The Definition of Done above replaces it. Do not run the
  app to satisfy it.

REPORT BACK
  1. The full output of the final dotnet test run.
  2. git show --stat HEAD and your commit message.
  3. Anything in this brief or in the plan that looked wrong, ambiguous
     or that you had to guess at — name it even if your tests pass.
     A silent guess costs far more to find later than a flagged one.
  4. Do not claim a file exists unless you created it. A previous
     delegate's report referenced a walkthrough document it never wrote.

OUT OF SCOPE
  - Do not start plan 01b.
  - Do not run the application or any server.
  - Do not "improve" unrelated code you happen to read; report it.
  - Do not add packages beyond the two above.
