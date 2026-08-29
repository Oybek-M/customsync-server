Execute the task described below. It is an implementation job in an
existing C# repository — not a document to review, summarise or score.
The only output that counts is committed source code.

REPOSITORY
  C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server
  Branch: Oybek. Never push to a remote named "upstream".

HOW TO REPORT
  Work quietly. Do not narrate steps. Do not paste build or test logs.
  The final report must be SHORT — the four points at the bottom, a line
  or two each. Previous reports have been far longer than useful.

STEP 0 — BEFORE YOU WRITE ANYTHING
  From the repository root run `dotnet test`.
  Expected right now: 99 passed, 0 failed. If it is not 99, stop and say
  so. Then read PROGRESS.md.

NON-NEGOTIABLE RULES
  K1  No configuration literals in code.
  K6  TDD: failing test first, watch it fail, then implement.
  K7  ONE commit. Imperative subject, WHY in the body, no "Key changes:"
      list, no Co-Authored-By trailer.
  Language: code comments in Uzbek, identifiers in English.
  Never run the application or any server. Tests only.
  Never edit anything under C:\TBuild\tdesktop\.
  🔴 Do NOT reassign `o.Events` in Program.cs — it holds two handlers.

============================================================
YOUR TASK — PLAN 01b, TASK 8
============================================================

Base text: plan file
C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01b-backend-sync.md
section "## Task 8: Platformalararo test vektorlari".

🔴 READ THIS BEFORE THE PLAN. Task 8 is the most out-of-date section in
the whole plan series, and following it literally would do real damage.

--- WHAT THE PLAN GETS WRONG ---

It tells you to build tools/GenerateTestVectors and have it WRITE
docs/sync-protocol/test-vectors.json.

That file already exists. It is the cross-platform contract, it lives at
C:\TBuild\tdesktop\docs\sync-protocol\test-vectors.json, it is generated
by generate-vectors.py in that same folder, and it was regenerated after
spec §0.12 changed the record_id formula.

Writing a second one inside this repository would create a competing
source of truth. docs/sync-protocol/README.md forbids exactly this:
"bitta nusxa, ko'p havola" — one copy, many links. A duplicated contract
goes stale immediately and the platforms drift apart without anyone
noticing, which is the whole failure mode the vectors exist to prevent.

The plan's generator is also written against the OLD four-argument
record_id formula, from before account_hash existed.

So:
  ❌ Do NOT create tools/GenerateTestVectors.
  ❌ Do NOT create any test-vectors.json inside this repository.
  ❌ Do NOT edit the one in the tdesktop tree.
  ✅ DO verify that .NET reproduces the existing vectors exactly.

--- WHAT TASK 8 ACTUALLY IS ---

tests/CustomSync.Tests/RecordIdTests.cs already checks ONE of the five
vector families: record_id. The other four are unverified on .NET.

Read the vector file. It contains:

  hkdf          master key -> 4 derived keys, salt = 32 zero bytes
  account_hash  HMAC-SHA256(account_key, account_id)[0..16] hex
  peer_hash     HMAC-SHA256(peer_key, peer_id)[0..16] hex
  aes_gcm       3 cases; nonce 12 bytes, tag 16 bytes, tag SEPARATE
  pbkdf2        3 cases; 600k and 2M iterations

tests/CustomSync.Tests/TestVectors.cs already loads the file — reuse it,
do not write a second loader.

To check the hash families you need the derivation primitives, which
CustomSync.Core does not have yet. Add them there, next to RecordId:

  DeriveKey(masterKey, info)      HKDF-SHA256, 32-byte output,
                                  salt = 32 ZERO bytes (not empty —
                                  libraries differ here and that is a
                                  classic silent interop break)
  ComputeAccountHash(accountKey, accountId)
  ComputePeerHash(peerKey, peerId)

account_id and peer_id are DECIMAL STRINGS, not numbers. Both hashes are
the first 16 bytes of the HMAC, lowercase hex.

The server itself never calls these — it never holds the master key. They
exist as the .NET reference implementation, which is what the vector file
is checking.

--- TESTS REQUIRED ---

New file tests/CustomSync.Tests/CryptoVectorTests.cs, six tests:

  1. HKDF reproduces all four derived keys
  2. account_hash matches all three cases
  3. peer_hash matches all three cases
  4. AES-256-GCM encryption produces the expected ciphertext AND tag
  5. AES-256-GCM decryption recovers the plaintext from ciphertext + tag
  6. PBKDF2 matches all three cases

Test 4 is the one that catches the classic bug: .NET's AesGcm takes
ciphertext and tag as SEPARATE buffers, and the vectors keep them
separate too — `ciphertext_hex` does not contain the tag. Some libraries
append it. Assert both fields independently.

Test 6 includes a 2,000,000-iteration case and will take a second or two.
That is expected; do not lower the iteration count to speed it up.

============================================================
DEFINITION OF DONE
============================================================

  - dotnet test  -> 105 passed, 0 failed  (99 today + 6 new)
  - dotnet build -> 0 warnings
  - You watched the new tests fail before implementing (K6)
  - No new test-vectors.json anywhere; the tdesktop one is untouched
  - No tools/GenerateTestVectors project
  - One commit, K7 style

If the count is not 105, STOP and report it.

============================================================
FINAL REPORT — four short points, a line or two each
============================================================

  1. Final `dotnet test` summary line.
  2. `git show --stat HEAD` (the stat block only) and the commit subject.
  3. Which of the five vector families are now verified on .NET.
  4. Anything wrong or ambiguous you had to guess at.

OUT OF SCOPE
  - Do not start Task 9 (deployment).
  - Do not add packages; .NET 8 has HKDF, AesGcm and Rfc2898DeriveBytes.
  - Do not run the application or any server.
