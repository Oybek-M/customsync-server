# Implement holati — bu fayldan boshlang

Oxirgi yangilanish: **2026-08-26**

> Bu fayl `customsync-server` ichidagi ish holatini kuzatadi.
> Protokol holati (barcha loyihalar bo'ylab) — `tdesktop/docs/sync-protocol/STATUS.md`.

---

## Qayerdamiz

Plan **01a — Backend poydevori**, 7 ta task'dan 5 tasi tugadi.

| Task | Holat | Izoh |
|---|---|---|
| 1 — Solution skeleti | ✅ commit `ccb1d89` | 5 loyiha, `/api/v1/health` |
| 2 — `RecordId` + kontraktlar | ✅ commit `c028de3` | 9 test, `test-vectors.json` bilan tekshirilgan |
| 3 — PostgreSQL sxemasi | ✅ commit `6acd969` | 10 jadval, ustunlar snake_case |
| 4 — `SettingsService` | ✅ commit `86e5377` | 4 test, `Program.cs` ga ulandi (Step 6) |
| 5 — Qurilma ro'yxati | ✅ commit `00d110c` + `74f9f54` | 6 test; atomar redeem va deviceId tuzatildi. JWT qismi Task 6 da |
| 6 — JWT endpoint'lari | ✅ commit `7daa10a` | JWT issuer, auth middlewares va device/settings endpoints (4 ta integratsion testlar) |
| 7 — Serilog + audit log | ⚪ | |

`dotnet test` hozir: **27 test, hammasi o'tadi**.

🔴 **2026-08-26: `record_id` ga `account_hash` qo'shildi (spec §0.12,
commit `04cb174`).** Ko'p akkaunt aralashuvi tuzatildi. `activity`
kind uchun `account_hash=""` (akkauntlar bo'ylab birlashadi),
qolgan barcha kind haqiqiy hash oladi. `test-vectors.json` qayta
generatsiya qilindi, `RecordId.Compute` 5 argument oladi endi
(`accountHash` qo'shildi). Task 5 dan boshlaganda `RecordId.Compute`
chaqiruvlari shu yangi signaturani kutadi.

🔴 **2026-08-26: ustunlar `snake_case` (commit `6acd969`).**
`EFCore.NamingConventions` + `.UseSnakeCaseNamingConvention()`.
Sabab: plan 01b sync hot-path'ni **raw `NpgsqlCommand`** bilan
yozadi; EF standarti bilan har so'rovda `"PeerHash"` deb qo'shtirnoq
kerak bo'lardi va bitta unutilgani runtime xato berardi.

⚠️ **Migratsiyalar birlashtirildi.** Ikki eski migratsiya
(`InitialSchema` + `AddAccountHash`) o'chirildi, o'rniga bitta yangi
`20260826114427_InitialSchema`. Hech narsa deploy qilinmagani va
bazada faqat qayta hosil bo'ladigan seed ma'lumot bo'lgani uchun
xavfsiz edi. **Bundan keyin migratsiyalarni birlashtirmang** —
birinchi deploydan keyin bu yo'l yopiladi.

Yangi `DbContext` qurilgan HAR joyda `.UseSnakeCaseNamingConvention()`
bo'lishi shart (`Program.cs` va `DatabaseFixture.cs` — ikkalasi mos
bo'lmasa testlar boshqa sxemaga qarshi ishlaydi).

**`SettingsService.Defaults` → `CreateDefaults()` metodi.** Static
ro'yxat `UpdatedAt` ni klass yuklanganda muzlatib qo'yardi va bir xil
entity instance'larini har `DbContext`ga berardi.

---

## 🔴 KEYINGI QADAM — Task 7: Serilog va audit log

Plan 01a, Task 7. Muhit tayyor, to'siq yo'q.

---

## Task 4 dan qolgan ikkita muhim eslatma

**1. `SettingsService.Cache` connection string bo'yicha ajratilgan,
plan matnida bitta flat static dictionary edi.** Bitta test jarayonida
bir nechta baza bo'lishi mumkin (har test klassi o'z vaqtinchalik
bazasini oladi) — flat static kesh ularni bulg'ab qo'yardi, ayniqsa
`Program.cs` Step 6 dan keyin `HealthTests` ham o'z (haqiqiy dev)
bazasiga `EnsureDefaultsAsync` chaqira boshlagach. Keyingi tasklarda
`SettingsService` ga tegilganda shu naqshni saqlang: `Cache` — instance
property, `db.Database.GetConnectionString()` bo'yicha `CachesByDatabase`
dan olinadi.

**2. `SettingsServiceTests` da `IClassFixture<DatabaseFixture>` bitta
bazani butun klassga baham qiladi (metodga emas!).** Plan izohi "Har
test alohida baza" deydi, lekin kod bloki (`IClassFixture`) buni
bermaydi — bu plan matnidagi ziddiyat, kod ustun deb hisoblandi.
Bazani mutatsiya qiladigan har qanday yangi test oxirida qiymatni
asl holiga qaytarishi shart, aks holda boshqa testlar tartibga qarab
buziladi (`Set_then_get_returns_new_value_without_restart` da
`try/finally` bilan namuna bor).

---

## Baza — tayyor (2026-08-25)

`scripts\db-bootstrap.ps1` ishga tushirildi, migratsiya qo'llandi.
10 ta jadval, hammasi `customsync` egaligida:
`devices`, `records`, `media_blobs`, `record_media`, `key_wraps`,
`server_settings`, `enrollment_codes`, `audit_log`, `sync_counter`,
`__EFMigrationsHistory`.

Connection string `src\CustomSync.Api\appsettings.Development.json`
da (gitignore'da). `dotnet ef` `ASPNETCORE_ENVIRONMENT` ni o'zi
`Development` ga qo'yadi, shuning uchun u shu fayldan o'qiydi —
`appsettings.json` dagi `CHANGE_ME` ishlatilmaydi.

Skriptni **qayta** ishga tushirish rolga YANGI tasodifiy parol qo'yadi
va sozlama faylini qayta yozadi. Lokalda zararsiz (ikkalasi birga
yangilanadi), lekin deploy qilingan muhitga qarshi ishlatmang.

---

## Hujjatlarni o'qish tartibi (yangi sessiya)

Nusxa yo'q — hammasi `C:\TBuild\tdesktop\docs\` da yagona nusxada.

```
1. sync-protocol/STATUS.md          -- kim nimani bajardi
2. sync-protocol/CHANGELOG.md       -- protokolda nima o'zgardi
3. superpowers/specs/2026-07-29-multi-device-sync-backend-design.md
     -> §0 REVIZIYA dan boshlang, u asosiy matndan USTUN
4. superpowers/plans/2026-07-29-multi-device-sync-00-index.md
     -> K1-K7 qoidalari
5. superpowers/plans/2026-07-29-multi-device-sync-01a-backend-foundation.md
     -> boshidagi "REVIZIYA 2026-08-25" blokini albatta o'qing
```

---

## Muhit — tekshirilgan faktlar

| Nima | Holat |
|---|---|
| .NET SDK | 8.0.405 **va** 10.0.400 o'rnatilgan → `global.json` 8.0.x ga qadaydi |
| PostgreSQL | **17.2** ishlab turibdi (plan 16 deydi — sxemada 16-ga xos narsa yo'q, muammo emas) |
| `dotnet-ef` | global tool 9.0.1 — EF Core 8 loyihasida ishlaydi, tekshirildi |
| `test-vectors.json` | `C:\TBuild\tdesktop\docs\sync-protocol\test-vectors.json` |
| pg auth | `scram-sha-256` — parolsiz kirish yo'q |

Test vektorlari boshqa joyda bo'lsa:
`set CUSTOMSYNC_TEST_VECTORS=<to'liq yo'l>`

---

## Plandan chetlashishlar (sabab bilan)

Bular plan matnida yo'q — ataylab qilingan, orqaga qaytarmang.

1. **`global.json` qo'shildi** (SDK 8.0.405, `rollForward: latestFeature`).
   Mashinada 10.0 SDK ham bor; usiz `dotnet new` net10.0 ga ketardi.

2. **`Microsoft.EntityFrameworkCore.Design` 8.0.11 ga qadaldi.**
   `8.0.*` 8.0.30 ni olardi, Npgsql provider esa EF Core 8.0.11 ga
   bog'liq → MSB3277 versiya to'qnashuvi. Endi build 0 warning.

3. **`Microsoft.AspNetCore.Mvc.Testing` `8.0.*`** — versiyasiz
   `dotnet add package` 10.0.11 ni olib, net8.0 bilan mos kelmasdi.

4. **`RecordKind` da 8 ta kind, planda 6 ta.** `media_index` va
   `tombstone` spec §0.6 dan — revizya plan matnidan ustun.

5. **`RecordIdTests` `test-vectors.json` ni o'qiydi.** Plan buni
   so'ramagan, lekin `sync-protocol/README.md` "birinchi ish —
   vektorlarni qayta hosil qilish" deydi. Fayl nusxalanmagan; topilmasa
   test **yiqiladi** (jimgina o'tib ketmaydi).

6. **`appsettings.Development.json` gitignore'da**, parol
   `db-bootstrap.ps1` tomonidan generatsiya qilinadi.
   `appsettings.json` da `CHANGE_ME` placeholder qoladi.

7. **`SettingsService.Defaults` da 10 ta qo'shimcha kalit** (8 ta
   `retention.<kind>_days` + 2 ta `storage.quota_*`) — spec §0.3/§0.9
   dan, planning `Defaults` ro'yxatida yo'q edi.

8. **`SettingsService.Cache` connection string bo'yicha ajratilgan**
   (yuqoridagi "Task 4 dan qolgan eslatma 1" ga qarang). Plan matnida
   bitta flat static dictionary edi — bu ishlamas edi, chunki
   `Program.cs` Step 6 dan keyin turli baza (dev + har test klassining
   vaqtinchalik bazasi) bitta jarayonda birga yashaydi.

9. **`DatabaseFixture` da `Password=CHANGE_ME` o'rniga resolver.**
   Plandagi literal parol ishlamaydi; haqiqiysi
   `appsettings.Development.json` dan o'qiladi (5-band bilan bir xil
   sabab).

---

## Buzilmaydigan qoidalar (K1-K7 + protokol)

Batafsil: `00-index.md` va `sync-protocol/README.md`.

- **K1** — sozlanadigan HAR qiymat `server_settings` jadvalida.
  Kodda literal: sync interval, batch/sahifa hajmi, retention, disk
  chegarasi, rate limit, timeout, feature toggle — **taqiqlangan**.
- **K3** — offset pagination taqiqlangan, faqat keyset + `seq` snapshot.
- **K4** — yozish idempotent (`record_id` PRIMARY KEY).
- **K6** — TDD: avval yiqiladigan test, keyin minimal implementatsiya.
- **K7** — commit: imperativ sarlavha, tanada **nima uchun**.
  `Co-Authored-By` **yo'q**. Branch `Oybek`. `upstream` ga push yo'q.
- `sha256` — **ochiq matn** ustidan, shifrlashdan OLDIN (§0.5).
- Retention tombstone **yaratmaydi** (§0.3).
- Manfiy `msg_id` — ishora saqlanadi (§0.6).

---

## Sessiya oxirida

1. Bu faylni yangilang.
2. Protokolga tegdingizmi → `tdesktop/docs/sync-protocol/CHANGELOG.md`.
3. `tdesktop/docs/sync-protocol/STATUS.md` da `customsync-server` qatori.
