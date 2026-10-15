# Plan 03 · Task 1 — prompt for the delegate (Gemini)

> TeamLead (customsync-server), 2026-10-15. Plan source (single copy, read-only):
> `C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-03-web-controller.md`
> — read the header, the "REVIZIYA 2026-08-25" block, "Ikki tekislik", "File Structure"
> and "Task 1". This prompt **overrides** the plan where they differ (corrections below).

---

You are implementing **Task 1 (scaffold + dense admin layout)** of the CustomSync server
controller web app, inside the existing repository
`C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server`
(branch `Oybek`). The backend (.NET 10, `src/`) is deployed and running; the web app
will later be served **same-origin** with it (static files at `/`, API at `/api/v1/*`,
WebSocket at `/ws/notify`). Task 1 is UI skeleton only — no auth, no crypto, no data.

## Scope

Create `web/` — Vue 3 + Vite + TypeScript SPA:

1. Scaffold (`npm create vite@latest web -- --template vue-ts`), then add
   `primevue`, `@primeuix/themes`, `primeicons`, `pinia`, `vue-router`, and dev deps
   `vitest`, `@vue/test-utils`, `jsdom`, `vue-tsc`.
2. `web/src/theme/aapanel.css` — exactly the design rules from the plan (34px rows,
   13px font, no shadows/gradients, 120ms hover/focus only, light + `data-theme="dark"`
   variables, `.cs-table-wrap` horizontal scroll so the page body never scrolls sideways).
3. `web/src/components/AppShell.vue` — sidebar + topbar + `<router-view>`.
   Sidebar sections (Uzbek labels, in this order): Boshqaruv paneli, Qurilmalar, Arxiv,
   Statistika, Xotira, Kalitlar, Sozlamalar, Audit. Active item uses `.is-active`.
   Topbar: vault lock indicator (open/closed, from a Pinia store), theme toggle,
   server status dot.
4. `web/src/router.ts` — one lazy-loaded route per section (`DashboardView` …
   `AuditView`, placeholder content only), `createWebHistory('/')`, unknown paths →
   dashboard.
5. `web/src/stores/vault.ts` (state `locked: boolean`, default `true`, no key material —
   real unlock is Task 5) and `web/src/stores/ui.ts` (theme `light|dark`, persisted in
   `localStorage` **inside try/catch** — storage may be unavailable; app must still work).
6. `web/src/composables/useServerHealth.ts` — polls `GET /api/v1/health` (relative URL)
   every 30 s with `AbortController` + timeout; states `ok | down | unknown`; stops
   polling on unmount. Drives the status dot. Never throws into the UI.
7. Narrow screens: under 768px the sidebar collapses behind a toggle; no horizontal
   page scroll at 375px width.

## Corrections to the plan text

- **Do NOT run any server** — no `npm run dev`, no `npm run preview`, nothing that
  listens on a port. Verify with build, type-check and unit tests only (below).
- **No `git add -A`.** Stage explicit paths only (`web/...`, plus root `.gitignore`
  if you must change it). Never stage `node_modules/`, `dist/`, or anything outside `web/`.
- **Exact versions + lockfile.** Install with `--save-exact`, use the latest stable
  releases that are at least 14 days old, commit `web/package-lock.json`. Run
  `npm audit --omit=dev` and report the result (do not run `npm audit fix --force`).
- Check `node -v` first; if it is below what the chosen Vite needs, stop and report.
- Do not touch `src/`, `tests/`, `deploy/`, `PROGRESS.md` or `.vps-audit/`.
- PrimeVue: register it with a theme preset from `@primeuix/themes`, but the shell
  layout itself uses the `cs-*` classes from `aapanel.css` (no PrimeVue layout wrappers).

## Tests (vitest + @vue/test-utils, jsdom) — write them first, see them fail

Add `"test": "vitest run"` and `"type-check": "vue-tsc --noEmit"` scripts. Minimum:

1. AppShell renders all 8 sidebar items in the stated order with the stated labels.
2. Clicking a sidebar item navigates (memory history in tests) and marks exactly that
   item `.is-active`.
3. Theme toggle sets `document.documentElement.dataset.theme` and persists it; when
   `localStorage` throws (stub it), toggling still works and nothing is thrown.
4. Vault indicator reflects the store (`locked` → closed icon/label; set `false` →
   open).
5. `useServerHealth`: `ok` on 200, `down` on network error / non-200 / timeout
   (fake timers), polling stops after unmount (no further fetch calls).
6. Unknown route resolves to the dashboard.

## Verification (all must pass, paste the summary lines)

```bash
cd web
npm ci
npm run type-check
npm test
npm run build
npm audit --omit=dev
```

Also confirm with `git status` that `node_modules/` and `dist/` are not tracked.

## Commit

One commit, explicit paths, message (no AI/co-author trailers):

```
feat(web): scaffold Vue app with dense admin layout

No shadows or gradients in the base theme: they look better in isolation
but get repainted per row in long lists and measurably slow scrolling,
which is the opposite of what this panel needs.
```

## Report back

- files created (tree of `web/src`), exact dependency versions, Node version;
- test count and names, type-check/build/audit summary lines, `dist` size;
- every deviation from this prompt or the plan, with the reason;
- anything you could not verify.
