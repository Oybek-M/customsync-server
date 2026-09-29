Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 6a — capture sync client: encrypt and push the outbox

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
- Starting state: HEAD `7a0f051` or later, `dotnet build` 0 warnings,
  `dotnet test` 288/288.
- **PC only (hostname `DESKTOP-5CAUS66`):** a stale Visual Studio NuGet
  config points at a deleted `E:\Application's datas\...` folder, so a
  plain `dotnet build` / `dotnet test` fails with `MSB4018` or `NU1301`.
  Do NOT edit anything under `C:\Program Files (x86)` or the user NuGet
  config. Instead:
  1. write a temporary `nuget.config` **outside the repo** containing
     `<packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>`
     and `<fallbackPackageFolders><clear/></fallbackPackageFolders>`;
  2. `dotnet restore --configfile <that file>` (again only if you add a
     package — try hard not to: everything needed is in the BCL);
  3. from then on **always** `dotnet build --no-restore` and
     `dotnet test --no-build`; for deliberate breaks rebuild with
     `dotnet build --no-restore --no-incremental`.
  Never commit that file. On the laptop none of this is needed.

## WHY THIS TASK MATTERS

The capture service (plan 05) already records `deleted`, `edited` and
`activity` rows into its local SQLite `capture_outbox`. Nothing leaves
the VPS yet, so the service is useless to the owner's other devices.
This task makes the service an **ordinary sync device**: it enrols like
tdesktop, encrypts every outbox row end-to-end and pushes it through
`/api/v1/sync/push`. The backend gets no special code path.

Task 6 is split. **6a (this prompt): crypto, device credentials, push.**
6b (next prompt): pull, `setting` records → scope snapshots. Do not
start 6b.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md` §2, §3, §4 and the verification sections for plan 05
   Tasks 4a, 5, 4b and 4c. The same defects will be looked for again:
   fail-open defaults, features not wired through the production
   registration, untested atomicity, data dropped on an error path,
   stale incremental builds during break testing.
2. Spec (READ-ONLY) `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`:
   §0.3, §0.5, §0.12, §0.13 (activity), §0.14, §3 (record shape — note
   `"payload": "<base64 AES-256-GCM ciphertext+tag>"`), §3.1, §3.2,
   §4.1, §4.2, §4.7, §5.1, §5.3, §5.7, §8.4 (backoff).
3. `<tdesktop>\docs\sync-protocol\README.md` and `test-vectors.json`
   (sections `hkdf`, `account_hash`, `peer_hash`, `record_id`,
   `aes_gcm`). The tests read the file through the existing
   `tests/CustomSync.Tests/TestVectors.cs` helper — do not copy it.
4. Server side of the contract (read, do not change):
   `src/CustomSync.Core/CryptoPrimitives.cs`, `RecordId.cs`,
   `Contracts/SyncRecord.cs`, `PushOutcome.cs`;
   `src/CustomSync.Api/Endpoints/SyncEndpoints.cs`, `DeviceEndpoints.cs`,
   `Program.cs` (HTTP JSON is **snake_case**);
   `src/CustomSync.Services/SyncService.cs` (`PushAsync` validation),
   `DeviceService.cs` (`RefreshAsync` **rotates** the refresh token).
5. Capture code: `Capture/MessageCache.cs` (`capture_outbox`,
   `PRAGMA user_version` migration), `PayloadBuilder.cs`,
   `Tdlib/IConsolePrompt.cs`, `Program.cs` (`--login` mode),
   `Preflight/CapturePreflight.cs`, `CaptureCacheStartup.cs`.

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service, `--login`, `--set-key` or
  `--enroll`, no `dotnet run`. `dotnet build` / `dotnet test` only. No
  test may need `libtdjson`, PostgreSQL, a running server or network —
  HTTP is faked with an `HttpMessageHandler`.
- **Never log** message text, payloads, the master key or anything
  derived from it, access/refresh tokens, enrollment codes, `account_id`
  or `peer_id`. `record_id`, kind, counts and HTTP status codes are fine.
- **Local-first:** with sync disabled, misconfigured, unauthorised or the
  server down, capture keeps working and the outbox keeps every row. An
  outbox row is deleted **only** after the server answered `created`,
  `duplicate` or `superseded` for its `record_id`.
- The server is not trusted: plaintext never leaves the process; the
  master key never leaves the VPS.
- **K1** — tunables from `IConfiguration`. **K4** — pushing twice
  creates nothing new. **K6** — TDD: each test first, seen failing.
  **K7** — one commit, imperative subject, WHY in the body, **no
  `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. Do not change
  `Api`, `Services`, `Data` or `Core` (read and reference only). Do not
  add payload fields.

