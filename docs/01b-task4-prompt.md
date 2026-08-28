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
  Expected right now: 67 passed, 0 failed. If it is not 67, stop and say
  so. Then read PROGRESS.md — it holds the verified environment facts and
  every deliberate deviation from the plan. Do not re-derive them.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code. Tunable values live in the
      server_settings table (sizes, limits, quotas, rate limits, timeouts).
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
      migrations. New migrations are fine if you need one.

============================================================
YOUR TASK — PLAN 01b, TASK 4 ONLY
============================================================

Base text: the plan file at
C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01b-backend-sync.md
section "## Task 4: Kalit o'ramlari".

Do Task 4 and nothing else. Do not start Task 5 (pagination and stats).

Follow its Steps 1-3, with the four corrections below. The corrections
win wherever they disagree with the plan.

WHAT THIS TABLE HOLDS: wrapped copies of the master key. The server can
never open them — the key-encryption key is derived on the client. This
is the most security-sensitive data in the system, which is why the
corrections below are not optional polish.

--- CORRECTION 1 (MANDATORY): the rate limit is already a setting ---

The plan hardcodes the limit:

    o.PermitLimit = 5;
    o.Window = TimeSpan.FromHours(1);

That breaks K1, and needlessly: `auth.wrap_rate_per_hour` already exists
in server_settings with a default of 5. Read it instead.

The rate limiter is configured on builder.Services before the database
exists, so you cannot read the setting there directly. Use a policy with
a partition factory, which runs per request and can resolve
SettingsService from context.RequestServices:

    options.AddPolicy("keywrap", context => { ... });

Inside, build a fixed-window partition whose PermitLimit comes from
SettingsService. Note in an Uzbek comment that the value is captured when
a partition is first created, so a changed setting applies to partitions
created afterwards — this is acceptable and should not be "fixed" by
rebuilding the limiter per request, which would disable limiting entirely.

--- CORRECTION 2 (MANDATORY): partition the limiter, do not share one ---

The plan's limiter has no partition key, so ALL callers share one bucket
of 5 per hour. That is worse than useless: the first caller to spend the
budget locks out everyone else, and one buggy client denies the real user
their own recovery path.

Partition by the calling device id (ClaimTypes.NameIdentifier).

The setting's description text says "IP bo'yicha" (by IP). Update that
description in SettingsService.CreateDefaults() to say per device, and
explain why in the commit body: this endpoint requires authentication, so
an unauthenticated attacker cannot reach it at all, and behind a reverse
proxy the remote IP is the proxy's rather than the caller's. The device
id is the identity actually being limited.

This also keeps the tests honest — each test enrols its own device and
therefore gets its own bucket, instead of tests stealing each other's
quota and failing in whatever order they happen to run.

--- CORRECTION 3 (MANDATORY): who may create and delete a wrap ---

The plan puts every endpoint behind a bare `.RequireAuthorization()`, so
any enrolled device can DELETE a recovery wrap — destroying a way back
into the account.

Apply the project's existing role model (see PROGRESS.md, "Auth modeli"):

    GET    /keys/wraps          any authenticated device
    GET    /keys/wraps/{id}     any authenticated device  (rate limited)
    POST   /keys/wraps          admin only  -> .RequireAuthorization("admin")
    DELETE /keys/wraps/{id}     admin only  -> .RequireAuthorization("admin")

Reading stays open to devices because a device restoring access needs the
wrap. Creating and destroying recovery paths is management, and matches
how settings and device revocation are already gated.

--- CORRECTION 4 (MANDATORY): the audit trail must record the actor ---

AuditService.WriteAsync now takes an actorDeviceId parameter
(action, targetDeviceId, actorDeviceId, detail, ct). The plan's calls
omit it, so a wrap retrieval would be recorded without saying who
retrieved it — on the one table where that question matters most.

Pass the caller's device id from the endpoint into KeyWrapService and on
into every audit call it makes (keywrap.created, keywrap.retrieved,
keywrap.deleted).

--- CORRECTION 5: Task 4 has no tests; write them ---

The plan's steps are "write the service, write the endpoints, commit".
Add tests/CustomSync.Tests/KeyWrapTests.cs covering at least:

  1. POST as admin creates a wrap; GET / lists it
  2. GET /{id} returns the base64 fields and updates LastUsedAt
  3. GET /{id} for an unknown id -> 404
  4. DELETE as admin removes it; a second DELETE is harmless (K4)
  5. POST as a device role -> 403
  6. DELETE as a device role -> 403
  7. any wraps request with no token -> 401
  8. audit rows exist for created / retrieved / deleted, each with the
     acting device recorded
  9. exceeding auth.wrap_rate_per_hour on GET /{id} -> 429

For test 9, set auth.wrap_rate_per_hour to a small number via
SettingsService.SetAsync before the requests and restore it in a
try/finally. Other tests share this database and a leftover limit will
break them — this has already gone wrong three times on this project.

Use WebApplicationFactory<Program>, as
tests/CustomSync.Tests/AuthorizationTests.cs already does.

🔴 Assert only on rows your own test created. Give each wrap a label
containing a GUID and filter by it. Never assert on a global count.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 76 passed, 0 failed  (67 today + 9 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing them (K6)
  - No test leaves auth.wrap_rate_per_hour changed
  - Exactly one commit, K7 style — a WHY body, not a change list

If the count is not 76, STOP and report it. Never adjust a test to match
a number.

============================================================
FINAL REPORT — keep it to these five points
============================================================

  1. The final `dotnet test` summary line only (not the whole log).
  2. `git show --stat HEAD` and your commit message.
  3. Rate limiting: confirm the limit comes from auth.wrap_rate_per_hour
     and that two different devices get separate buckets.
  4. Authorization: confirm a device-role token gets 403 on POST and
     DELETE but 200 on GET.
  5. Anything that looked wrong, ambiguous or that you had to guess at —
     name it even if the tests pass.

  Do not claim a behaviour works unless you tested that exact behaviour.
  Answering "both work" after testing only one has happened once on this
  project and cost a full review cycle.

OUT OF SCOPE
  - Do not start Task 5 or any later task in plan 01b.
  - Do not run the application or any server.
  - Do not add packages; the rate limiter ships with ASP.NET Core 8.
  - Do not "improve" unrelated code you read; report it instead.
