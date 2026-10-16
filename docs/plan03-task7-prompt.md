# Plan 03 · Task 7 — prompt for the delegate (Gemini)

> TeamLead (customsync-server), 2026-10-16. Plan source (single copy, read-only):
> `C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-03-web-controller.md`
> — read "Task 7" and the "REVIZIYA 2026-08-25 / Task 7" block at the top.
> **This prompt overrides the plan where they differ.** The plan pages through a server
> endpoint `GET /api/v1/records` — that endpoint does **not** exist and must not be added.
> Since Task 6 the archive is decrypted only inside the crypto worker; the table pages
> through the worker's in-memory index (`indexQuery`). The server never sees a query.

---

You are implementing **Task 7 (archive browser: virtual scroll + keyset paging + new-records
banner + media_index view)** of the CustomSync web panel in
`C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server` (branch `Oybek`).
Tasks 1–6 are done. Read first: `web/src/crypto/worker.ts` (`indexBegin/indexAdd/indexQuery/
indexStats`, `compareNewestFirst`), `web/src/crypto/worker-api.ts`, `web/src/db/sync.ts`,
`web/src/db/indexLoader.ts`, `web/src/views/ArchiveView.vue`, and their tests. Reuse them.

## Data you have

- Index rows (`DecryptedIndexedRecord`): `seq, record_id, kind, account_hash, peer_hash,
  msg_id, occurred_at (unix s), observed_at, device_id, json` (decrypted payload).
- Order key is the tuple `(occurred_at, seq)`; `seq` is unique, so the tuple is a total order.
- Kinds shown in the table: `deleted`, `edited`, `activity`, `media_index`.
  `tombstone` never appears (Task 6 removes it and its target).
- `media_index` payload (spec §0.4): `{kind, file_name, rel_path, size, sha256, status,
  reason, layer, msg_date}`. `status` is e.g. `present` / `pending`; `reason` e.g.
  `quota_full`. `sha256` may be missing on old records.
- Device names: `GET /api/v1/devices` → `[{device_id, name, platform, role, ...}]` (already
  used by `DevicesView`). Unknown id → show the first 8 chars of the id.

## Design (follow it)

1. **Worker query, keyset only (rule K3 — no OFFSET, no array index as a cursor).**
   Extend `indexQuery` with:
   - `order: 'desc' | 'asc'` (default `desc`);
   - `after?: [occurred_at, seq]` — return rows strictly after this key *in the chosen order*
     (replaces `before`; keep `before` working as an alias for `desc` so Task 6 callers/tests
     do not break);
   - `max_seq?: number` — the **snapshot**: ignore rows with `seq > max_seq`;
   - existing `kind`, `peer_hash` filters stay.
   Find the start position with **binary search** on `sortedRecords` (it is sorted by
   `compareNewestFirst`), not a linear skip from index 0. For `asc`, walk the same array
   backwards. A page must be O(log n + limit) when no filter is set.
2. **Snapshot.** `indexStats` additionally returns `max_seq` (0 when empty) and, when given
   `since_seq`, `newer: number` = count of rows with `seq > since_seq`. A query session takes
   `max_seq` once at start and passes it on every page. Records that sync adds afterwards
   must not appear, shift or duplicate rows in the open session.
3. **`web/src/components/RecordTable.vue`** — PrimeVue `DataTable` (PrimeVue 5.0.2 is
   installed; do not add another table/virtual-scroll library), `scrollable`,
   `scrollHeight="flex"`, `virtualScrollerOptions` with `lazy: true`, fixed `itemSize`,
   `onLazyLoad` → load next page, `dataKey="record_id"`. Columns: time (local, from
   `occurred_at`), kind, peer (first 8 of `peer_hash` — names come in Task 9), device name,
   short preview of `json` (text field if present, else compact JSON, max ~120 chars, as
   **text**, never `v-html`).
