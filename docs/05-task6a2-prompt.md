Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 6a-2 — `--set-key` unwraps the master key from the passphrase wrap

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
- **`<tdesktop>` is READ-ONLY.** No edits, commits, pushes or builds
  there — another session owns it. Read only the protocol documents
  named below.
- Starting state: HEAD `f51d955` or later, `dotnet build` 0 warnings,
  `dotnet test` 317/317.
- **PC only (hostname `DESKTOP-5CAUS66`):** a stale Visual Studio NuGet
  config points at a deleted `E:\Application's datas\...` folder, so a
  plain `dotnet build` / `dotnet test` fails with `MSB4018` or `NU1301`.
  Do NOT edit anything under `C:\Program Files (x86)` or the user NuGet
  config. Instead:
  1. write a temporary `nuget.config` **outside the repo** containing
     `<packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>`
     and `<fallbackPackageFolders><clear/></fallbackPackageFolders>`;
  2. `dotnet restore --configfile <that file>` (no new packages are
     needed — everything is in the BCL);
  3. from then on **always** `dotnet build --no-restore` and
     `dotnet test --no-build`; for deliberate breaks rebuild with
     `dotnet build --no-restore --no-incremental`.
  Never commit that file. On the laptop none of this is needed.

## WHY THIS TASK MATTERS

Task 6a made the capture service push encrypted records, but its
`--set-key` asks for the master key as 64 hex characters. **tdesktop
never shows or exports the master key** (spec §4.4.0) — it only shows a
fingerprint (FP). The one way the VPS service can get the key is the
passphrase wrap the owner already uploaded from tdesktop:
fetch the wrap from the server, open it with the owner's passphrase,
show the FP so the owner can compare it with tdesktop's Sync tab, then
store the key. Without this the sync client cannot be deployed.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block at the top, §3, §4, and the
   verification sections "Plan 05 Task 6a tekshiruvi" and "Task 4c".
   The same kinds of defect will be looked for again: a contract tested
   only against a fake that does not match the real server, secrets
   lost or leaked on an error path, untested wiring.
2. Spec (READ-ONLY) `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`
   **§4.4.0** (wrap format, FP), §4.1, §4.4, §5.7.
3. `<tdesktop>\docs\sync-protocol\test-vectors.json` sections
   **`key_wrap`** (2 cases, each with a `wrong_passphrase`) and
   **`fingerprint`** (3 cases). Read them through the existing
   `tests/CustomSync.Tests/TestVectors.cs` helper — do not copy the file.
4. Server side (read, do not change):
   `src/CustomSync.Api/Endpoints/KeyEndpoints.cs` (list and get),
   `src/CustomSync.Services/KeyWrapService.cs` (`WrapSummary`),
   `src/CustomSync.Api/Program.cs` — the `keywrap` rate limiter:
   **`GET /api/v1/keys/wraps/{id}` is limited to 5 per hour per device
   (`auth.wrap_rate_per_hour`), rejections are HTTP 429.** HTTP JSON is
   snake_case; `created_at` is a `DateTime` (ISO string), not a number.
5. Capture code from 6a: `Sync/SyncCliCommands.cs`, `SyncCrypto.cs`,
   `DeviceCredentials.cs`, `CaptureSyncHttpClient.cs` (note
   `ParseExpiresAt` and why it exists), `CaptureSyncRunner.cs`
   (`EnsureAccessTokenAsync`), `Program.cs`,
   `tests/CustomSync.Tests/CaptureSyncTests.cs` and
   `CaptureSyncVerificationTests.cs`.

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service, `--login`, `--set-key` or
  `--enroll`, no `dotnet run`. `dotnet build` / `dotnet test` only. No
  test may need `libtdjson`, PostgreSQL, a running server or network —
  HTTP is faked with an `HttpMessageHandler`, the console with the
  existing `IConsolePrompt`.
- **Never log or print** the passphrase, the KEK, the master key (hex or
  bytes), `wrapped_key`, access/refresh tokens. The FP, `wrap_id`,
  `label` and HTTP status codes are fine. The passphrase is read only
  through `IConsolePrompt` with `isSecret: true` — never a command-line
  argument, environment variable or config key.
- The server is not trusted. It never receives the passphrase, KEK or
  master key; only the GCM tag decides whether the passphrase is right.
- **K1** — tunables from `IConfiguration`. **K6** — TDD: each test first,
  seen failing. **K7** — one commit, imperative subject, WHY in the
  body, **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. Do not change
  `Api`, `Services`, `Data` or `Core`.

## 🔴 1. Crypto (`SyncCrypto`)

- `UnwrapMasterKey(passphrase, salt, iterations, nonce, wrappedKey)`:
  `KEK = Rfc2898DeriveBytes.Pbkdf2(UTF-8(passphrase), salt, iterations,
  SHA256, 32)`; `master = AesGcm(KEK, 16).Decrypt(nonce,
  wrappedKey[0..32], tag = wrappedKey[32..48], no AAD)`. A tag mismatch
  means a wrong passphrase — return a distinct result the caller can
  tell apart from a malformed wrap; do not let `CryptographicException`
  escape as a crash.
- Validate before deriving: salt 16 bytes, nonce 12, `wrapped_key` 48,
  `iterations` read **from the wrap**, accepted only in
  `1 … Capture:Sync:MaxWrapIterations` (default 10 000 000 — a hostile
  server must not be able to hang the process). No lower bound: the
  vectors use 1 000 and a low count leaks nothing to the server.
