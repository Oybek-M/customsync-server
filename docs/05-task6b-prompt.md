Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 6b — pull the owner's `setting` records into the capture scope

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
- Starting state: HEAD `a2e1bec` or later, `dotnet build` 0 warnings,
  `dotnet test` 337/337.
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

The capture service decides what to record through three scope layers:
server `Block`/`Allow`, then the **owner's own tdesktop settings**
(whitelist, blacklist, categories, AntiDelete/AntiEdit, activity
include/exclude), then a server default that is `false` (capture
nothing). The middle layer has been an empty seam since Task 5
(`NullSyncedScopeSettingsSource`,
`NullSyncedActivityScopeSettingsSource`). tdesktop already pushes these
settings as encrypted `setting` records. This task pulls them, verifies
them, keeps the newest value per key durably, and feeds the two scope
evaluators, so the VPS captures exactly what the owner chose in
tdesktop — no more, no less.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block, §3, §4, the verification sections
   for plan 05 Tasks 5, 4b, 6a and 6a-2, and "Integratsiya auditi uchun
   yig'ilayotgan ro'yxat". Defects found there will be looked for again:
   a fake server that does not match the real one, migrations that
   silently skip statements, fail-open scope, untested wiring.
2. Spec (READ-ONLY) `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`:
   **§3.2.1** (scope keys and value formats), **§3.2.1a** (settings are
   GLOBAL in tdesktop and re-sent at every start under every account),
   §3.2 (`setting` row: `msg_id = DiscriminatorFor(key)`), §0.3
   (retention — pull filter), §0.12, §0.14, §5.4 (pull).
3. `<tdesktop>\docs\sync-protocol\test-vectors.json`: `record_id` (the
   `setting` case), `discriminator`, `hkdf`, `account_hash`,
   `peer_hash` — through `tests/CustomSync.Tests/TestVectors.cs`.
4. Server: `Api/Endpoints/SyncEndpoints.cs` (`/pull`),
   `Services/SyncService.cs` (`PullAsync`), `Core/Contracts/PullResponse.cs`,
   `tests/CustomSync.Tests/SyncEndpointsTests.cs`.
5. Capture: `Capture/ScopeSettingsSnapshot.cs`,
   `ActivityScopeSettingsSnapshot.cs`, `CaptureScopeEvaluator.cs`,
   `ActivityScopeEvaluator.cs`, `CaptureHandlerRegistration.cs` (the two
   `TryAddSingleton<ISynced...>` lines), `ScopeConfigReader.IsCanonicalPeerId`,
   `ActivityMapper.DiscriminatorFor`, `MessageCache.Initialize` (the
   `user_version` migration and the comment about `PRAGMA`),
   `Sync/*` (runner, loop, crypto, HTTP client, registration),
   `Program.cs` (registration order).

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service or any CLI mode, no
  `dotnet run`. `dotnet build` / `dotnet test` only. Capture tests fake
  HTTP with an `HttpMessageHandler`; the server-side test in §1 uses the
  existing PostgreSQL test fixture exactly as `SyncEndpointsTests` does.
- **Never log** setting values, payloads, keys, tokens, `account_id` or
  peer ids. Setting **key names**, `record_id`, `seq` and counts are fine.
- The server is not trusted: every pulled record is verified before it
  can change the scope; a record that fails any check is ignored.
- **Fail closed:** anything missing, unparseable or unverifiable leaves
  the synced layer empty (`null` snapshot), so the server default
  (capture nothing) applies. Never widen capture on doubt.
- **K1** — tunables from `IConfiguration`. **K4** — pulling the same
  records twice changes nothing. **K6** — TDD: each test first, seen
  failing. **K7** — one commit, imperative subject, WHY in the body,
  **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. The ONLY
  backend change allowed is §1.

## 🔴 1. Server: optional `kind` filter on `/api/v1/sync/pull`

Today pull has no filter, so the capture service would download every
encrypted record of every device just to find a few settings. Add an
optional `kind` query parameter (one value; absent = today's behaviour,
byte-for-byte). With it, `PullAsync` returns only rows of that kind,
still `seq > since` ascending, `next_since` = last returned `seq`,
`has_more` as today. An unknown kind → `400`. Tests in
`SyncEndpointsTests` style: the filter returns only that kind across
several pages, cursor pages do not skip or repeat rows, and the call
without `kind` is unchanged.

## 🔴 2. Pull, verify, store (`src/CustomSync.Capture/Sync/`)

- Each sync cycle: after the push step, `GET /api/v1/sync/pull?since=<cursor>&limit=<Capture:Sync:PullBatchSize, default 500>&kind=setting`;
  follow `has_more` up to `Capture:Sync:MaxPullPagesPerCycle`
  (default 20) pages. Same token handling as push (shared component).
- For every record, in this order, and skip (count + log `record_id`)
  on the first failure:
  1. `kind == "setting"`;
  2. `record_id` recomputed with `CustomSync.Core.RecordId.Compute`
     matches;
  3. payload decrypts with the content key (`SyncCrypto`);
  4. payload is `{key, value, account_id, peer_id}`, all strings;
     `peer_id == "0"`; `HMAC(peer_key, "0") == peer_hash`;
     `HMAC(account_key, account_id) == account_hash` (§0.14);
  5. `msg_id == ActivityMapper.DiscriminatorFor(key)`;
  6. `key` is one of the 11 scope keys of §3.2.1 (other setting keys
     are ignored silently — not an error).
  **Do NOT filter by `account_hash`** (§3.2.1a): accept settings from
  every account the owner's key opens.
