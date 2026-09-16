Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Tasks 1–2 — TDLib interop and the one-time login flow

## REPOSITORY

- **Paths depend on the computer** (laptop and PC differ). First run
  `hostname`, then take `<server>` and `<tdesktop>` from the table in
  `<tdesktop>\docs\MACHINES.md` (find `<tdesktop>`: whichever of
  `C:\TBuild\tdesktop` / `D:\Oybek\Telegram\tdesktop` exists; otherwise the
  folder containing `custom_db.cpp`). Check every path exists before using
  it; `git -C <server> remote -v` must show `Oybek-M/customsync-server`.
  Never guess a path — if not found, stop and ask.
- Repo: `<server>` (`customsync-server`), branch `Oybek`. Push only to
  `origin Oybek`.
- Plan text (READ-ONLY, another repo):
  `<tdesktop>\docs\superpowers\plans\2026-07-29-multi-device-sync-05-capture-service.md`
  §Task 1 and §Task 2, plus the "Oldindan bilinishi kerak bo'lgan
  cheklovlar" section. The requirements below override the plan where
  they differ, and each difference says why.
- Starting state: `dotnet build` 0 warnings, `dotnet test` 169/169.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md` §2 (including "Plan 05 ga kelganda hal qilinadigan:
   `libtdjson`"), §3 ("must not break"), §4 (test rules), §7 (environment).
2. `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`
   §0 — the revision list. §0.5 (sha256 before encryption) and §0.8 (no
   media paths in records) constrain later tasks; read them now so the
   interop layer does not make them impossible.
3. `src/CustomSync.Core/RecordId.cs` and `src/CustomSync.Core/Contracts/`
   — the capture service must produce the SAME record contract as the
   backend, which is why it references `CustomSync.Core`.
4. `src/CustomSync.Api/Program.cs` — how configuration and DI are wired here.

## NON-NEGOTIABLE RULES

- 🔴 **Do NOT build TDLib.** No `git clone` of tdlib/td, no `cmake`, no
  compiling `libtdjson`. Which of the three options is used to obtain it
  (prebuilt package / build on the VPS / Docker) is the OWNER's open
  decision, recorded in `PROGRESS.md` §2. Your work must build and test
  **without the native library present at all**.
- 🔴 **Do NOT run the service or the login.** No `dotnet run`, no
  `--login`, no starting any server. `dotnet build` and `dotnet test`
  only. The phone/code/2FA login is performed by the OWNER, once, on the
  VPS.
- 🔴 **Secrets never enter the repo and never enter the logs.**
  `api_id`, `api_hash`, phone number, login code, 2FA password, and the
  TDLib session all stay out of git and out of log output. If you need a
  value to test with, invent an obvious fake one.
- **K1** — no config literals for anything tunable at runtime. Paths and
  credentials come from `IConfiguration` (like `Storage:MediaRoot` does);
  do not invent new `server_settings` keys in this task — the capture
  service is a separate process and does not read the server's settings
  table.
- **K6** — TDD: each test written first and seen failing.
- **K7** — one commit per task (two commits total), imperative subject,
  WHY in the body, **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`.
- Do not change existing projects' behaviour. `CustomSync.Api`,
  `CustomSync.Services`, `CustomSync.Data` keep passing their 169 tests
  unchanged; you may only add the new project to the solution.

## 🔴 1. Project skeleton that builds without TDLib

- `dotnet new worker -n CustomSync.Capture -o src/CustomSync.Capture`,
  add to `CustomSync.sln`, reference `CustomSync.Core`, add
  `Microsoft.Data.Sqlite` **with `--version 8.0.*`** (without the version
  the tool resolves a net10.0 package and fails with `NU1202` — this has
  happened five times in this repo).
- `appsettings.json` holds only non-secret defaults (`ApiId: 0`,
  `ApiHash: ""`, directories, `Backend:BaseUrl`).