## 🔴 1. Crypto and record building (`src/CustomSync.Capture/Sync/`)

- Keys from the 32-byte master key via `CryptoPrimitives.DeriveKey`:
  `customsync-content-v1` (payload AES key), `customsync-peer-v1`,
  `customsync-account-v1`.
- Encryption: AES-256-GCM (`System.Security.Cryptography.AesGcm`),
  **fresh random 12-byte nonce per record**, no associated data,
  16-byte tag. Wire `payload` = **ciphertext followed by tag**; `nonce`
  is its own field. (The vectors keep the tag separate — join them.)
- A `capture_outbox` row becomes a `CustomSync.Core.Contracts.SyncRecord`:
  - `account_hash` = `ComputeAccountHash(account_key, account_id)`,
    **except `kind == "activity"` → `""`** (§0.12/§0.13);
  - `peer_hash` = `ComputePeerHash(peer_key, peer_id)` for every kind
    (the account-less formula — see the `record_id` vectors: `deleted`
    and `activity` share one `peer_hash`);
  - `record_id` = `CustomSync.Core.RecordId.Compute(...)` — never a
    re-implementation;
  - `msg_id`, `occurred_at`, `observed_at` from the row; `device_id` =
    this service's enrolled id;
  - plaintext = the UTF-8 bytes of `payload_json` exactly as stored.
- Before building, check the row against §0.14: `payload_json` must
  parse and its `account_id` / `peer_id` must equal the row's columns.
  A mismatch is a local bug: keep the row, count it, log its outbox id
  and never push it.

## 🔴 2. Master key and device credentials

- **Master key:** new interactive mode `--set-key` (same pattern as
  `--login`, through `IConsolePrompt`, input not echoed). Accepts exactly
  64 hex characters (32 bytes); anything else is rejected with a message
  that does not echo the input. Written to `Capture:Sync:MasterKeyPath`.
- **Enrollment:** new interactive mode `--enroll`: asks for the one-time
  code (never a command-line argument — it would land in shell history)
  and a device name (default `capture-vps`), calls
  `POST /api/v1/devices/enroll` with `platform = "service"`, stores
  `device_id` and `refresh_token` in `Capture:Sync:StatePath`.
- Both files: written atomically (temp file in the same directory, then
  replace). On Linux created with mode `0600`; **on load, a file whose
  mode is wider than `0600` is refused** (sync stays off, capture runs,
  one error logged). Skip only the mode check on Windows, and let the
  test say so.
- Access token: memory only. Refresh before `expires_at − 60 s` or once
  after a `401`. `RefreshAsync` rotates the refresh token, so the new one
  is persisted **before** the new access token is used; the remaining
  crash window (server rotated, file not yet written → re-enrol) is
  documented in `PROGRESS.md`, not hidden. A refresh answered `401`
  means revoked/invalid: sync stops, the outbox keeps everything, one
  error is logged.

## 🔴 3. Pushing the outbox

- Loop inside the existing host (wired through a registration extension
  like `AddCaptureHandlers`; Worker only starts it), interval
  `Capture:Sync:IntervalSeconds` (default 30), `TimeProvider` only.
- Sync runs only when `Capture:Sync:Enabled` is `true` **and** the key
  and state files load. Default `false`. Preflight: when enabled,
  `Capture:Sync:ServerUrl` must be an absolute `https` URL (`http` only
  for `localhost` / `127.0.0.1`), and a bad `Enabled` value is an error,
  not a silent `false`.
- One cycle: take eligible rows oldest first, up to
  `Capture:Sync:PushBatchSize` (default 500) and about 5 MB of JSON,
  build records, `POST /api/v1/sync/push` (`{"records":[...]}`,
  snake_case, byte arrays as base64), then per result **by `record_id`**:
  - `created` / `duplicate` / `superseded` → delete that outbox row **by
    its `id`**, never by the unique key: `INSERT OR REPLACE` gives a
    replaced row a new `id`, and a newer version written during the
    request must survive;
  - `error` → keep the row, store the server message, increase its
    attempt count and set `next_retry_at` (1 s, 2 s, 4 s … max 300 s);
  - a record missing from the response counts as `error`.
