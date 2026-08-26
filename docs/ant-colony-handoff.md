# Handoff brief — customsync-server

You are implementing one task of a multi-device sync backend. This
document is the contract for your work. Read all of it before you touch
a file.

**Written in English on purpose.** The plans and the code comments are
in Uzbek; these *instructions* are in English so nothing here is
misread. Your output must still follow the Uzbek conventions below.

---

## 1. Repository

```
C:\Users\Oybek\Documents\Projects programming\Telegram\customsync-server
```

Branch: `Oybek`. Never push to a remote named `upstream`.

Start by reading `PROGRESS.md` in that repo. It records where the
implementation stands, the verified environment facts, and every
deliberate deviation from the plan text. Do not re-derive any of it.

---

## 2. Your task

> **Plan 01a, Task 4 — `SettingsService`**
>
> File:
> `C:\TBuild\tdesktop\docs\superpowers\plans\2026-07-29-multi-device-sync-01a-backend-foundation.md`
>
> Section: `## Task 4: SettingsService — runtime konfiguratsiya`

Work through its Steps 1–7 in order. The plan contains the code
verbatim; type it as written **except** where section 4 of this document
overrides it.

The plan is in Uzbek. If a sentence is unclear, the code block below it
is authoritative — do not invent an alternative design.

---

## 3. Rules that are never negotiable

These come from the plan series index (`K1`–`K7`) and the spec. Breaking
one is a failed task even if the tests pass.

**K1 — no configuration literals in code.** Any tunable value lives in
the `server_settings` table and is edited from the web app. Forbidden as
literals anywhere in the source: sync intervals, batch sizes, page
sizes, retention periods, disk thresholds, rate limits, timeouts,
feature toggles. This task *is* the mechanism that enforces K1, so
setting it up with hardcoded values would defeat its whole purpose.

**K3 — offset pagination is forbidden.** Keyset (cursor) pagination
only. Not directly relevant to this task, but never introduce `Skip()`.

**K4 — writes are idempotent.** Any operation may be retried with the
same result.

**K6 — TDD, in this order.** Write the failing test → run it → *see it
fail* → write the minimal implementation → run it → see it pass. Do not
write the implementation first. Do not skip watching it fail; a test
that never failed has proven nothing.

**K7 — commit style.** Imperative subject line. The body explains **why**,
not what (the diff already shows what). Never add a `Co-Authored-By`
trailer. One commit for the task.

**Spec §0 overrides the plan text.** The plans were written 2026-07-29;
the spec gained a `§0 REVIZIYA` section on 2026-08-25 with eleven
decisions that supersede anything written earlier. Where they conflict,
§0 wins. Section 4 below already applies every §0 item relevant to this
task — you do not need to reconcile them yourself.

**Language.** Code comments, XML doc comments and user-facing message
strings are written in **Uzbek**, matching the surrounding files. Read
`src/CustomSync.Core/RecordId.cs` for the house style. Identifiers stay
in English.

---

## 4. Required deviations from the plan text

Apply all three. Each one is a place where following the plan literally
produces broken or unsafe code.

### 4.1 `DatabaseFixture` must not hardcode the database password

The plan shows:

```csharp
private const string AdminConnection =
    "Host=localhost;Port=5432;Database=postgres;Username=customsync;Password=CHANGE_ME";
```

That password is a placeholder and will fail. The real one was generated
by `scripts\db-bootstrap.ps1` and written to
`src\CustomSync.Api\appsettings.Development.json`, which is
**gitignored**. Never copy a password into a source file.

Replace the constant with this resolver, and use
`AdminConnection` as a static readonly field initialised from it:

```csharp
private static readonly string AdminConnection = ResolveAdminConnection();

private static string ResolveAdminConnection()
{
    var fromEnv = Environment.GetEnvironmentVariable("CUSTOMSYNC_TEST_DB");
    if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CustomSync.sln")))
        dir = dir.Parent;

    if (dir is null)
        throw new InvalidOperationException(
            "CustomSync.sln topilmadi -- test qayerdan ishga tushganini aniqlab bo'lmadi.");

    var settingsPath = Path.Combine(
        dir.FullName, "src", "CustomSync.Api", "appsettings.Development.json");

    if (!File.Exists(settingsPath))
        throw new InvalidOperationException(
            $"{settingsPath} yo'q. Avval scripts\\db-bootstrap.ps1 ni ishga tushiring.");

    using var stream = File.OpenRead(settingsPath);
    var connection = JsonDocument.Parse(stream)
        .RootElement.GetProperty("ConnectionStrings")
        .GetProperty("Postgres").GetString();

    if (string.IsNullOrWhiteSpace(connection))
        throw new InvalidOperationException("ConnectionStrings:Postgres bo'sh.");

    // Fixture o'z test bazasini YARATADI, shuning uchun avval `postgres`
    // bazasiga ulanadi.
    return Regex.Replace(connection, @"Database=[^;]*", "Database=postgres");
}
```

