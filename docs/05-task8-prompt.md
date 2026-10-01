Execute the task described below. It is an implementation job in an
existing .NET 8 repository — not a document to review, summarise or
score. The only output that counts is committed source code plus tests.

# Plan 05 Task 8 — opt-in media capture for deleted messages

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
- Starting state: HEAD `d02811a` or later, `dotnet build` 0 warnings,
  `dotnet test` 376/376.
- **Stale test vectors (seen on the laptop, `DESKTOP-L2J53IK`).** If
  `<tdesktop>\docs\sync-protocol\test-vectors.json` has no `key_wrap`,
  `fingerprint` or `discriminator` section, the `<tdesktop>` checkout is
  behind its remote and ~17 tests fail with a message saying so. Do NOT
  pull `<tdesktop>`. Instead:
  `git -C <tdesktop> show origin/Oybek:docs/sync-protocol/test-vectors.json > <file outside the repo>`
  and set `CUSTOMSYNC_TEST_VECTORS` to that file for every `dotnet test`.
  Read the spec the same way (`git -C <tdesktop> show origin/Oybek:<path>`)
  if your checkout lacks a section named below. Never commit these files.
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

Telegram's delete update carries only ids. To keep a deleted photo,
voice note or document, the service must have downloaded the file
**before** the deletion — and downloading every file in every chat would
fill the disk within days. So media capture is **off by default** and,
when enabled, limited to an explicit list of chats and a size cap.

The server side of the media protocol has never been used by any client
(tdesktop does not sync media yet), and reading it for this task showed
it cannot work as is:

- `GET /api/v1/media/{hash}` returns only the bytes and the pull
  response returns only hashes — **no client can ever learn the nonce,
  so no downloaded blob can be decrypted**;
- `PUT` without an `X-Nonce` header silently stores twelve zero bytes as
  the nonce, and invalid base64 there is a 500;
- `{hash}` is not validated at all, and the storage path is
  `Path.Combine(root, hash[..2], hash)` — a hash like `..x` writes a file
  **outside** the media root;
- a pushed record may reference a hash the server does not have (no
  foreign key, no check), although spec §5.3 promises a per-record
  `media hash missing` error.

This task fixes that contract and adds the capture side on top of it.

Two deliberate scope decisions (do not "improve" them):

1. **Upload late, not early.** A file is uploaded only when a `deleted`
   record is about to reference it. A blob that no record references is
   invisible to every other device and is never swept by the server's
   orphan cleanup (`orphaned_at` stays NULL), so uploading every
   downloaded file would just burn quota.
2. **No `media_index` records yet.** Its payload fields (`rel_path`,
   `layer`, `kind`, `status` values) are not agreed with tdesktop, and
   records live on the server forever. The link used here is the
   generic `media` array of the record (spec §3 — its own example is a
   `deleted` record with `media`).

Lesson from every previous plan 05 task: protection that exists in the
code but is not wired in, or that has a second door around it, passes
its own tests. Here: the size cap, the chat list, the scope check, the
upload-before-push order and the nonce consistency must each be proved
through the production path.

## HOW TO REPORT

Short and factual. Do not claim anything you did not run. If a
requirement turns out to be impossible or wrong, stop and say so in the
report instead of silently doing something else. Do not state TDLib
behaviour you did not confirm from TDLib's documentation or source —
say "unconfirmed" instead.

## STEP 0 — read before writing code

1. `PROGRESS.md`: the hand-off block, §3, §4, the verification sections
   for plan 05 Tasks 4a, 6a, 6b and **7**, and "Integratsiya auditi uchun
   yig'ilayotgan ro'yxat". Defects found there will be looked for again:
   a fake server that does not match the real one, unwired protection, a
   second door around a gate, tests that build their own
   `ServiceCollection`, logs that leak private data.
2. Plan (READ-ONLY) `<tdesktop>\docs\superpowers\plans\2026-07-29-multi-device-sync-05-capture-service.md`:
   **Task 8**, the "REVIZIYA 2026-08-25" block and constraint 2 at the top.
3. Spec (READ-ONLY) `<tdesktop>\docs\superpowers\specs\2026-07-29-multi-device-sync-backend-design.md`:
   §0.4, **§0.5** (sha256 over the PLAINTEXT, before encryption), §0.8,
   §0.9 (507 → keep in the outbox), §3 (the record example with
   `media`), §4.1 (`customsync-media-v1`), §4.2, §5.3 (media first, then
   the record), the media part of §6/§8.
4. `test-vectors.json`: `hkdf` (it has a `customsync-media-v1` key) and
   `aes_gcm` — through `tests/CustomSync.Tests/TestVectors.cs`.
