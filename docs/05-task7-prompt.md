Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 7 — keep the capture session invisible

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
  builds there — another session owns it. Read only the documents named
  below.
- Starting state: HEAD = the commit that added this prompt or later,
  `dotnet build` 0 warnings, `dotnet test` 356/356.
- **Stale test vectors (seen on the laptop, `DESKTOP-L2J53IK`, 2026-10-01).**
  If `<tdesktop>\docs\sync-protocol\test-vectors.json` has no `key_wrap`,
  `fingerprint` or `discriminator` section, the `<tdesktop>` checkout is
  behind its remote and ~17 tests fail with a message saying so. Do NOT
  pull `<tdesktop>`. Instead:
  `git -C <tdesktop> show origin/Oybek:docs/sync-protocol/test-vectors.json > <file outside the repo>`
  and set `CUSTOMSYNC_TEST_VECTORS` to that file for every `dotnet test`.
  Never commit that file.
- **PC only (hostname `DESKTOP-5CAUS66`):** a stale Visual Studio NuGet
  config points at a deleted `E:\Application's datas\...` folder, so a
  plain `dotnet build` / `dotnet test` fails with `MSB4018` or `NU1301`.
  Do NOT edit anything under `C:\Program Files (x86)` or the user NuGet
  config. Instead:
  1. write a temporary `nuget.config` **outside the repo** containing
     `<packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>`
     and `<fallbackPackageFolders><clear/></fallbackPackageFolders>`;
  2. `dotnet restore --configfile <that file>` (no new packages are
     needed);
  3. from then on **always** `dotnet build --no-restore` and
     `dotnet test --no-build`; for deliberate breaks rebuild with
     `dotnet build --no-restore --no-incremental`.
  Never commit that file. On the laptop none of this is needed.

## WHY THIS TASK MATTERS

The capture service is a 24/7 TDLib session on the owner's Telegram
account. CustomMod is built around Ghost Mode: the owner is not shown
online, messages are not marked read, nobody sees "typing…". A
headless session that leaks any of these signals — even once, even
through a single careless request added in a later task — silently
breaks the main promise of the product, and the owner would only learn
about it from other people.

Two things are needed:

1. **The session tells Telegram it is offline** as soon as it is
   authorized, and the service refuses to run if that cannot be
   confirmed.
2. **The service physically cannot send a request that leaks presence
   or read state.** A comment saying "never call `viewMessages`" is not
   a guarantee; a gate in the one place every request passes through is.
   The gate is an **allow-list**, not a deny-list: a deny-list only
   blocks the methods someone remembered (and TDLib has many more —
   `openMessageContent` marks voice notes as listened, `openStory` marks
   stories as seen, `sendChatAction` shows "typing…"). With an allow-list,
   a future task that needs a new request (Task 8: `downloadFile`) has to
   add it on purpose, and a test shows the list.

Lesson from every previous plan 05 task: a protection that exists in the
code but is not wired in (`TdRedactor`, authorization, the cache) passes
all its own tests. Here the wiring is the deliverable — it must be
proved through the production path, not a hand-built copy.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else. Do not state TDLib
behaviour you did not confirm from TDLib's documentation or source —
say "unconfirmed" instead.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block at the top, §3, §4, and the
   verification sections for plan 05 Tasks 1–2, 3 and 6a. Defects found
   there will be looked for again: unwired protection, tests that build
   their own `ServiceCollection` instead of the real one, failures that
   exit with code 0, logs that leak secrets.
2. Plan (READ-ONLY) `<tdesktop>\docs\superpowers\plans\2026-07-29-multi-device-sync-05-capture-service.md`,
   **Task 7** (if your checkout lacks it, read it with
   `git -C <tdesktop> show origin/Oybek:<that path>`).
3. Capture code: `Tdlib/TdClient.cs` (`SendAsync` and `Execute` — both
   reach the transport), `Tdlib/ITdClient.cs`, `Tdlib/ITdTransport.cs`,
   `Tdlib/TdAuthenticator.cs` (the four requests it sends),
   `Tdlib/AuthorizationGate.cs`, `Capture/CaptureUpdateHandler.cs`
   (`getMe`, `getMessage`), `Capture/CaptureHandlerRegistration.cs` (it
   wraps the existing `ITdClient` registration), `Worker.cs`,
   `Program.cs`, `Preflight/CapturePreflight.cs` (`nativeLibChecker`),
   and `tests/CustomSync.Tests/CaptureCacheVerificationTests.cs`
   (`Test22`, the only test that runs `Worker` today).