4. **Paging state (in a composable, e.g. `web/src/composables/useArchiveQuery.ts`, so it is
   testable without the DOM).** State: `rows`, `afterKey`, `snapshot`, `loading`,
   `exhausted`, a generation counter. `loadMore()` returns early when loading or exhausted;
   `exhausted` = page shorter than `limit`. **Reset** (order or filter changed, or banner
   clicked): bump generation, clear rows/afterKey/exhausted, take a new snapshot, load page 1.
   A page response whose generation is stale is **dropped** — it must not be appended.
   (Worker calls cannot be aborted; the generation check is what keeps results unmixed.)
5. **New-records banner.** After each sync batch / periodic tick, ask
   `indexStats({since_seq: snapshot})`; if `newer > 0` show "N ta yangi yozuv — yangilash".
   The table itself does not change until the user clicks it (then reset).
6. **`media_index` view** (REVIZIYA block): a toggle/tab in `ArchiveView` that shows, per
   file, on which device it is present. Group `media_index` rows by file key
   (`sha256` when present, else `rel_path`); inside a group keep, per `device_id`, the row
   with the largest `occurred_at` (LWW, spec §0.4 — not status priority). Render e.g.
   `file_name | size | ✅ desktop-pc-01 · ❌ telefon (quota_full)`. Highlight files that are
   `pending` on some device but `present` on another. Build it in the worker or a pure
   function over query pages — a pure, unit-tested function is preferred.
7. `ArchiveView.vue`: keep Task 6 behaviour (locked notice, sync/index progress, cancel).
   Replace its first-page table with `RecordTable`; add order toggle (newest/oldest), kind
   filter, peer filter (text input of a peer_hash prefix is enough), banner, media view.

## Rules

- No new server endpoint, no change to `src/` (.NET). No plaintext leaves the worker except
  query result rows rendered in memory; nothing decrypted is written to IndexedDB,
  `localStorage`, `sessionStorage` or the URL (no filters with peer data in the query string).
- No new npm dependency. Keep existing tests green; do not weaken or delete them.
- After lock, the view must drop its rows (they are plaintext) — watch the vault store.
- Uzbek UI strings, like the existing views.

## Minimum tests (vitest)

1. Worker: desc and asc paging over ≥ 1 000 rows with duplicate `occurred_at` values gives
   every row exactly once, in order, for page sizes 1, 7 and 50.
2. Worker: `max_seq` snapshot — rows added after the snapshot are excluded from later
   pages; `indexStats({since_seq})` counts them.
3. Worker: `before` alias still behaves like Task 6.
4. Worker: binary-search start — a test that would fail if the scan started from index 0 and
   compared wrongly at the tie (`occurred_at` equal, `seq` boundary).
5. Composable: stale generation — start a reset while a page is in flight; the late page is
   not appended (use a deferred promise for the worker mock).
6. Composable: `loadMore` while loading is a no-op; `exhausted` stops further calls.
7. Composable/view: lock clears rows.
8. Media grouping: LWW per device by `occurred_at` (an older `present` must not beat a newer
   `pending`), grouping by `sha256` falling back to `rel_path`, highlight flag.
9. RecordTable preview renders `<script>` text literally (no HTML injection).

## Verification (paste summary lines)

```
cd web
npm ci
npm run type-check
npx vitest run
npm run build        # vue-tsc -b also checks test files — must pass
```

## Commit

Only `web/` files. Message:

```
feat(web): add virtualised archive table with keyset paging

Rendering 50k rows into the DOM freezes the browser, so only the visible
rows exist at a time. Pages come from the worker index by (occurred_at,
seq) key and are pinned to the snapshot taken when the query started:
records arriving mid-scroll collect in a banner instead of shifting rows
and producing duplicates.
```

Do **not** push. Do not add Co-Authored-By lines.

## Report back

Write `web/TASK7-REPORT.md` (uncommitted): files changed, the test list with what each
proves, summary lines of the four commands, and anything from this prompt you did not do
or did differently, with the reason.