- `Fingerprint(master)` = lowercase hex of
  `SHA256("customsync-fingerprint-v1" ‖ master)[0..8]` (16 hex chars).
- Zero the KEK and intermediate byte arrays after use
  (`CryptographicOperations.ZeroMemory`), best effort.

## 🔴 2. The new `--set-key` flow

Replace the 64-hex prompt (tdesktop never shows the key; keep
`ParseMasterKeyHex` — the key file on disk stays 64 hex chars):

1. The device must already be enrolled (`--enroll`). No state file →
   clear message, exit 1.
2. Get an access token by refreshing with the stored refresh token and
   **persist the rotated refresh token before using the access token**.
   Do NOT write a second copy of that logic: extract one component that
   both `CaptureSyncRunner` and the CLI use (refresh, `ParseExpiresAt`,
   save state).
3. `GET /api/v1/keys/wraps`; keep `wrap_type == "passphrase"`. None →
   "create one in tdesktop (Sync tab → archive password)", exit 1. One →
   use it. Several → list `label` and `created_at`, ask which.
4. `GET /api/v1/keys/wraps/{wrap_id}` **exactly once**. `429` → tell the
   owner the hourly limit is reached, exit 1.
5. Ask for the passphrase (secret). Wrong passphrase → ask again, **at
   most 3 attempts, all against the wrap already in memory** (re-fetching
   would burn the 5-per-hour limit and does nothing for security).
6. Print the FP and ask the owner to confirm it matches tdesktop's
   "Kalit barmoq izi (FP)". Anything but an explicit yes → nothing is
   written, exit 1.
7. If a key file already exists: same FP → say so and exit 0 without
   writing; different FP → say it will be replaced and ask again before
   writing.
8. Write with the existing `DeviceCredentials.SaveMasterKey` (atomic,
   mode `0600`). Print the path and the FP, nothing else about the key.

## 🔴 3. Running service vs `--set-key`

`--set-key` rotates the refresh token. A service already running still
holds the old one in memory, so its next refresh gets `401` and today
it stops sync for good (`_isStopped`). Fix: before stopping on a refresh
`401`, the runner re-reads the state file once; if the stored refresh
token differs from the one it used, it retries the refresh with the new
one. Only a second `401` with the current file stops sync.

## TESTS

1. `key_wrap` vectors: both cases unwrap to `master_hex`; each
   `wrong_passphrase` gives "wrong passphrase", not an exception.
2. `fingerprint` vectors: all 3 cases; and each `key_wrap` case's master
   gives that case's `fingerprint`.
3. Malformed wrap: wrong salt/nonce/`wrapped_key` length, `iterations`
   0, negative, and above `MaxWrapIterations` → rejected before PBKDF2
   runs (the test must prove PBKDF2 did not run, e.g. a huge count
   finishes instantly).
4. Full CLI flow against a fake server that answers with the **server's
   real JSON shapes** (build the bodies from the same anonymous objects
   as `DeviceEndpoints` / `KeyEndpoints`, serialised snake_case, with
   `DateTime` values): refresh → list → get → passphrase → FP confirmed
   → key file holds `master_hex`, rotated refresh token on disk.
5. Wrong passphrase twice, right on the third try → success; the wrap
   was fetched exactly once.
6. Three wrong passphrases → exit 1, no key file, wrap fetched once.
7. FP not confirmed → no key file.
8. No passphrase wrap → exit 1 with the tdesktop hint; several wraps →
   the chosen one is fetched.
9. `429` on the wrap fetch → exit 1, the rate-limit message.
10. Not enrolled → exit 1, no HTTP.
11. Existing key file: same FP → not rewritten; different FP → replaced
    only after the second confirmation.
12. Runner: refresh `401` + a newer token in the state file → retries
    and pushes; `401` with the current token → stops.
13. The refresh logic exists once: runner and CLI both go through it (a
    test that fails if either path skips persisting the rotated token).
14. Output and logs of every test above contain no passphrase, KEK,
    master hex, `wrapped_key` or token.

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, then
   **rebuild with `--no-incremental`** before the next one:
   a) swap ciphertext and tag in `wrapped_key`;
   b) hard-code `iterations = 600000` instead of reading the wrap;
   c) drop the `"customsync-fingerprint-v1"` prefix from the FP;
   d) re-fetch the wrap on every passphrase attempt;
   e) write the key file before the FP is confirmed;
   f) let a wrong passphrase throw out of the command;
   g) skip saving the rotated refresh token in the CLI path;
   h) remove the upper iterations bound;
   i) runner stops on the first `401` without re-reading the state file;
   j) print the master hex after saving.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–3 implemented; tests 1–14 pass; all 10 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 Task 6a-2 row, header test count, the new
  config key `Capture:Sync:MaxWrapIterations`, the owner's setup order
  (`--enroll` → `--set-key` → enable sync) in §8. Do not mark 6b done.

## FINAL REPORT (six short points)

1. Commit hash and files changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–j), by test name.
4. Anything done differently from this prompt, and why.
5. Any mismatch between spec §4.4.0, the vectors and the server code
   (quote file:line) — report it, do not "fix" it.
6. The exact console dialogue the owner will see (with placeholders for
   secrets), so it can go into the deploy notes.

## OUT OF SCOPE

Task 6b (pull, `setting` records, scope snapshots), recovery-code and
email-escrow wraps, creating or deleting wraps, media, Tasks 7–10,
obtaining `libtdjson`, the owner's login and enrollment, deployment.