5. Server: `Api/Endpoints/MediaEndpoints.cs`, `Services/MediaService.cs`,
   `Services/SyncService.cs` (`record_media` insert), `Core/Contracts/SyncRecord.cs`
   (`MediaRef`), `Services/Storage/PurgeService.cs` (`SweepOrphanedMediaAsync`),
   the existing media endpoint tests.
6. Capture: `Capture/CaptureUpdateHandler.cs` (`updateNewMessage`,
   `ExtractTextAndMedia`), `Capture/MessageCache.cs` (`user_version` is 3;
   the migration pattern and its `PRAGMA` comment), `Capture/OutboxRow.cs`,
   `Capture/TdIdMapper.cs`, `Capture/CaptureScopeEvaluator.cs`,
   `Capture/ScopeConfigReader.cs` (`IsCanonicalPeerId`, how
   `Capture:Scope:Allow` is read), `Sync/SyncCrypto.cs` (`BuildRecord`, key
   derivation), `Sync/CaptureSyncRunner.cs`, `Sync/CaptureSyncHttpClient.cs`,
   `Tdlib/TdRequestPolicy.cs`, `Worker.cs`, `Program.cs`,
   `Preflight/CapturePreflight.cs`.

## NON-NEGOTIABLE RULES

- Do NOT build TDLib, do NOT run the service or any CLI mode, no
  `dotnet run`. `dotnet build` / `dotnet test` only. Capture tests fake
  TDLib with a fake `ITdTransport` under the real `TdClient`, and fake
  HTTP with an `HttpMessageHandler` whose responses follow the **real**
  server endpoints (status codes and headers from §1). Server tests use
  the existing PostgreSQL fixture.
- **Never log** file names, local paths, message text, keys, nonces,
  tokens or peer ids. Hash prefixes (first 8 hex), sizes, counts and
  statuses are fine.
- **sha256 is computed over the plaintext file, before encryption**
  (spec §0.5). Media key = `HKDF-SHA256(master, "customsync-media-v1")`,
  AES-256-GCM, fresh random 12-byte nonce per upload, request body =
  ciphertext ‖ 16-byte tag, nonce in `X-Nonce` (base64).
- **Never widen capture on doubt:** media is downloaded only when every
  condition in §2 holds. Anything missing, unknown or unparseable means
  "do not download".
- **Text is never lost because of media.** If the file is missing,
  unreadable, changed (hash mismatch) or rejected as too large, the
  `deleted` record is pushed **without** media.
- **K1** — tunables from `IConfiguration` on the capture side (it never
  reads `server_settings`). **K4** — a crash between upload and push,
  then a retry, must not upload twice or push twice. **K6** — TDD: each
  test first, seen failing. **K7** — one commit, imperative subject, WHY
  in the body, **no `Co-Authored-By` trailer**.
- Do not write to `<tdesktop>` or `docs/sync-protocol/`. The ONLY backend
  changes allowed are those in §1.

## 🔴 1. Server: make the media contract usable and safe

In `Api/Endpoints/MediaEndpoints.cs` / `Services/MediaService.cs` /
`Services/SyncService.cs`:

1. `{hash}` on `HEAD`, `PUT` and `GET` must match `^[0-9a-f]{64}$`
   (lowercase hex SHA-256); anything else → `400`, nothing touches the
   disk or the database. Also make `MediaService` refuse a non-conforming
   hash itself, so no future caller can route around the endpoint check.
2. `PUT` requires `X-Nonce`: valid base64 decoding to exactly 12 bytes.
   Missing, invalid base64 or a different length → `400` (never a stored
   zero nonce, never a 500).
3. `HEAD` and `GET` of an existing blob return the stored nonce in an
   `X-Nonce` response header (base64). This is how a client decrypts a
   downloaded blob and how an uploader that finds the blob already present
   learns the nonce that matches it.
4. Push (`SyncService`): a record whose `media[]` has a hash that is not
   in `media_blobs`, a malformed hash, or a nonce that is not 12 bytes →
   that record gets `status: "error"`, `message: "media_hash_missing"`
   (or `"media_ref_invalid"` for the format cases); the record is NOT
   stored, and the other records in the same batch are unaffected.

Server tests for each of the four (existing fixture, real HTTP pipeline
as in the existing media/sync endpoint tests).

## 🔴 2. Capture: what gets downloaded (`src/CustomSync.Capture/Media/`)

Configuration (all optional; defaults shown):

| Key | Default | Meaning |
|---|---|---|
| `Capture:Media:Enabled` | `false` | master switch |
| `Capture:Media:PeerIds` | empty | chats whose media may be downloaded — **same format and reader as `Capture:Scope:Allow`** |
| `Capture:Media:MaxBytes` | `10485760` | per-file cap; must be `1..26214400` |
| `Capture:Media:DownloadTimeoutSeconds` | `120` | one `downloadFile` call |
| `Capture:Media:MaxAttempts` | `5` | then the file is given up |

