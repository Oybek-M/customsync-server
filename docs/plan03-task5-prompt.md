# Plan 03 · Task 5 — prompt for the delegate (Gemini)

> TeamLead (customsync-server), 2026-10-16. Plan source (single copy, read-only):
> `C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-03-web-controller.md`
> — read "Task 5". This prompt overrides the plan where they differ.

---

You are implementing **Task 5 (vault unlock + key wrap management)** of the CustomSync web
panel in `C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server`
(branch `Oybek`). Tasks 1–4 are done: shell + `stores/vault.ts` (Task 1, `locked` flag),
API client (Task 2), control-plane views (Task 3), crypto worker + `worker-api.ts`
(Task 4). Reuse them; do not duplicate crypto.

## Backend contract (snake_case JSON, verified in `src/CustomSync.Api/Endpoints/KeyEndpoints.cs`)

| Call | Response |
|---|---|
| `GET /api/v1/keys/wraps` (any device) | `[{wrap_id, wrap_type, label, created_at, last_used_at}]` — summary only, no key material |
| `GET /api/v1/keys/wraps/{wrap_id}` | `{wrap_id, wrap_type, label, salt, nonce, wrapped_key, iterations}` (base64 strings); **rate-limited → 429**; 404 if gone |
| `POST /api/v1/keys/wraps` (admin) body `{wrap_type, label, salt, nonce, wrapped_key, iterations}` | `{wrap_id}` |
| `DELETE /api/v1/keys/wraps/{wrap_id}` (admin) | 204; **409 `{error:"last_wrap"}`** when it is the only wrap (server-enforced since `ec739b8`) |
| `GET /api/v1/settings` (admin) | includes `web.autolock_minutes` (int, default 15) |

Only `wrap_type: "passphrase"` can be opened by the web panel today (the only wrap type
with shared vectors). Other types are listed but their "Ochish" is disabled with the note
"web panelda hali qo'llab-quvvatlanmaydi".

## Scope

1. **Unlock** (`components/UnlockDialog.vue`, `stores/vault.ts`): list wraps → pick a
   passphrase wrap → `GET` it → worker `unlock` → on success `vault.locked = false` and
   show the key **fingerprint** (16 hex, grouped `xxxx xxxx xxxx xxxx`) so the owner can
   compare it with tdesktop's "Kalit barmoq izi (FP)". Distinct Uzbek messages for:
   wrong passphrase, 429 (too many attempts — wait), 404 (wrap deleted — refresh list),
   network failure. The passphrase field is cleared after every attempt.
2. **Never persisted:** master key, derived keys, passphrase and fingerprint never go to
   `localStorage`/`sessionStorage`/IndexedDB/URL/console. A page reload means locked —
   by design.
3. **Auto-lock** (`composables/useAutoLock.ts`): timeout from `web.autolock_minutes`
   (read once; on 403/any failure fall back to 15 and say so in a dev-only comment, not a
   UI error). Reset on `pointerdown`, `keydown`, `wheel`, `touchstart` (throttled; not
   `mousemove` on every pixel). On timeout: worker `lock`, `vault.locked = true`. Also lock
   when the worker reports `locked` unexpectedly. Listeners removed on unmount.
4. **Worker change — keep the master while unlocked.** Creating a new wrap needs the
   master key, but Task 4 zeroes it right after derivation. Keep the master **inside the
   worker** while unlocked; zero it (with the derived keys) on `lock`. Add a
   `createWrap {passphrase, label}` request: random 16-byte salt, random 12-byte nonce,
   600 000 iterations, AES-GCM-seal the master (no AAD), **re-open it with the same
   passphrase and check the fingerprint matches before replying**, reply
   `{salt, nonce, wrapped_key, iterations}` as base64. Locked → `{ok:false, error:'locked'}`.
5. **Keys screen** (`views/KeysView.vue`): table (type, label, created, last used). "Yangi
   o'ram" (only when unlocked; admin): label, passphrase ≥ 12 chars + confirmation field,
   worker `createWrap` → `POST`. Delete: confirm dialog naming the label; **disabled when
   only one wrap is listed** with the text "Bu oxirgi o'ram. O'chirsangiz arxivga kirish
   butunlay yo'qoladi."; still handle a server `409 last_wrap` (another admin may have
   deleted one meanwhile) by showing that message and refreshing the list.
6. Topbar lock indicator (Task 1) reflects `vault.locked`; clicking it when locked opens
   `UnlockDialog`, when unlocked offers "Qulflash".

## Rules

- **Do NOT run any server**; tests mock `fetch` and use the Node handler from Task 4 (or a
  fake worker) — no real Worker needed in unit tests.
- Reuse the `key_wrap` vector from `test-vectors.json` (located like Task 4's loader) for
  the unlock tests — correct passphrase, `wrong_passphrase`, and its `fingerprint`.
- **No `git add -A`**, no push. Explicit `web/...` paths. Don't touch `src/`, `tests/`,
  `deploy/`, `docs/`, `PROGRESS.md`, `.vps-audit/`.
- Tests first, see them fail. Run `npm run build` too.
- If you need Node types for tests, put them in a **test-only tsconfig** instead of
  widening `tsconfig.app.json` (Task 4 added `"node"` there — move it out if you can do so
  without breaking the build, and report it).

## Minimum tests

- unlock with the vector passphrase → unlocked, fingerprint shown grouped; wrong passphrase
  → message, still locked, field cleared; 429 and 404 → their own messages;
- nothing secret in storage: after unlock, `localStorage`/`sessionStorage` contain no
  passphrase, key or fingerprint (scan all values);
- auto-lock with fake timers: locks after N minutes idle, activity resets the timer,
  fallback 15 when settings returns 403, listeners removed after unmount;
- worker `createWrap`: result re-opens with the same passphrase to the same fingerprint;
  while locked → `locked`; `lock` zeroes master (subsequent `createWrap` → `locked`);
- keys view: delete disabled with one wrap; confirm required; 409 `last_wrap` shows the
  message and reloads; new-wrap form rejects < 12 chars and mismatched confirmation;
  POST body is snake_case with base64 fields.

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
feat(web): add vault unlock and key wrap management

The master key is never persisted to storage -- a page refresh requires
unlocking again, which is the intended trade. A new wrap is re-opened
inside the worker before it is uploaded, so a wrap that could not be
opened again never reaches the server.
```

## Report back

Write `web/TASK5-REPORT.md` (do not commit): files, test names + counts, type-check/build
lines, every deviation with reason, anything not verified.
