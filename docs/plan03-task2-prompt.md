# Plan 03 · Task 2 — prompt for the delegate (Gemini)

> TeamLead (customsync-server), 2026-10-16. Plan source (single copy, read-only):
> `C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-03-web-controller.md`
> — read "Ikki tekislik" and "Task 2". This prompt **overrides** the plan where they differ.

---

You are implementing **Task 2 (API client + enrolment)** of the CustomSync controller web
app in `C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server`
(branch `Oybek`). Task 1 (`web/`, commit `2ea6556`) is done: Vue 3 + Vite + TS, PrimeVue,
Pinia, vue-router, vitest. Build on it; keep its style (`cs-*` classes, Uzbek UI labels).

## Backend contract (verified in `src/CustomSync.Api/Endpoints/DeviceEndpoints.cs` — read it)

- `POST /api/v1/devices/enroll` body `{code, name, platform}` → 200
  `{deviceId, refreshToken, accessToken, expiresAt}` (`expiresAt` = ISO-8601 string);
  bad/used code → 400 `{error:"invalid_or_used_code"}`. The server normalises the code
  (case, spaces, dashes) — the UI must NOT reject or reformat it; just `trim()`.
- `POST /api/v1/devices/refresh` body `{deviceId, refreshToken}` → 200
  `{refreshToken, accessToken, expiresAt}`; invalid/revoked → **401**. The refresh token
  rotates on every call; the old one replays the same successor for 120 s (grace window).
- Web enrols with `platform: "web"`. No backend changes in this task.

## Scope

Files: `web/src/api/client.ts`, `web/src/stores/auth.ts`, `web/src/views/EnrollView.vue`,
router guard in `web/src/router.ts`, tests under `web/src/**/__tests__` or `*.spec.ts`.

## Corrections to the plan's sample code (each one needs a test)

1. **Do not destroy the refresh token on transient errors.** Clear stored credentials
   only on **401** from `/refresh`. Network error, 5xx, 502 during a deploy → keep the
   token, throw `ApiError` (status 0 for network), let the next call retry.
2. **Single-flight refresh.** Concurrent `api()` calls with an expired token must share
   ONE in-flight refresh promise (one `/refresh` request), otherwise rotation races.
3. **One retry on 401 from a normal API call:** drop the cached access token, refresh
   once, repeat the request once. A second 401 → throw.
4. **Storage may throw** (private mode, blocked storage). Wrap every `localStorage`
   access in try/catch via a small helper; on failure the session works in memory only.
   Keys: `cs.deviceId`, `cs.refreshToken`. Never store the access token.
5. Do not override a caller's `Content-Type`; set JSON content type only when `body` is a
   string and no content type was given. Pass `signal` through to `fetch`.
6. Errors: `ApiError(status, code)` where `code` is the JSON `error` field when the body
   is JSON, otherwise a short generic code — never put the raw response body or any
   token into the error message or `console`.
7. **Router guard actually wired** (not just written): not enrolled → every route
   redirects to `/enroll`; enrolled user opening `/enroll` → dashboard. When a refresh
   ends in 401 (device revoked), the auth store becomes "not enrolled" and the app lands
   on `/enroll`.
8. `EnrollView`: code input is **visible text** (not a password field),
   `autocomplete="off"`, `spellcheck="false"`; device-name input (default
   `"Web panel"`); "Ulash" button disabled while pending and on empty code; error
   shown in Uzbek (bad code vs. server unreachable are distinct messages).

## Rules

- **Do NOT run any server** (`npm run dev`/`preview`, nothing listening on a port).
  Tests mock `fetch` (`vi.stubGlobal`).
- No new dependencies unless unavoidable (then exact version, ≥14 days old, report it).
- **No `git add -A`**, no `git push`. Stage explicit `web/...` paths only. Do not touch
  `src/`, `tests/`, `deploy/`, `PROGRESS.md`, `.vps-audit/`, `docs/`.
- Write tests first and see them fail before implementing.

## Minimum tests

- enroll success stores deviceId+refreshToken, sends `platform:"web"`, code trimmed only;
- enroll 400 → "bad code" message; network failure → "unreachable" message;
- refresh 401 clears credentials; refresh 503 / network error keeps them;
- two concurrent `api()` calls with expired token → exactly one `/refresh` fetch;
- 401 on API call → one refresh + one retry; second 401 throws;
- `localStorage` throwing → enroll + api still work in memory, nothing thrown to UI;
- refresh token rotated in storage after refresh;
- guard: not enrolled → `/enroll`; enrolled on `/enroll` → dashboard; revoked → `/enroll`;
- no token string appears in thrown error messages.

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
feat(web): add API client with token refresh and enrolment

The browser enrols as an ordinary device with platform 'web', so the
backend needs no special case for it. A failed refresh only forgets the
token on 401: a 502 while the server restarts must not log the panel out.
```

## Report back

Files changed, test names + counts, type-check/build lines, every deviation with reason,
anything not verified. Write the report to `web/TASK2-REPORT.md` (do not commit it).