`26214400` (25 MiB) is a hard ceiling because the API runs with Kestrel's
default request-body limit (~28.6 MB), below `media.max_upload_bytes`.
Preflight rejects a non-canonical peer id in `PeerIds`, a `MaxBytes`
outside the range, and non-positive timeouts/attempts — with messages
that never print the values of other settings.

A message from `updateNewMessage` is queued for download **only if all**
hold:

1. `Enabled` is true;
2. its tdesktop peer id is in `PeerIds`;
3. the capture scope says AntiDelete applies to that chat
   (`ShouldAntiDelete` — the file is only useful if the deletion will be
   recorded);
4. the content type is one of `messagePhoto`, `messageVideo`,
   `messageDocument`, `messageAudio`, `messageVoiceNote`,
   `messageVideoNote`, `messageAnimation` (stickers and everything else:
   no);
5. a known size is `> 0` and `≤ MaxBytes` (for a photo: pick the largest
   size that fits; none fits → no).

Queue it in a new SQLite table in the cache database (migration to
`user_version = 4`, following the existing pattern, every existing table
and row intact): one row per `(peer_id, msg_id)` in tdesktop ids (the
ids the outbox uses — `TdIdMapper`), plus the TDLib `chat_id` /
`message_id`, content type, status (`pending`, `downloaded`, `failed`,
`uploaded`, `skipped`), attempts, next attempt time, and — once
downloaded — local path, sha256, size. The update handler only inserts
the row; it never blocks on a download.

## 🔴 3. Capture: downloading

A background downloader:

- starts **only after** `Worker` has passed authorization **and** the
  Task 7 invisibility step (wire it there; resolve with
  `GetRequiredService`, no `GetService` + `?.`);
- takes due `pending` rows one at a time, gets a fresh file object with
  `getMessage` (TDLib file ids may not survive a restart — confirm or
  report "unconfirmed"), then calls `downloadFile` (whole file,
  `synchronous: true`, timeout from config);
- on success checks the file is complete and exists, computes sha256 and
  size from the file on disk, stores them with the local path, status
  `downloaded`. If the actual size exceeds `MaxBytes`, discard it
  (status `skipped`);
- on failure: `attempts + 1`, backoff, `failed` after `MaxAttempts`; a
  message that no longer exists → `failed` immediately;
- add `downloadFile` to the `TdRequestPolicy` allow-list (the one place
  that decides — a privacy decision, so the comment next to the list
  says why it is safe: downloading a file does not mark it viewed or
  listened; `openMessageContent` / `openStory` stay forbidden), allowed
  only with an integer `file_id > 0`. Update the Task 7 tests that pin
  the allow-list.

Local files stay where TDLib put them; cleaning them up is Task 9.

## 🔴 4. Capture: upload and link when the message is deleted

In the push cycle (`CaptureSyncRunner` / `SyncCrypto.BuildRecord`), for a
`deleted` outbox row whose `(peer_id, msg_id)` has a `downloaded` media
row:

1. read the file; if it is missing, unreadable or its sha256 differs from
   the stored one → push the record **without** media, mark the media
   row `failed`;
2. `HEAD /api/v1/media/{sha256}`:
   - `200` → use the `X-Nonce` from the response for the record
     (no upload); a `200` without a valid `X-Nonce` is an error — keep
     the row for retry;
   - `404` → encrypt with the media key and a fresh nonce, `PUT` with
     `X-Nonce`;
3. `PUT` result: `200` → continue; `507` → **keep the outbox row** for
   retry with backoff (spec §0.9) and do not push it in this cycle;
   `413` → push the record without media, mark the media row `skipped`;
   network error / `5xx` → keep for retry;
4. only then push the record with
   `media: [{ hash: sha256, size: <plaintext size>, nonce: <the blob's nonce> }]`;
5. after the push result says `created` / `duplicate` / `superseded`,
   mark the media row `uploaded`.

The other rows of the batch are not held back by one row's media
problem. A restart at any point repeats nothing that already
succeeded (K4).

## TESTS

1. Server: `HEAD`/`PUT`/`GET` with `..x`, uppercase hex, 63 chars → 400,
   no file created anywhere, no row.
2. Server: `PUT` without `X-Nonce`, with bad base64, with an 11-byte
   nonce → 400; with a valid nonce → stored, and `HEAD`/`GET` return the
   same nonce in `X-Nonce`.
3. Server: push of a record referencing an unknown hash → per-record
   `media_hash_missing`, record not stored, the other record in the same
   batch `created`.
