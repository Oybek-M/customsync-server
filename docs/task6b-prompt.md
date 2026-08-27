Execute the task described below. It is an implementation job in an
existing C# repository — not a document to review, summarise or score.
Do not rate it. The only output that counts is committed source code.

REPOSITORY
  C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server
  Branch: Oybek. Never push to a remote named "upstream".

STEP 0 — BEFORE YOU WRITE ANYTHING
  From the repository root run:

      dotnet test

  Expected right now: 27 passed, 0 failed. Report that number before
  continuing. If you cannot run the command or read its output, STOP and
  say so plainly — this task is test-driven and you must be able to
  verify your own work. If the number is not 27, stop and report it.

  Then read PROGRESS.md in that repository. It records the environment
  facts and every deliberate deviation from the plan. Do not re-derive
  any of it.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code. Every tunable value lives in
      the server_settings table. Forbidden as literals: intervals, batch
      sizes, page sizes, retention periods, thresholds, rate limits,
      timeouts, feature toggles.
  K4  Writes are idempotent.
  K6  TDD, in this order: write the failing test -> run it -> SEE IT
      FAIL -> write the minimal implementation -> run it -> see it pass.
      Never write the implementation first.
  K7  One commit. Imperative subject line. The body explains WHY, not
      what — the diff already shows what. Never add a Co-Authored-By
      trailer.
  Language: code comments and user-facing strings in Uzbek, matching the
      surrounding files. See src/CustomSync.Core/RecordId.cs for the
      house style. Identifiers stay in English.
  Never run the application (dotnet run) or any server. Tests only.
  Never edit anything under C:\TBuild\tdesktop\.

============================================================
TASK 6b — AUTHORIZATION ROLES AND IMMEDIATE REVOCATION
============================================================

WHY THIS EXISTS

After Task 6, any enrolled device can call POST /devices/codes (enroll
more devices), DELETE /devices/{id} (revoke ANY device) and
PUT /settings/{key}. One stolen phone therefore owns the server — the
worst case being setting retention.*_days to 1 and destroying the
central archive.

This lands before plan 01b because 01b builds the sync endpoints. Adding
a role to them afterwards means reopening the whole layer.

--- 1. SCHEMA: ADD A NEW MIGRATION ---

Add to the devices table:
    role TEXT NOT NULL DEFAULT 'device'          -- 'device' | 'admin'

Add to the enrollment_codes table:
    grants_role TEXT NOT NULL DEFAULT 'device'

The code itself decides which role it grants, so enrolling an admin is a
deliberate act and cannot happen by accident.

WARNING: add a NEW migration. Do not squash or edit existing migrations.

--- 2. CARRY THE ROLE THROUGH THE SERVICE ---

EnrolledDevice gains a Role field. Both RedeemAsync and RefreshAsync
must return it, because the endpoints need it to mint the token.

RedeemAsync must read grants_role from the enrollment code and store it
as the new device's role.

CRITICAL: RedeemAsync currently claims the code atomically with a
conditional ExecuteUpdateAsync inside a transaction. That atomicity is
deliberate and was added to fix a real bug — two concurrent requests
could otherwise both redeem one single-use code. DO NOT replace it with
a read-then-write. grants_role never changes after the row is created,
so you may read it in a separate query before the atomic claim without
weakening anything.

--- 3. ROLE CLAIM IN THE JWT ---

JwtIssuer.IssueAsync takes the role alongside deviceId:

    claims: [
        new Claim(ClaimTypes.NameIdentifier, deviceId),
        new Claim(ClaimTypes.Role, role)
    ]

In Program.cs:

    builder.Services.AddAuthorization(o =>
        o.AddPolicy("admin", p => p.RequireRole("admin")));

--- 4. PER-ENDPOINT PERMISSIONS ---

    POST   /devices/enroll      open (the code itself is the secret)
    POST   /devices/refresh     open (the refresh token is the secret)
    GET    /devices             admin
    POST   /devices/codes       admin
    DELETE /devices/{id}        admin, OR the caller's own deviceId
    GET    /settings            admin
    PUT    /settings/{key}      admin

