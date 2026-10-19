# Plan 03 · Task 8 — prompt for the delegate (Gemini)

> TeamLead (customsync-server), 2026-10-19. Plan source (single copy, read-only):
> `C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-03-web-controller.md`
> — read "Task 8". **This prompt overrides the plan where they differ.** The plan has two
> sources (server for metadata, IndexedDB for search) and walks the IndexedDB `byTime`
> index checking text. Both are obsolete: since Task 6 IndexedDB holds **ciphertext only**
> (owner decision 2026-10-16) and Task 7 pages everything from the worker's decrypted
> in-memory index. Search therefore runs **inside the worker**, over the same index, with
> the same keyset contract. There is one source; nothing about search reaches the server.

---

You are implementing **Task 8 (client-side search over the decrypted index)** of the
CustomSync web panel in
`C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server` (branch `Oybek`).
Tasks 1–7 are done. Read first: `web/src/crypto/worker.ts` (`indexQuery` with `order`,
`after`, `max_seq`, binary search), `web/src/crypto/worker-api.ts`,
`web/src/composables/useArchiveQuery.ts` (generation counter, `error`, `reset`, `loadMore`),
`web/src/components/RecordTable.vue`, `web/src/views/ArchiveView.vue`,
`web/src/db/indexLoader.ts` (index progress), and their tests. Reuse them.

## Searchable text per kind (spec §3.2 payloads, decrypted `json`)

| kind | fields |
|---|---|
| `deleted` | `text` |
| `edited` | `old_text`, `new_text` |
| `activity` | `old_value`, `new_value` (only when they are strings) |
| `media_index` | `file_name`, `rel_path` |

Missing or non-string fields are skipped, never `JSON.stringify`-ed into the haystack
(searching for `"text"` must not match every record).

## Design (follow it)

1. **Worker: `text` parameter on `indexQuery`.** When `text` is a non-empty string after
   trimming, a row is returned only if its searchable text contains it. Matching:
   case-insensitive and Unicode-normalised — normalise both sides with
   `.normalize('NFKC').toLocaleLowerCase()`; Uzbek/Cyrillic text must work (`Салом` finds
   `салом`, `O'zbek` finds `o'zbek`). Plain substring, no regex built from user input.
   Everything else is unchanged: `order`, `after`, `max_seq`, `kind`, `peer_hash` apply
   **before** the text test (cheap filters first), the page is keyset-continued from the last
   returned row, `limit` rows max.
2. **Do not re-normalise on every query.** Build the normalised haystack once per record when
   it enters the index (`indexAdd`), keep it inside the worker (a field not sent back in
   result rows, or a parallel map keyed by `record_id`), and drop it with the record on
   tombstone and on `lock`. Result rows must not carry the haystack.
3. **Scan budget (rule K2 — never block).** A sparse search may walk the whole index. Cap
   one call at `scan_limit` examined rows (default 20 000). The response gains
   `scanned_to: [occurred_at, seq] | null` — the key of the last row examined — and
   `complete: boolean` (true when the end of the index was reached). The composable
   continues the next page from `scanned_to`, not from the last *match*, so rows that
   were already examined are not scanned again and none are skipped. Without `text`,
   behaviour and response stay compatible with Task 7 (`complete`/`scanned_to` may be added
   but existing callers and tests must keep passing).
4. **Composable.** Add `searchText` to `useArchiveQuery`. Changing it resets the query
   (same generation counter as order/filter — one mechanism, not a second counter), after a
   **250 ms debounce** in the view or composable. A result from an older generation is
   dropped (already true for `reset`/`loadMore`; keep it true for search). `loadMore` with a
   search uses `scanned_to` as the continuation key. If a page returns 0 matches but
   `complete` is false, the composable fetches the next chunk itself (bounded loop; stop on
   generation change), so the user is not left with an empty table and no way forward.
5. **Partial-index warning.** While the index loader has not finished (progress < 100 %, or
   it was cancelled), a search shows above the table:
   "Indeks N% tayyor — natijalar to'liq bo'lmasligi mumkin." Take N from the existing loader
   progress; do not compute it a second way. No warning when there is no search text.
6. **View.** A search input in `ArchiveView` (Uzbek placeholder, e.g. "Matn bo'yicha
   qidirish..."), a clear button, and an empty-result message that names the query
   ("«…» bo'yicha hech narsa topilmadi") — rendered as text, never `v-html`. `RecordTable`
   stays mode-agnostic. The media tab is out of scope.
7. **Privacy.** The search text is plaintext of the user's archive: do not put it in the URL
   / router query, `localStorage`, `sessionStorage`, IndexedDB, logs or `console.*`; clear it
   from the composable on `lock` together with the rows.

## Rules

- No server change, no new endpoint, no change to `src/` (.NET). No new npm dependency
  (write the debounce yourself — a few lines, with a cancel on unmount).
- Keep all existing tests green; do not weaken or delete them.
- Uzbek UI strings, like the existing views.

## Minimum tests (vitest)

1. Worker: case/Unicode — `SALOM`, `салом` (Cyrillic) and an NFKC-equivalent form find the
   right rows; a field name like `text` does not match every record.
2. Worker: per-kind fields — `edited` matches on `old_text` and on `new_text`; `activity`
   ignores non-string values; `media_index` matches `file_name`.
3. Worker: search + keyset paging over ≥ 1 000 rows with duplicate `occurred_at`, page sizes
   1 and 7, desc and asc — every match exactly once, in order.
4. Worker: `scan_limit` — with matches only at the far end, the first call returns 0 rows,
   `complete: false` and a `scanned_to`; continuing from `scanned_to` finds them; no row is
   returned twice.
5. Worker: snapshot still applies to search — a late matching record with an old
   `occurred_at` and `seq > max_seq` is not returned.
6. Worker: tombstone and lock remove the haystack (after tombstone the target is no longer
   found; after lock + unlock + empty index nothing is found and the internal haystack
   storage is empty).
7. Composable: two quick searches where the first resolves last — only the second's rows are
   shown (deferred worker mock).
8. Composable: empty page with `complete: false` triggers the follow-up fetch; it stops when
   the generation changes.
9. View: partial-index warning shown with N from loader progress during search, hidden
   without search text; empty-result message names the query as text (`<b>x</b>` is shown
   literally).
10. Lock clears `searchText` and rows; search text is not found in `localStorage`,
    `sessionStorage` or the router's current URL after a search.

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
feat(web): add client-side search over the decrypted index

Payloads are encrypted at rest, in the browser as on the server, so search
runs inside the crypto worker over the index it already holds, with the
same keyset contract as browsing -- the table never learns which mode it
is in. A scan budget keeps a sparse search from freezing the worker, and
stale results are dropped by the same generation counter as filters.
```

Do **not** push. Do not add Co-Authored-By lines.

## Report back

Write `web/TASK8-REPORT.md` (uncommitted): files changed, the test list with what each
proves, summary lines of the four commands, and anything from this prompt you did not do
or did differently, with the reason.