Today the production code sends exactly six TDLib request types:
`setTdlibParameters`, `setAuthenticationPhoneNumber`,
`checkAuthenticationCode`, `checkAuthenticationPassword`, `getMe`,
`getMessage`. Confirm this yourself before writing the allow-list.

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service or any CLI mode, no
  `dotnet run`. `dotnet build` / `dotnet test` only. All TDLib traffic
  in tests goes through fake `ITdTransport` / `ITdClient` objects.
- **Never log** request payloads, phone numbers, codes, passwords,
  `api_hash`, message text or peer ids. The request `@type`, option
  names and reply `@type` are fine. Existing debug logging must keep
  going through `TdRedactor`.
- **Fail closed:** if the session cannot be confirmed offline, the
  service stops with exit code 1. Running visibly online is worse than
  not running.
- **K1** — tunables from `IConfiguration`. **K6** — TDD: each test
  first, seen failing. **K7** — one commit, imperative subject, WHY in
  the body, **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. No backend
  (`CustomSync.Api` / `Services` / `Data`) changes.

## 🔴 1. Request gate (`src/CustomSync.Capture/Tdlib/`)

New `TdRequestPolicy` (static is fine) used by `TdClient` for **both**
`SendAsync` and `Execute`:

- Parse the request once. The check runs on the **same parsed object
  that is then serialised and handed to the transport** — never on the
  raw input string — so what is checked is exactly what is sent.
- `@type` must be a JSON string; missing, non-string, or a request that
  is not a JSON object → rejected. Duplicate property names → rejected.
- Exact, case-sensitive match against the allow-list: the six types
  above plus `setOption` and `getOption`.
- `setOption` is allowed **only** as
  `{"name":"online","value":{"@type":"optionValueBoolean","value":false}}`.
  Any other name, `value:true`, a missing value, or `optionValueEmpty`
  (which resets the option to its default) → rejected.
- `getOption` is allowed only with `name == "online"`.
- A rejected request throws a dedicated exception (e.g.
  `TdRequestNotAllowedException`), logs one warning with the `@type`
  only, **never reaches the transport**, and leaves nothing behind in the
  pending-request table.
- The allow-list is one visible constant. A comment next to it says that
  adding a method is a privacy decision and names the methods that must
  never be added.

## 🔴 2. Invisibility step

New `SessionInvisibility.EnsureAsync(ITdClient, TimeSpan timeout, CancellationToken)`
(name is yours) that:

1. sends `setOption online = false` and requires the reply `@type` to be
   `ok`;
2. sends `getOption online` and requires exactly
   `{"@type":"optionValueBoolean","value":false}`;
3. returns a result (success / failure + reason without secrets); a
   TDLib `error`, a timeout, `optionValueBoolean true`, or any other
   reply is a failure.

Timeout from `Capture:SessionInvisibilityTimeoutSeconds` (default 30,
positive integers only; an invalid value falls back to the default with
a warning).

## 🔴 3. Wiring (`Worker.cs`, `Program.cs`)

- `Worker` calls the step **right after** `AuthorizationGate` reports
  ready and **before** it logs "Capture service authorized and running."
  On failure: log an error with the reason, `ExitCode = 1`, stop the
  host, never log the "running" line.
- Move the TDLib registrations out of `Program.cs` into an extension
  method (e.g. `AddTdlibClient(...)`) that `Program.cs` calls, so tests
  exercise the real registration.
- Give `Worker` a seam for the native-library probe so a test can get
  past preflight without TDLib: register the probe in DI from a
  registration method `Program.cs` calls (the real probe is today's
  `NativeLibrary.TryLoad` logic), and have `Worker` pass it to
  `CapturePreflight.Check(config, nativeLibChecker: ...)`. No
  `GetService` + `?.` fallbacks — use `GetRequiredService`.
- Tests that change `Environment.ExitCode` must restore it in `finally`
  and run in a non-parallel xUnit collection.

## TESTS

Fake transports record every payload they receive.

1. Each of the six existing request types passes the gate: drive the
   real `TdAuthenticator` **in interactive mode with a fake
   `IConsolePrompt`** (service mode never sends the phone/code/password
   requests) through all four states, and the real handler's `getMe` /
   `getMessage` paths, over a real `TdClient` with a fake transport that
   auto-replies `ok` / a valid object.