The DELETE rule lets a device log itself out:

    var callerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    var isAdmin  = user.IsInRole("admin");
    if (!isAdmin && callerId != deviceId) return Results.Forbid();

The unused ClaimsPrincipal parameter already on that endpoint exists for
exactly this.

--- 5. IMMEDIATE REVOCATION, WITHOUT SLOWING SYNC ---

REQUIREMENT: a revoked device's token must stop working immediately, and
the sync hot path must gain NO extra database round trip. Calling
IsActiveAsync per request violates this — plan 01b calls push/pull very
frequently.

Solution: an in-memory revocation set. Add a singleton:

    public sealed class DeviceRevocationCache
    {
        private readonly ConcurrentDictionary<string, byte> _revoked = new();

        public bool IsRevoked(string deviceId) => _revoked.ContainsKey(deviceId);
        public void Add(string deviceId)       => _revoked[deviceId] = 0;

        public void Load(IEnumerable<string> revokedDeviceIds)
        {
            foreach (var id in revokedDeviceIds) _revoked[id] = 0;
        }
    }

Wire it up in three places:

  1. At startup, load once from devices WHERE revoked_at IS NOT NULL,
     next to the existing migration / EnsureDefaultsAsync block in
     Program.cs. Register it as a SINGLETON.

  2. DeviceService.RevokeAsync calls cache.Add(deviceId) after writing
     to the database, so the very next request rejects the token.

  3. In AddJwtBearer:

    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = ctx =>
        {
            var cache = ctx.HttpContext.RequestServices
                .GetRequiredService<DeviceRevocationCache>();
            var deviceId = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            if (deviceId is null || cache.IsRevoked(deviceId))
                ctx.Fail("device_revoked");

            return Task.CompletedTask;
        }
    };

This is an O(1) memory lookup — no database query, no effect on sync
throughput.

KNOWN LIMITATION — write it in a comment, do not fix it now: the cache
is per-process, so with multiple server instances a revocation on one
would not reach the others. PostgreSQL LISTEN/NOTIFY solves it and
belongs to plan 04/06, where multi-instance deployment is designed.
Today there is one instance.

Cheap defence in depth: change the auth.jwt_lifetime_minutes default
from 60 to 15 in SettingsService.CreateDefaults(). This is a
server_settings value, so no other code changes.

--- 6. BOOTSTRAP CLI ---

Extend the existing --create-enrollment-code path with an --admin flag:
with it the code gets grants_role='admin', otherwise 'device'.

Do NOT run it — the user runs it themselves.

--- 7. TESTS (WRITE THEM FIRST, SEE THEM FAIL) ---

New file: tests/CustomSync.Tests/AuthorizationTests.cs

    device role   -> POST /devices/codes            expect 403
    admin role    -> POST /devices/codes            expect 200
    device role   -> PUT  /settings/{key}           expect 403
    device        -> DELETE its own deviceId        expect 204
    device        -> DELETE another deviceId        expect 403
    revoked token -> any protected request          expect 401

The last one proves the "immediate" requirement and is the most
important: obtain the token BEFORE revoking, then call RevokeAsync, then
send a request with that same token.

CRITICAL: assert only on records your own test created.
WebApplicationFactory connects to the shared development database and
will contain devices from other tests. A test asserting on a global
count WILL fail — this mistake has already happened twice in this
project. Filter by the deviceId or name you created.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 33 passed, 0 failed  (27 today + 6 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing them (K6)
  - git status does not show appsettings.Development.json
  - Exactly one commit, K7 style

If the count is not 33, STOP and report it. Never adjust a test to match
a number.

REPORT BACK
  1. The full output of the final dotnet test run.
  2. git show --stat HEAD and your commit message.
  3. Anything in this brief that looked wrong, ambiguous or that you had
     to guess at — name it even if your tests pass. A silent guess costs
     far more to find later than a flagged one.

OUT OF SCOPE
  - Do not start plan 01b or Task 7.
  - Do not run the application or any server.
  - Do not modify PROGRESS.md, db-bootstrap.ps1, or existing migrations.
  - Do not "improve" unrelated code you happen to read; report it
    instead.
  - Do not add packages beyond what this brief requires.
