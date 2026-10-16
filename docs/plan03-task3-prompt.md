# Plan 03 · Task 3 — prompt for the delegate (Gemini)

> TeamLead (customsync-server), 2026-10-16. Plan source (single copy, read-only):
> `C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-03-web-controller.md`
> — read "Ikki tekislik", the REVIZIYA block (quota panel) and "Task 3". This prompt
> **overrides** the plan where they differ.

---

You are implementing **Task 3 (control plane screens)** of the CustomSync web panel in
`C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server` (branch `Oybek`).
Tasks 1–2 are done: `web/` shell, router guard, `web/src/api/client.ts` (`api<T>()`,
`ApiError`, token refresh). Use `api<T>()` for every call. Nothing here needs the master key.

## Backend contract — JSON is **snake_case** on the wire (both directions)

The server uses `JsonNamingPolicy.SnakeCaseLower`. Type every response with snake_case
interfaces; do not convert to camelCase. All endpoints below are admin-only.

| Call | Response |
|---|---|
| `GET /api/v1/devices` | `[{device_id, name, platform, role, enrolled_at, last_seen_at, last_cursor, revoked_at}]` (dates ISO, nullable ones may be `null`) |
| `POST /api/v1/devices/codes` body `{}` or `{role:"admin"}` | `{code}` |
| `DELETE /api/v1/devices/{device_id}` | 204 |
| `GET /api/v1/devices/health` | `[{device_id, name, platform, revoked, reported_at, stale, rss_bytes, memory_limit_bytes, cache_db_bytes, media_store_bytes, media_store_files, tdlib_files_bytes, tdlib_database_bytes, free_disk_bytes}]` |
| `GET /api/v1/settings` | `[{key, value, value_type, category, description, updated_at}]` — `value` is always a string |
| `PUT /api/v1/settings/{key}` body `{value: "<string>"}` | 204; 400 `{error:"invalid_value"}` wrong type; 404 `{error:"unknown_setting"}` |
| `GET /api/v1/audit?limit=&before=` | `{items:[{id, at, action, device_id, actor_device_id, detail}], next_before}` — newest first; `detail` is a JSON **string** or null; `next_before` null on the last page |
| `GET /api/v1/stats/storage` | `{record_count, record_bytes, media_count, media_bytes}` |
| `GET /api/v1/stats/peers?sort=bytes|count|recent&limit=10` | `[{peer_hash, record_count, total_bytes, last_occurred_at}]` (`last_occurred_at` = unix time; treat values below 1e12 as seconds, otherwise ms) |

`value_type` is one of `int`, `bool`, `json` today. Unknown type → read-only text with a note.

## Scope

Files (adjust names if Task 1 used others): `web/src/api/devices.ts`, `settings.ts`,
`audit.ts`, `stats.ts`; `DashboardView.vue`, `DevicesView.vue`, `SettingsView.vue`,
`AuditView.vue`, `StorageView.vue`; small shared pieces (`StatCard.vue`, a confirm dialog,
`formatBytes`/`formatDate` utils). Uzbek UI labels, `cs-*` dense style from Task 1.

1. **Devices:** table (name, platform, role, enrolled, last seen, cursor, status
   active/revoked). "Bekor qilish" only for non-revoked rows, behind a confirm dialog that
   names the device; revoking **this** panel's own device (`cs.deviceId`) needs an extra
   warning — afterwards the panel lands on `/enroll`. "Yangi kod" (role select: device /
   admin) opens a modal with the code in a large monospace font and a countdown from the
   `auth.enroll_code_minutes` setting (read once from `GET /settings`); after expiry the
   code is greyed out with "muddati tugadi". The code is never logged or stored.
2. **Settings:** grouped by `category`; per row key, description, editor by `value_type`
   (`int` → numeric input, `bool` → toggle, `json` → textarea). Save per row: send the
   string, show 400 `invalid_value` inline on that row without losing the user's input,
   reflect the saved value on 204. Validate `int` and `json` client-side before sending,
   but the server stays the authority. Show `storage.quota_total_mb` /
   `storage.quota_per_device_mb` with "0 = cheksiz" hint (REVIZIYA quota panel).
3. **Audit:** table (time, action, device, actor, detail). Keyset paging with
   "Ko'proq yuklash" using `next_before`; never offset. `detail` parsed defensively
   (invalid JSON → show raw string), rendered as text — **never `v-html`**.
4. **Storage:** totals from `/stats/storage`, records vs media split, top-10 peers from
   `/stats/peers` (peer names come in Task 9 — show the hash shortened), device health
   table from `/devices/health` (stale rows marked).
5. **Dashboard:** 4 `StatCard`s — total records, disk used (record+media bytes), active
   devices (non-revoked), last sync (max `last_seen_at`); below: latest 10 audit events and
   device status. Each card handles loading / error / empty independently — one failing
   endpoint must not blank the whole page.
6. Every list request uses an `AbortController`; leaving the view aborts it. An
   `AbortError` is not shown as an error (Task 2 client already rethrows it).

## Rules

- **Do NOT run any server**; tests mock `fetch`. No new dependencies unless unavoidable.
- **Test fixtures must use the exact snake_case shapes in the table above** — Task 2
  passed its own tests while speaking camelCase to a snake_case server. Put the sample
  responses in one fixtures file and reuse it.
- **No `git add -A`**, no push. Stage explicit `web/...` paths. Don't touch `src/`, `tests/`,
  `deploy/`, `docs/`, `PROGRESS.md`, `.vps-audit/`.
- Tests first, see them fail. Run `npm run build` too — `type-check` alone does not check
  test files, `vue-tsc -b` in `build` does.

## Minimum tests

- devices: rows rendered from fixture; revoke shows confirm, DELETE sent only after confirm,
  cancelled confirm sends nothing; own-device revoke shows the extra warning; revoked rows
  have no revoke button; code modal shows code + countdown, expires (fake timers);
- settings: editor per type; `bool` toggle sends `"true"`/`"false"`; 400 keeps input and
  shows inline error; bad `int`/`json` blocked client-side;
- audit: second page requests `before=<next_before>`; button hidden when `next_before` null;
  malformed `detail` shown raw; HTML in `detail` rendered as text;
- dashboard: one endpoint failing leaves the other cards rendered;
- leaving a view aborts its in-flight request and shows no error.

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
feat(web): add control plane views

Everything here works without the master key, so the panel stays fully
useful for operating the server even when the vault is locked. The
settings screen is the user-facing half of the no-config-in-code rule:
values edited here take effect without a redeploy.
```

## Report back

Write `web/TASK3-REPORT.md` (do not commit): files, test names + counts, type-check/build
lines, every deviation with reason, anything not verified.