2. Each of these is rejected and the transport receives nothing:
   `viewMessages`, `openChat`, `closeChat`, `openMessageContent`,
   `readAllChatMentions`, `readAllChatReactions`,
   `readAllMessageThreadMentions`, `readChatList`,
   `toggleChatIsMarkedAsUnread`, `sendChatAction`, `openStory`,
   `sendMessage`, `forwardMessages`, `deleteMessages`, `joinChat`,
   `logOut`, `destroy`, `terminateAllOtherSessions`.
3. The allow-list and the list in test 2 do not intersect (asserted on
   the real constant).
4. `setOption`: `online=false` passes; `online=true`, `optionValueEmpty`,
   missing value, another option name → rejected.
5. `getOption`: `online` passes, any other name rejected.
6. Malformed requests (no `@type`, numeric `@type`, JSON array,
   duplicate `@type` keys, `"ViewMessages"` with a capital V) → rejected.
7. `Execute` applies the same gate (a forbidden request never reaches
   `ITdTransport.Execute`).
8. A rejected request leaves `PendingRequestCount` at 0 and logs only the
   `@type` (no payload content — use a request containing a marker string
   and assert the marker is absent from the logs).
9. `EnsureAsync`: `ok` + `false` → success; TDLib `error` on either
   request, timeout, `optionValueBoolean true`, `optionValueEmpty` →
   failure.
10. **Production path:** build the service provider with the same
    registration methods `Program.cs` calls, replace only `ITdTransport`
    with a fake, resolve `ITdClient` (through the `AddCaptureHandlers`
    wrapper) and send `viewMessages` → rejected, transport saw nothing.
11. Architecture: no type in `CustomSync.Capture` except `TdClient` takes
    `ITdTransport` in a constructor (reflection over the assembly), so
    nothing can route around the gate.
12. **Worker, success:** preflight probe passes, fake client reaches
    `authorizationStateReady`, answers `ok` / `false` → the transport
    log shows `setOption online=false` then `getOption online`, both
    before the "authorized and running" log line; the host is not
    stopped.
13. **Worker, failure:** the fake answers `optionValueBoolean true` (and,
    separately, a TDLib `error`) → `ExitCode == 1`, the host is stopped,
    "running" is never logged.
14. The real probe registration resolves the real probe type (so test
    12's fake probe cannot hide a broken production registration).

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, then
   **rebuild with `--no-incremental`** before the next one:
   a) `Worker` never calls the invisibility step;
   b) `Worker` calls it but ignores a failure;
   c) the step accepts `optionValueBoolean true`;
   d) the gate covers `SendAsync` only, not `Execute`;
   e) replace the allow-list with a deny-list of only `viewMessages`,
      `openChat`, `readAllChatMentions`;
   f) allow `setOption` with any value;
   g) remove `checkAuthenticationPassword` from the allow-list;
   h) log "running" before the step completes;
   i) make the gate opt-in (constructor flag, default off) so the
      production registration is unguarded;
   j) log the full request payload on rejection;
   k) compare `@type` case-insensitively.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–3 implemented; tests 1–14 pass; all 11 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 Task 7 row, header test count, the new config
  key `Capture:SessionInvisibilityTimeoutSeconds`, and a note that the
  plan's **manual check (Step 3) moves to Task 10** — it needs a real
  account and must not be run now (the VPS is not trusted; see the
  deploy-audit section). Do not touch the deploy-audit section except to
  add findings.

## FINAL REPORT (six short points)

1. Commit hash and files changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–k), by test name.
4. Anything done differently from this prompt, and why.
5. TDLib facts this code relies on (`setOption`/`getOption` for
   `online`, the reply shapes) — for each, the source you confirmed it
   from, or "unconfirmed".
6. Any remaining way the session could reveal presence or read state
   that the gate does not stop (think about TDLib behaviour, not just
   our requests). Report, do not fix.

## OUT OF SCOPE

Task 8 media downloads (it will add `downloadFile` to the allow-list
itself), reconnect/re-authorization flows, graceful TDLib `close` on
shutdown, Tasks 8–10, the `photo` activity field, obtaining `libtdjson`,
the owner's login, running anything against a real account, deployment.
