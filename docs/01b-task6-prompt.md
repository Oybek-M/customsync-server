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
  Expected right now: 85 passed, 0 failed. If it is not 85, stop and say
  so. Then read PROGRESS.md — it holds the verified environment facts and
  every deliberate deviation from the plan. Do not re-derive them.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code. Tunable values live in the
      server_settings table.
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
YOUR TASK — PLAN 01b, TASK 6 ONLY
============================================================

Base text: the plan file at
C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01b-backend-sync.md
section "## Task 6: WebSocket bildirishnoma".

Do Task 6 and nothing else. Do not start Task 7 (.cmx format).

Follow its Steps 1-4, with the four corrections below. The corrections
win wherever they disagree with the plan.

WHAT THIS IS: a "there is new data, pull now" signal. No records travel
over this channel. If it drops, the system keeps working — periodic pull
remains the primary path. Keep it that simple.

--- CORRECTION 1 (MANDATORY, SECURITY): do not replace o.Events ---

The plan says to put OnMessageReceived inside AddJwtBearer like this:

    o.Events = new JwtBearerEvents { OnMessageReceived = ... };

Program.cs ALREADY assigns o.Events, and the existing handler is
OnTokenValidated, which rejects tokens belonging to revoked devices via
DeviceRevocationCache. Assigning a new JwtBearerEvents object would throw
that away, and revoked devices would silently start working again.

ADD OnMessageReceived to the existing JwtBearerEvents object. Do not
create a second one. Both handlers must be present when you are done.

The existing test Revoked_token_is_immediately_unauthorized in
tests/CustomSync.Tests/AuthorizationTests.cs guards this. If you see it
fail, this is why — fix the events wiring, do not touch the test.

--- CORRECTION 2 (MANDATORY): the token travels in the query string ---

That is unavoidable — browsers cannot set headers on a WebSocket
handshake — but it means the token can reach places a header would not.

Verify that `app.UseSerilogRequestLogging()` does not write the query
string into logs/customsync-*.log. Serilog's default template logs
RequestPath, which excludes the query string, so this is probably already
safe — but confirm it rather than assume, and if the token does appear,
configure the logging to strip or redact it.

Say in your report which of the two you found.

Restrict the query-string fallback to the WebSocket path only, as the
plan already does with StartsWithSegments("/ws"). A token in a query
string must never be accepted on the ordinary REST endpoints.

--- CORRECTION 3 (MANDATORY): Task 6 must have tests ---

The plan's Step 3 is manual: run the server, connect with wscat, push
with curl. This project does not start servers, and that would leave the
notification path untested.

WebApplicationFactory can drive WebSockets:
`factory.Server.CreateWebSocketClient()` then
`ConnectAsync(new Uri("ws://localhost/ws/notify?access_token=..."), ct)`.

New file tests/CustomSync.Tests/NotifyTests.cs covering at least:

  1. connecting with a valid token succeeds
  2. connecting without a token is rejected
  3. device A is connected; device B pushes a new record -> A receives
     {"type":"changes","seq":N}
  4. the pushing device does NOT receive its own notification
  5. a push whose records are all duplicates sends no notification
     (nothing changed, so nobody should be woken)
  6. a plain GET on /ws/notify that is not a WebSocket request -> 400

Give each test its own peer hash (a GUID). Use a bounded wait when
expecting a message — a few seconds with a CancellationToken, never an
unbounded ReceiveAsync, or a broken implementation hangs the suite
instead of failing it.

🔴 Do NOT use `pull?since=0` anywhere. The shared development database is
past 500 rows and a record no longer lands on the first page. See
SyncEndpointsTests.SinceBeforePush for the pattern.

--- CORRECTION 4: Prune loses sockets that arrive while it runs ---

Prune rebuilds each device's ConcurrentBag and assigns it back. A socket
registered by another thread during that rebuild is dropped, and that
device stops receiving notifications until it reconnects.

Impact is small — pull is the fallback — but do not leave it silent.
Either make the replacement safe, or add an Uzbek comment stating the
race and why it is tolerated. Your choice; say which you picked.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 91 passed, 0 failed  (85 today + 6 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing them (K6)
  - Both OnMessageReceived and OnTokenValidated are wired (correction 1)
  - Exactly one commit, K7 style — a WHY body, not a change list

If the count is not 91, STOP and report it. Never adjust a test to match
a number.

============================================================
FINAL REPORT — keep it to these five points
============================================================

  1. The final `dotnet test` summary line only (not the whole log).
  2. `git show --stat HEAD` and your commit message.
  3. Correction 1: confirm both JWT event handlers are present, and that
     Revoked_token_is_immediately_unauthorized still passes.
  4. Correction 2: does the access token appear in the log files? State
     what you actually checked.
  5. Anything that looked wrong, ambiguous or that you had to guess at —
     name it even if the tests pass.

  Do not claim a behaviour works unless you tested that exact behaviour.

OUT OF SCOPE
  - Do not start Task 7 or any later task in plan 01b.
  - Do not introduce SignalR — plain WebSocket is deliberate, because the
    desktop agent is C++/Qt and has no maintained SignalR client.
  - Do not send record data over this channel; the payload stays a seq hint.
  - Do not run the application or any server.