Needs `using System.Text.Json;` and `using System.Text.RegularExpressions;`.

Keep the rest of the fixture exactly as the plan has it, including
`ConnectionString => AdminConnection.Replace("Database=postgres", ...)`.

### 4.2 Ten additional settings keys

Spec §0.3 (retention) and §0.9 (quota) were written after the plan, so
its `Defaults` list is incomplete. Add these ten entries, keeping the
same `New(...)` helper and formatting:

| Key | Value | Type | Category |
|---|---|---|---|
| `retention.deleted_days` | `0` | int | retention |
| `retention.edited_days` | `0` | int | retention |
| `retention.activity_days` | `90` | int | retention |
| `retention.ghost_read_days` | `0` | int | retention |
| `retention.setting_days` | `0` | int | retention |
| `retention.peer_directory_days` | `0` | int | retention |
| `retention.media_index_days` | `0` | int | retention |
| `retention.tombstone_days` | `0` | int | retention |
| `storage.quota_total_mb` | `0` | int | storage |
| `storage.quota_per_device_mb` | `0` | int | storage |

`0` means unlimited. Write Uzbek descriptions in the style of the
existing rows. Two of them carry reasoning worth stating in the
description text:

- `retention.activity_days` is **90** while the desktop client keeps only
  30 days. The server is the central archive and must retain *longer*
  than any client. If it retained less, a client would keep re-pushing
  records the server had dropped.
- Server-side retention **never creates a `tombstone` record.** Retention
  is a local storage decision; a tombstone is a global delete. Confusing
  the two would make every device truncate every other device's archive.

Add one more test to `SettingsServiceTests` asserting
`retention.activity_days` seeds as `90`, so the new keys are covered.

### 4.3 After Step 6, `HealthTests` needs a live database

Step 6 adds `MigrateAsync()` and `EnsureDefaultsAsync()` to `Program.cs`
startup. `WebApplicationFactory<Program>` runs that startup path, so the
existing `HealthTests` will from now on require a reachable database.
That is expected — do not try to avoid it, and do not remove the startup
calls. Just make sure the full suite still passes.

---

## 5. Definition of done

All of these, verified by actually running the commands:

- [ ] `dotnet build` — 0 errors, **0 warnings**. The tree is currently
      warning-free; keep it that way.
- [ ] `dotnet test` — **14 tests pass, 0 fail.** (10 exist today; this
      task adds 3 from the plan plus 1 from §4.2.)
- [ ] You watched the new tests fail before implementing (K6).
- [ ] No password, connection string, or tunable value is hardcoded in
      any source file.
- [ ] `git status` is clean apart from your intended changes, and
      `appsettings.Development.json` does **not** appear in it.
- [ ] Exactly one commit, K7 style.

If the count differs from 14, stop and report it rather than adjusting
tests to match.

---

## 6. What to report back

Keep it short and factual. This report is reviewed by another agent.

1. The full output of the final `dotnet test` run.
2. `git show --stat HEAD` and your commit message.
3. Anything in the plan that looked wrong, ambiguous, or that you had to
   guess at — **name it even if your tests pass.** A silent guess is far
   more expensive to find later than a flagged one.
4. Anything you changed beyond §4, and why.

---

## 7. Out of scope — do not do these

- Do not start Task 5, 6 or 7. One task per handoff.
- Do not run the application (`dotnet run`) or any server. Tests only.
- Do not edit anything under `C:\TBuild\tdesktop\` — those documents are
  the shared cross-project contract and are updated separately.
- Do not modify `scripts\db-bootstrap.ps1`, `PROGRESS.md`, or existing
  migrations.
- Do not "improve" code from earlier tasks that you happen to read. If
  something looks wrong, report it under §6.3 instead.
- Do not add packages beyond what the plan calls for.