- Retry state lives in SQLite (new `capture_outbox` columns, added by the
  existing `user_version` migration so an old DB file upgrades in place).
  A row that keeps failing must **not** block the rows behind it.
- HTTP-level: `400 batch_too_large` → halve the batch and retry;
  `401` → one refresh then retry once; network error / `5xx` → whole
  cycle backs off (same 1 s … 300 s curve), rows untouched.
- Counters: pushed, duplicate, error, poisoned (§0.14 mismatch).

## TESTS

1. HKDF: the three derived keys equal `hkdf` vectors.
2. `account_hash` and `peer_hash` equal the vectors.
3. AES-GCM: encrypting a vector's plaintext with its nonce yields
   `ciphertext‖tag`; decrypting the joined bytes returns the plaintext;
   a flipped tag bit fails.
4. Two encryptions of the same row use different nonces.
5. Outbox row → `SyncRecord`: `edited` 390234 @ 1787000010 and @
   1787000020, `deleted`, and `activity` (`account_hash ""`) — each
   `record_id` equals `test-vectors.json` when built with the vectors'
   master key and account/peer ids.
6. §0.14 mismatch row: never sent, kept, counted.
7. Contract: the captured request body deserialises into
   `CustomSync.Api.Endpoints.SyncEndpoints.PushRequest` with
   `JsonNamingPolicy.SnakeCaseLower`; `RecordId.Compute` over its fields
   matches; decrypting `payload` returns `payload_json` byte-for-byte.
8. Results `created`, `duplicate`, `superseded`, `error` in one batch →
   exactly the first three rows deleted; the error row keeps its data
   and gets `next_retry_at`.
9. A row replaced (`INSERT OR REPLACE`) while the push is in flight
   survives the success of the old version.
10. A failing row does not block: next cycle sends the other rows, the
    failing one only after its `next_retry_at`.
11. Network error and `5xx` → nothing deleted, cycle backs off.
12. `400 batch_too_large` → batch halved, all rows eventually pushed.
13. `401` → one refresh; the rotated refresh token is on disk before the
    retried push; refresh `401` → sync stops, outbox intact.
14. Key/state files: bad hex rejected without echo; atomic write; mode
    `0600` enforced on Linux (skipped with a reason on Windows); a
    missing file → sync off, capture handler still records.
15. Migration: a DB created by the old schema (no retry columns) opens,
    keeps its rows and pushes them.
16. Production wiring: real registration from config, fake
    `HttpMessageHandler` via DI, a captured row reaches the fake server.
17. `Enabled` absent → no HTTP at all; preflight rejects bad `Enabled`
    and a non-https URL.
18. Logs (all tests above) contain no payload text, key, token, code,
    `account_id` or `peer_id`.

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, then
   **rebuild with `--no-incremental`** before the next one:
   a) put the tag before the ciphertext;
   b) give `activity` a real `account_hash`;
   c) hash `account_id ‖ 0x00 ‖ peer_id` for `peer_hash`;
   d) reuse one nonce for every record;
   e) delete pushed rows by unique key instead of `id`;
   f) delete a row whose result was `error` or missing;
   g) keep the old refresh token after a refresh;
   h) retry failing rows without `next_retry_at` (they block the queue);
   i) accept a key file with mode `0644`;
   j) default `Capture:Sync:Enabled` to `true`;
   k) call the push loop only from a `GetRequiredService` line in
      `Worker` (not through the registration).
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–3 implemented; tests 1–18 pass; all 11 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 Task 6a row, header test count, new config keys
  and the refresh-rotation crash window in §8. Do not mark 6b done.

## FINAL REPORT (six short points)

1. Commit hash and files changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–k), by test name.
4. Anything done differently from this prompt, and why.
5. Any mismatch you found between the spec, the vectors and the server
   code (quote file:line) — do not "fix" it, report it.
6. What 6b (pull + `setting` → scope snapshots) will need from the code
   you wrote.

## OUT OF SCOPE

Task 6b (pull, cursor, retention filter, `setting` records, scope
snapshots), media upload and `media_index`, tombstones, WebSocket,
key wraps / password unlock, Tasks 7–10, obtaining `libtdjson`, the
owner's login and enrollment, deployment.