- `.gitignore`: add `src/CustomSync.Capture/appsettings.Production.json`
  next to the existing `appsettings.Development.json` line.
- The native library location is configuration, not a constant:
  `Telegram:TdJsonPath` (empty = let the OS resolve `tdjson` normally).
  Wire it with `NativeLibrary.SetDllImportResolver` so the same build
  works with a `.so` on the VPS and a `.dll` on a dev machine.

## 🔴 2. Interop correctness — three things that bite

The plan's sample `TdJsonInterop` is a sketch. These are required:

1. **`td_receive` returns memory owned by TDLib and valid only until the
   next `td_receive` on that thread.** Copy the string out immediately
   (`Marshal.PtrToStringUTF8`) before doing anything else. Never store or
   pass the `IntPtr` around.
2. **`td_send`/`td_execute` take UTF-8.** Marshal with UTF-8 explicitly
   and free what you allocate in a `finally`. ANSI marshalling corrupts
   any non-ASCII text — which in this project means chat content, names,
   and the Uzbek/Russian text this service exists to capture.
3. **`td_receive` must be called from exactly ONE thread.** One dedicated
   background loop owns it; everything else communicates through queues
   or completion sources.

## 🔴 3. `TdClient`: request/response correlation

- Every outgoing request carries a unique `@extra`; the reply with the
  same `@extra` completes that request's `TaskCompletionSource`.
- Anything without a matching `@extra` is an update — published to
  subscribers, never dropped silently.
- A request must not hang forever: a timeout (configurable, sensible
  default) completes it with a failure **and removes the pending entry**
  — a leak here grows unbounded in a 24/7 process.
- `error` objects from TDLib complete the request as a failure carrying
  code and message, not as a success.
- Disposal stops the loop and fails every pending request; a second
  dispose is harmless.
- Behind an interface (`ITdClient`) so the authenticator and every later
  task can be tested with a fake. **Tests never P/Invoke.**

## 🔴 4. Logging must redact

TDLib JSON carries `api_hash`, the phone number, the login code and the
2FA password in plain fields. A service that logs raw JSON writes all of
them to the journal, where they outlive the login. Required:

- A single redaction helper applied to every logged TDLib payload,
  covering at least: `api_hash`, `phone_number`, `code`, `password`,
  `recovery_code`, `email_address`, `authentication_code`.
- Redact the VALUE, keep the key, so logs stay debuggable.
- Message text is not a credential, but it is private: do not log message
  bodies at information level.

## 🔴 5. `TdAuthenticator`: a state machine that fails loudly

Drive `updateAuthorizationState`:

| State | Action |
|---|---|
| `waitTdlibParameters` | `setTdlibParameters` (see below) |
| `waitPhoneNumber` | `setAuthenticationPhoneNumber` — interactive mode only |
| `waitCode` | `checkAuthenticationCode` — interactive mode only |
| `waitPassword` | `checkAuthenticationPassword` — interactive mode only |
| `ready` | authorized |
| `closed` | stop cleanly, do not loop |
| `waitRegistration` | **refuse**: this flow never creates a new account |
| `waitEmailAddress`, `waitEmailCode`, or anything unknown | **refuse** with the state name in the message |

Refusing on unknown states matters because newer TDLib versions add them;
silently ignoring one leaves the service sitting in a loop that looks
healthy and captures nothing.

`setTdlibParameters`: `use_message_database = true` (the deletion handler
needs the cached text — the whole point of this service),
`use_file_database = true`, `use_chat_info_database = true`,
`use_secret_chats = false` (device-bound by design, never synced).

**Two modes, one code path:**
- `--login` (interactive, run by a human once): may prompt on the console.
- Normal service start: **never prompts**. If the session is not already
  authorized, it logs one clear line saying to run `--login` on the VPS
  and exits non-zero. A systemd unit has no console; prompting there
  produces a service that hangs forever with no explanation.

## 🔴 6. Preflight before anything else

