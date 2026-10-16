# Plan 03 · Task 4 — prompt for the delegate (Gemini)

> TeamLead (customsync-server), 2026-10-16. Plan source (single copy, read-only):
> `C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-03-web-controller.md`
> — read "Task 4". **The plan's sample code is outdated** (wrong `record_id` formula, wrong
> vector-file layout, missing keys). This prompt overrides it; the contract is
> `test-vectors.json` itself.

---

You are implementing **Task 4 (crypto primitives + Web Worker)** of the CustomSync web panel
in `C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server` (branch
`Oybek`). Tasks 1–3 are done (`web/`). This task adds **no UI** and makes **no network
calls**: it is pure crypto, verified byte-for-byte against the cross-platform vectors that
the C++ client and the .NET server already pass.

## The contract: `test-vectors.json`

- It lives in the tdesktop repo and must **not** be copied into this repo.
- Locate it the same way the .NET tests do (`tests/CustomSync.Tests/TestVectors.cs`):
  env `CUSTOMSYNC_TEST_VECTORS`, else `C:\TBuild\tdesktop\docs\sync-protocol\test-vectors.json`,
  else `D:\TBuild\tdesktop\docs\sync-protocol\test-vectors.json`.
- **If it is not found, the vector tests FAIL with a message saying how to set the env var.
  Never skip, never pass vacuously.** Each section's test also asserts its case count is > 0.

Sections and exact rules (read each `_note` in the file — they are authoritative):

| Section | Rule |
|---|---|
| `hkdf` | HKDF-SHA256, salt = **32 zero bytes** (always passed explicitly), info = the key of `derived`; four keys: `customsync-content-v1`, `customsync-media-v1`, `customsync-peer-v1`, `customsync-account-v1` |
| `account_hash` | `hex(HMAC-SHA256(account_key, account_id)[0..16])`; `account_id` is a decimal **string** |
| `peer_hash` | `hex(HMAC-SHA256(peer_key, peer_id)[0..16])`; `peer_id` decimal string |
| `record_id` | `hex(SHA256(kind ‖ 0x00 ‖ account_hash ‖ 0x00 ‖ peer_hash ‖ 0x00 ‖ msg_id ‖ 0x00 ‖ occurred_at))`, numbers as decimal strings; **`msg_id` may be negative, keep the sign**; `account_hash` is `""` only for `kind='activity'` |
| `discriminator` | first 8 bytes of `SHA256(UTF-8(text))` as big-endian int64, top bit cleared → always ≥ 0. Values exceed 2^53: use **`BigInt`**, compare as decimal strings |
| `pbkdf2` | PBKDF2-HMAC-SHA256, 32 bytes; 600 000 and 2 000 000 iterations (slow — raise the test timeout) |
| `aes_gcm` | AES-256-GCM, 12-byte nonce, 16-byte tag; vectors give `ciphertext_hex` and `tag_hex` **separately** — Web Crypto wants `ct ‖ tag`. Test both seal and open |
| `key_wrap` | passphrase wrap: KEK = PBKDF2(passphrase, salt, iterations **from the wrap**), master = AES-GCM-open(KEK, nonce, wrapped_key = ct[0:32] ‖ tag[32:48]), **no AAD**; salt/nonce/wrapped_key are **base64**. `wrong_passphrase` must fail (GCM tag failure is the only signal) |
| `fingerprint` | `hex(SHA256("customsync-fingerprint-v1" ‖ master)[0:8])` (16 hex chars) |

The `account_hash` / `peer_hash` vectors use the `customsync-account-v1` / `customsync-peer-v1`
keys derived from the `hkdf` section's `master_key_hex` (TeamLead re-computed every hash,
`record_id`, `discriminator` and `fingerprint` case from these rules — they all match).

Records on the wire (for `openRecords`): payload = `ciphertext ‖ tag`, nonce 12 bytes,
**AAD empty**, key = content key. Same as `src/CustomSync.Capture/Sync/SyncCrypto.cs`.
Key-wrap API shape (Task 5 will fetch it, snake_case): `{wrap_id, wrap_type, label, salt,
nonce, wrapped_key, iterations}` with base64 strings.

## Scope

1. `web/src/crypto/primitives.ts` — pure functions over Web Crypto: `hkdf`, `pbkdf2`,
   `hmacSha256`, `sha256`, `accountHash`, `peerHash`, `recordId`, `discriminator` (returns
   `bigint`), `sealGcm`/`openGcm` (`openGcm` returns `null` on failure, never throws),
   `unwrapPassphrase(wrap, passphrase)` → master bytes or `null`, `fingerprint`, plus small
   `hex`/`base64` helpers. No `Buffer` in `src/` (browser code); tests may use Node APIs.
2. `web/src/crypto/worker.ts` — module Worker owning the keys. Messages:
   `unlock {passphrase, wrap}` → derives content/media/peer/account keys, replies
   `{ok:true, fingerprint}` or `{ok:false, error:'wrong_passphrase'}`; `lock` → drops every key;
   `openRecords {records:[{record_id, nonce, payload}]}` → per record `{record_id, json}` or
   `{record_id, corrupt:true}` (one bad record never fails the batch); `peerHash {peer_id}`
   (Task 7 needs it); unknown type → `{ok:false, error:'unknown_request'}`; requests while
   locked → `{ok:false, error:'locked'}`. Master bytes are zeroed after derivation.
3. `web/src/crypto/worker-api.ts` — typed main-thread wrapper: request ids, one pending
   promise per id, timeout, `terminate()`. Keys never cross `postMessage` — only results.
4. Keep the message handler a plain exported function (e.g. `handleMessage(state, req)`) so
   it can be unit-tested in Node without a real Worker; `worker.ts` just wires it to
   `self.onmessage`.

## Rules

- **Do NOT run any server.** No new runtime dependencies (Web Crypto only). If vitest's
  environment lacks `crypto.subtle`, use the `node` environment for crypto test files.
- **No `git add -A`**, no push. Explicit `web/...` paths. Don't touch `src/`, `tests/`,
  `deploy/`, `docs/`, `PROGRESS.md`, `.vps-audit/`, and never write into the tdesktop repo.
- Tests first, see them fail. Run `npm run build` too (`type-check` skips test files).
- No key, passphrase or plaintext in any `console.*` call or error message.

## Minimum tests

- one test per vector section, iterating **all** cases, asserting count > 0;
- vector file missing → the loader throws a clear error (test it with a bogus env path);
- `record_id` with negative `msg_id` and with `kind='activity'` + empty account_hash
  (both are in the vectors — make sure they are exercised);
- `openGcm` with a flipped tag byte → `null`;
- handler: unlock with the `key_wrap` vector → fingerprint equals the `fingerprint` of
  that master; wrong passphrase → `wrong_passphrase`; `openRecords` before unlock →
  `locked`; after unlock a batch of [valid, corrupted] → one `json`, one `corrupt`;
  after `lock` → `locked` again;
- no reply object ever contains a key or master (assert on the serialized replies).

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
feat(web): add crypto worker matching the shared test vectors

Keys live only inside the worker and never cross postMessage, so a
script injected into the page cannot read them. The primitives are
checked against the same vectors the desktop client and the server
pass, because a one-byte disagreement would fail silently as
"corrupt" records rather than as an error.
```

## Report back

Write `web/TASK4-REPORT.md` (do not commit): files, test names + counts (per vector
section), type-check/build lines, how the vector file was located, every deviation with
reason, anything not verified.
