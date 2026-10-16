# Plan 03 · Task 6 — prompt for the delegate (Gemini)

> TeamLead (customsync-server), 2026-10-16. Plan source (single copy, read-only):
> `C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-03-web-controller.md`
> — read "Task 6". **Owner decision (2026-10-16) overrides the plan:** the local
> IndexedDB store holds records **encrypted, exactly as the server sent them**. The plan's
> "decrypted index in IndexedDB" would leave the deleted-messages archive readable in the
> browser profile while the vault is locked. Decryption happens only in worker memory.

---

You are implementing **Task 6 (local encrypted store + incremental sync + in-memory
decrypted index)** of the CustomSync web panel in
`C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server` (branch
`Oybek`). Tasks 1–5 are done: API client, control plane, crypto worker (`handleMessage`,
`openGcm`, keys only in worker), vault unlock/auto-lock. Reuse them.

## Backend contract (snake_case, verified in `src/CustomSync.Services/SyncService.cs`)

`GET /api/v1/sync/pull?since=<seq>&limit=<n>` →
`{records: [{seq, record_id, kind, account_hash, peer_hash, msg_id, occurred_at,
observed_at, device_id, nonce, payload, target_record_id, media_hashes}], next_since,
has_more}`.
- `nonce`, `payload`: base64 (`payload` = ciphertext ‖ 16-byte tag, AAD empty, content key).
- `occurred_at`, `observed_at`: unix **seconds**. `msg_id` may be negative.
- `limit` is clamped by the server (`sync.pull_batch_size`); always follow `next_since` /
  `has_more`, never assume your limit was honoured.
- Kinds: `deleted`, `edited`, `activity`, `media_index`, `setting`, `ghost_read`,
  `tombstone`. A `tombstone` record removes the record named by its `target_record_id`
  (and must not itself be shown).

## Design (follow it)

1. **IndexedDB (`web/src/db/schema.ts`, name `customsync`, version 1)**
   - `records` store, key `record_id`: the server record **as received** (metadata +
     base64 `nonce`/`payload`). No plaintext, ever. Indexes: `bySeq` [`seq`],
     `byTime` [`occurred_at`, `seq`], `byPeerTime` [`peer_hash`, `occurred_at`, `seq`],
     `byKindTime` [`kind`, `occurred_at`, `seq`].
   - `meta` store: `cursor` (last fully written `seq`), `deviceId` the cache belongs to.
   - If the enrolled device changes (or on enroll/logout), wipe the database.
2. **Sync (`web/src/db/sync.ts`)** — needs **no key**, so it runs while locked too:
   loop pull → write the batch in **one IDB transaction** (put by `record_id`, apply
   tombstones: delete target + do not store the tombstone as a visible record) → only after
   that transaction completes, set `cursor = next_since` in the same or a following
   transaction. An interrupted sync re-fetches that batch; dedup by `record_id` makes it
   harmless. Abortable (`AbortSignal`), reports progress `{fetched, cursor, hasMore}`,
   triggered on app start (after enroll) and by a "Yangilash" button; one run at a time.
3. **In-memory decrypted index (in the worker)** — new worker requests:
   - `indexBegin` → clears the in-memory index;
   - `indexAdd {records}` → decrypts each with the content key (`openGcm`), keeps
     `{record_id, kind, peer_hash, msg_id, occurred_at, seq, json}` in a Map plus sorted
     arrays for `byTime`; corrupt records are counted, not thrown; tombstoned ids removed;
   - `indexQuery {kind?, peer_hash?, before?: [occurred_at, seq], limit}` → keyset page,
     newest first, returns only that page's rows;
   - `indexStats` → `{count, corrupt}`.
   `lock` (already exists) **also drops the whole index**. Plaintext lives only here.
4. **Loader (`web/src/db/indexLoader.ts`)** — on unlock: read `records` from IDB in pages of
   ~500 (by `bySeq` cursor), send each page with `indexAdd`, report progress
   "12 400 / 48 000 yozuv indekslandi". After each page the UI may already query
   (**partial results visible immediately**). Cancel button stops loading (index keeps what
   it has). New records arriving from sync while unlocked are pushed with `indexAdd` too.
5. Minimal UI hook: the Archive view shows sync progress, index progress, a cancel button,
   and the first page from `indexQuery` (plain list; the real browser is Task 7). While
   locked it shows "Arxiv qulflangan — ochish uchun parolni kiriting" plus the number of
   encrypted records cached.

## Rules

- **Do NOT run any server.** Tests mock `fetch`; use `fake-indexeddb` (dev dependency,
  exact version ≥14 days old, report it) for IDB tests. No other new dependencies.
- **Never write plaintext to IndexedDB, storage, URL or console.** A test must scan every
  value stored in IDB after an unlock + index run and assert no decrypted text appears.
- **No `git add -A`**, no push. Explicit `web/...` paths. Don't touch `src/`, `tests/`,
  `deploy/`, `docs/`, `PROGRESS.md`, `.vps-audit/`.
- Tests first, see them fail. Run `npm run build` too.
- Fixtures: build sealed records with the real primitives (Task 4) and the `hkdf` vector
  master's content key, so decryption in tests is real, not mocked.

## Minimum tests

- sync: two pages (`has_more` true then false) → all stored, cursor = last `next_since`;
  failure while writing page 2 → cursor stays at page 1's value, re-run fetches page 2
  again, no duplicates; server clamps `limit` → loop still completes via `has_more`;
  tombstone removes its target; abort stops after the current batch; runs while locked;
- device change wipes the database;
- index: unlocked + loader → `indexQuery` returns newest first with correct keyset paging
  (equal `occurred_at` ordered by `seq`, no duplicates or gaps across pages); `kind` and
  `peer_hash` filters; corrupt record counted, others still indexed; partial results
  available after the first page; cancel stops loading;
- `lock` empties the index (`indexQuery` → `locked`), nothing plaintext remains in IDB.

## Verification (paste summary lines)

```bash
cd web
npm ci
npm run type-check
npm test
npm run build
```

## Commit

One commit, explicit paths, no AI/co-author trailers:

```
feat(web): cache the archive encrypted and index it in worker memory

The browser keeps records exactly as the server sent them, so a locked
panel leaves nothing readable on disk; they are decrypted into an index
inside the worker only after unlock. The cursor advances only after a
batch is written, so an interrupted sync re-fetches that batch instead
of skipping it, and dedup by record id makes the repeat harmless.
```

## Report back

Write `web/TASK6-REPORT.md` (do not commit): files, dependency versions, test names +
counts, type-check/build lines, every deviation with reason, anything not verified.