- Merge: per key, the value with the greatest `(occurred_at, record_id)`
  wins (`record_id` compared ordinally as the tie-break). Stored in the
  cache SQLite file: table `synced_settings(key PK, value, occurred_at,
  record_id)` and table `sync_state(name PK, value)` holding the pull
  cursor. New tables come through the `user_version` migration (v2 → v3)
  inside one transaction; a v2 file upgrades in place with its data.
- **The cursor moves only in the same transaction as the page's merge.**
  A crash between them re-pulls the page (at-least-once, K4 makes it
  harmless). Skipped records still advance the cursor (they will never
  verify later either).
- A page that cannot be read at all (HTTP error, bad JSON) → nothing
  stored, cursor unchanged, the cycle backs off like push.

## 🔴 3. Snapshots for the scope evaluators

- Parse the stored values (formats in §3.2.1):
  - lists (`scope.whitelist`, `scope.blacklist`, `scope.activity_include`,
    `scope.activity_exclude`) — JSON array of strings, every entry must
    pass `ScopeConfigReader.IsCanonicalPeerId`;
  - categories — JSON object with booleans `user`, `group`, `channel`;
  - per-peer maps — JSON object, canonical peer id → boolean;
  - globals and `scope.activity_track_all_contacts` — `"true"` / `"false"`.
  One invalid entry makes the **whole key invalid** (dropping one
  blacklist entry would silently widen capture).
- **Message snapshot** (`ScopeSettingsSnapshot`) exists only when all 8
  message keys are stored and valid; otherwise `null`. **Activity
  snapshot** (`ActivityScopeSettingsSnapshot`) exists only when all 3
  activity keys are stored and valid; otherwise `null`. tdesktop re-sends
  the full set at every start (§3.2.1a), so a partial set means "not
  synced yet". This replaces the older plan note "missing activity keys
  → tdesktop defaults"; state that in `PROGRESS.md`.
- Implement `ISyncedScopeSettingsSource` and
  `ISyncedActivityScopeSettingsSource` backed by the store: the snapshot
  is rebuilt after each merge that changed a scope key and published by
  an atomic reference swap (readers never see a half-built snapshot).
  On startup the snapshot is built from the stored rows, before any
  pull, so a restart does not fall back to "capture nothing" while
  offline.
- Registration: `AddCaptureHandlers` registers the `Null...` sources
  with `TryAdd` and runs first in `Program.cs`, so the sync registration
  must **replace** them. Test through the real registrations in the real
  order; resolve only what production resolves.

## TESTS

Fake server bodies are built from the real contract types
(`PullResponse`, `StoredRecord`) serialised snake_case, and setting
payloads are encrypted with `SyncCrypto` and the vectors' master key.

1. Server: `kind=setting` returns only settings, paged without gaps or
   repeats; no `kind` → identical to before; unknown kind → `400`.
2. A `setting` record built from the vectors (`scope.whitelist`,
   account `111222333`, `occurred_at` 1787000007) has the vectors'
   `record_id`, verifies and is stored.
3. Each verification step (1–5 of §2) rejects its tampered record, the
   scope stays as it was, the cursor still advances.
4. Unknown setting key → ignored, not counted as an error.
5. Settings from two different `account_hash` values → both accepted;
   the newest `occurred_at` wins; equal `occurred_at` → greater
   `record_id` wins; an older record pulled later does not overwrite.
6. Pulling the same page twice → no change (K4).
7. All 8 message keys → snapshot equals the values; remove or corrupt
   one → `null`; one non-canonical blacklist entry → `null`.
8. All 3 activity keys → activity snapshot; missing one → `null`.
9. End-to-end: real registrations, fake server serves the 11 keys →
   `CaptureScopeEvaluator` / `ActivityScopeEvaluator` decisions follow
   the owner's settings; before the pull they fall back to the server
   default.
10. Restart: new service provider over the same SQLite file, server
    unreachable → the snapshot is already there.
11. Cursor: a crash (exception) after reading a page but before commit
    leaves the cursor unchanged and nothing half-stored; the next cycle
    re-pulls it.
12. `has_more` paging stops at `MaxPullPagesPerCycle`.
13. HTTP `5xx` / bad JSON on pull → cursor unchanged, backoff.
14. Migration: a v2 file (as created by the current code) upgrades to
    v3 with every existing table and row intact.
15. Logs contain no setting values, payloads, keys, tokens or ids.

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, then
   **rebuild with `--no-incremental`** before the next one:
   a) keep only settings whose `account_hash` is this account's;
   b) skip the `record_id` recomputation;
   c) skip the `msg_id == DiscriminatorFor(key)` check;
   d) last-pulled wins instead of greatest `(occurred_at, record_id)`;
   e) move the cursor before the merge commits;
   f) drop an invalid list entry instead of invalidating the key;
   g) build a message snapshot from a partial key set;
   h) keep the `Null...` sources registered (no replace);
   i) build the snapshot only after the first pull (not at startup);
   j) ignore the `kind` filter on the server;
   k) log a setting value.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–3 implemented; tests 1–15 pass; all 11 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 Task 6b row, header test count, new config keys
  (`Capture:Sync:PullBatchSize`, `Capture:Sync:MaxPullPagesPerCycle`),
  the `kind` pull filter, and the replaced "missing activity keys"
  note. Do not touch the deploy-audit section except to add findings.

## FINAL REPORT (six short points)

1. Commit hash and files changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–k), by test name.
4. Anything done differently from this prompt, and why.
5. Any mismatch between spec §3.2.1/§3.2.1a, the vectors and the code
   (quote file:line) — report it, do not "fix" it.
6. Any way a hostile server could still change the scope that the
   checks in §2 do not stop. Think about it concretely; report, do not
   fix.

## OUT OF SCOPE

Pulling or merging any kind other than `setting`, tombstones, media,
WebSocket notifications, changing what tdesktop sends, Tasks 7–10,
obtaining `libtdjson`, the owner's login, deployment.