On start, check and report — each as its own clear message, not an
exception trace:
- the native library is loadable (or the configured path does not exist);
- `ApiId` / `ApiHash` are set (values never printed);
- `DatabaseDirectory` and `FilesDirectory` exist or can be created, and
  are writable.

## WHAT NOT TO ADD

No message cache, no update handlers, no scope evaluation, no backend
sync client, no media downloads, no storage maintenance — those are plan
05 Tasks 3–9 and each one is its own prompt. No systemd unit file yet.
No changes to the backend API. No new `server_settings` keys.

## TESTS — `tests/CustomSync.Tests`, new files

Add to the existing test project (reference `CustomSync.Capture`).
**No test may require `libtdjson`, PostgreSQL, or network access.**

`CaptureClientTests.cs` (against a fake transport, not P/Invoke):
1. Two concurrent requests each get their own reply, matched by `@extra`.
2. A payload with no `@extra` is delivered as an update.
3. A TDLib `error` reply fails that request with code and message.
4. A request with no reply times out and the pending entry is gone
   afterwards (assert the internal count, not just the exception).
5. Dispose fails pending requests and stops the loop; double dispose is a
   no-op.

`CaptureAuthTests.cs`:
6. Each state above produces the expected outgoing request.
7. `waitRegistration` refuses with a clear message.
8. An unknown/new state refuses and names the state.
9. `setTdlibParameters` carries the four flags above (assert
   `use_secret_chats == false` explicitly).
10. Non-interactive start with an unauthorized session does NOT prompt,
    returns non-zero, and the message names `--login`.
11. `closed` stops without looping.

`CaptureRedactionTests.cs`:
12. A payload containing api_hash, phone_number, code and password logs
    none of those values, and still contains their keys.
13. Redaction survives nested objects and does not corrupt other fields.

`CapturePreflightTests.cs`:
14. Missing native library path → specific message, no unhandled exception.
15. Missing ApiId/ApiHash → specific message that does not contain the
    (fake) hash value.

## HOW TO VERIFY

1. Every test above written first and seen failing.
2. `dotnet build` 0 warnings. `dotnet test` all green (169 + new).
3. Confirm the build does not need TDLib: there must be no native library
   anywhere in the repo, and no test may load one. State in the report how
   you verified this.
4. `git status` clean and `git ls-files` shows no `appsettings.Production.json`,
   no `.so`/`.dll` binaries, no credentials.
5. Deliberate breaks — do each, confirm at least one test fails, revert:
   a) drop the `@extra` correlation and hand every reply to the first waiter;
   b) leave the pending entry in place on timeout;
   c) treat a TDLib `error` reply as success;
   d) accept `waitRegistration` instead of refusing;
   e) set `use_secret_chats = true`;
   f) remove `password` from the redaction list;
   g) let the non-interactive path prompt for the code.
   If a break does NOT fail a test, add or fix the test.

## DEFINITION OF DONE

- Sections 1–6 implemented; tests 1–15 pass; all 7 breaks caught.
- Two commits (Task 1, Task 2), pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 section started with a Task 1–2 row, deviations
  from the plan listed with reasons, and a short note stating that the
  service cannot run until the owner (a) provides `libtdjson`, (b)
  supplies `api_id`/`api_hash`, and (c) performs the one-time `--login`.

## FINAL REPORT (seven short points)

1. Commit hashes and files changed.
2. Test count before/after; exact `dotnet test` summary line.
3. Which deliberate break failed which test (a–g).
4. Anything from this prompt you did differently, and why.
5. How you verified no test or build step needs the native library.
6. Anything in the plan text you think is wrong or unsafe for later tasks.
7. Exactly what the owner must do before this service can run.

## OUT OF SCOPE

Plan 05 Tasks 3–10, obtaining or building `libtdjson`, the systemd unit,
deployment, the owner's Telegram login, and plan 04 Tasks 4/5.