4. Media key from the vectors' master key equals
   `hkdf.derived["customsync-media-v1"]`; the encrypt function matches
   the `aes_gcm` vector.
5. Policy: each of conditions 1–5 in §2, flipped alone, means no queue
   row (drive the real handler with real registrations and a fake
   transport). Default configuration → no row, transport never sees
   `downloadFile`.
6. Photo: the largest size that fits is chosen; none fits → no row.
7. Downloader: success → `downloaded` with the right sha256/size;
   failures → attempts/backoff → `failed`; deleted message → `failed`;
   oversized actual file → `skipped`.
8. The downloader does not start before the invisibility step succeeds
   (Worker-level test, as in Task 7's Test12/13), and does not start at
   all when that step fails.
9. End-to-end: message with a photo in an allowed chat → downloaded →
   deleted → `HEAD 404` → `PUT` whose body, decrypted with the media key
   and the sent `X-Nonce`, equals the original file and whose sha256 is
   the URL hash → the pushed record's `media` has that hash, the
   plaintext size and that nonce; the `PUT` happened before the push.
10. `HEAD 200` with `X-Nonce` → no `PUT`, and the record's nonce is the
    server's, not a fresh one.
11. `507` → the outbox row stays, nothing pushed for it; the other rows
    of the batch are pushed; a later cycle with `200` pushes it.
12. Missing file, changed file (hash mismatch), `413` → record pushed
    without media, media row `failed`/`skipped`.
13. Crash after `PUT`, before push (exception thrown there) → next cycle:
    `HEAD 200`, no second `PUT`, one push (K4).
14. Migration: a v3 cache file (as the current code creates it) upgrades
    to v4 with every existing table and row intact.
15. Allow-list: `downloadFile` with `file_id` 0, negative, string, or
    missing → rejected; Task 7's forbidden list still rejected.
16. Logs contain no file name, local path, text, key, nonce or peer id
    (use marker strings in the file name and content and assert they are
    absent).
17. Preflight: each invalid `Capture:Media:*` value → a clear error;
    valid defaults → no error.

## HOW TO VERIFY

1. Every test written first and seen failing.
2. `dotnet build` 0 warnings; full `dotnet test` **three times** — report
   all three summary lines.
3. Deliberate breaks — do each, confirm a test fails, revert, then
   **rebuild with `--no-incremental`** before the next one:
   a) server accepts any `{hash}`;
   b) server stores a zero nonce when `X-Nonce` is missing;
   c) `HEAD` does not return `X-Nonce`;
   d) push accepts an unknown media hash;
   e) ignore `PeerIds` (download for every chat);
   f) skip the `ShouldAntiDelete` condition;
   g) compute sha256 over the ciphertext;
   h) push the record before uploading the blob;
   i) on `507`, push the record without media;
   j) on a missing local file, keep the record forever instead of pushing
      it without media;
   k) on `HEAD 200`, put a fresh nonce in the record;
   l) start the downloader before the invisibility step;
   m) log the file name.
   If a break does NOT fail a test, fix the test.

## DEFINITION OF DONE

- Sections 1–4 implemented; tests 1–17 pass; all 13 breaks caught.
- One commit, pushed to `origin Oybek`, tree clean.
- `PROGRESS.md`: plan 05 Task 8 row (commit hash, not `HEAD`), header
  test count, the new `Capture:Media:*` keys, the four server contract
  changes, the two scope decisions from "WHY", and the Task 9 hand-over
  (local media files and media rows are never cleaned up yet). Do **not**
  mark the task as verified — the TeamLead does that after an independent
  check. Do not touch the deploy-audit section except to add findings.

## FINAL REPORT (six short points)

1. Commit hash and files changed.
2. Test count before/after; the three `dotnet test` summary lines.
3. Which deliberate break failed which test (a–m), by test name.
4. Anything done differently from this prompt, and why.
5. TDLib facts this code relies on (`downloadFile` reply shape and
   `synchronous`, file id lifetime across restarts, the JSON shape of each
   of the seven content types' file objects) — for each, the source you
   confirmed it from, or "unconfirmed".
6. Any way the server, a compromised device or a malformed TDLib update
   could still make the service download more than intended, push wrong
   media, or lose a deleted message's text. Report, do not fix.

## OUT OF SCOPE

`media_index` records, eager upload of non-deleted media, media for
`edited` records (old version of a replaced photo), avatars and stories
(negative `msg_id`), downloading media on other devices, local file and
media-row cleanup (Task 9), the Kestrel body limit vs
`media.max_upload_bytes` mismatch (record it, do not change it), Tasks
9–10, obtaining `libtdjson`, the owner's login, deployment.
