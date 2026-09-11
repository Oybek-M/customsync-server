# Implement holati — bu fayldan boshlang

Oxirgi yangilanish: **2026-09-11**

> Bu fayl `customsync-server` ichidagi ish holatini kuzatadi.
> Protokol holati (barcha loyihalar bo'ylab) — `tdesktop/docs/sync-protocol/STATUS.md`.

**Hozir:** `dotnet test` → **135 test, hammasi o'tadi**. `dotnet build` → 0 warning.
Branch `Oybek`, ish daraxti toza.

---

## 0. 🆕 2026-09-03 — plan 06 Task 1–4 va tozalash

> Bu bo'limni **tdesktop sessiyasi** yozdi. Siz plan 02 ustida ishlayotgan
> bo'lsangiz, quyidagilar sizning ishingizga tegmaydi — faqat bilib
> turing, adashmaslik uchun.

**Plan 06 Task 1–4 bajarildi** (Gemini, 4 commit: `a1e7161`, `c38350e`,
`1660229`, `d6a7fe4`). Reliz yuklash API'si: `Modules/Releases` emas,
`Services/Releases/` + `Api/Endpoints/ReleaseEndpoints.cs`.

Klient tomoni (Task 5) tdesktop repo'sida allaqachon tayyor va
sinovdan o'tgan: `tools/publish/release-api.ps1`.

### Men (tdesktop sessiyasi) nima qildim

**1. To'rtta o'lik fayl o'chirildi** (`git rm`):

```
src/CustomSync.Data/Entities/Release.cs          class Release : ReleaseEntity {}
src/CustomSync.Data/Entities/ReleaseMirror.cs    class ReleaseMirror : ReleaseMirrorEntity {}
src/CustomSync.Data/Entities/UploadSession.cs    class UploadSession : UploadSessionEntity {}
src/CustomSync.Api/Modules/Releases/ReleaseController.cs   const string ModuleName
```

Ular hech qayerda ishlatilmasdi — `DbSet` lar `*Entity` klasslarga
qaraydi. Gemini rejadagi "File Structure" ro'yxatini fayl nomlari
darajasida takrorlab yaratgan edi.

`Release : ReleaseEntity` shunchaki keraksiz emas: kimdir uni modelga
qo'shsa, **EF Core buni meros deb hisoblab discriminator ustuni
yaratadi** va migratsiya buziladi.

O'chirilgandan keyin: `dotnet build` → 0 warning, `dotnet test` →
109/109. Ya'ni o'lik ekani kompilyator bilan tasdiqlandi.

`src/CustomSync.Api/Modules/` papkasi bo'shab qoldi va o'chirildi.
`.gitignore` dagi `!src/**/[Rr]eleases/` qatoriga **tegilmadi** — u
`Services/Releases/` uchun kerak (25–26-qatorlardagi .NET build
chiqishi ignore'ini bekor qiladi).

### ⚠️ Ochiq qolgan test bo'shlig'i

`Upload_ResumesAfterInterruption` testida "uzilish" — bo'laklar
orasida shunchaki to'xtash, ya'ni **toza chegarada**. Server hech
qachon **yarim yozilgan bo'lak** holatini boshdan kechirmaydi.

Aynan shu holat tdesktop klientidagi haqiqiy xatoni ochgandi:
dastlabki klient uzilgan bo'lakni ayni offsetdan qayta yuborardi va
416 olib butunlay yiqilardi.

Server kodi bu holatga **to'g'ri yozilgan** — `WriteChunkAsync` da
`finally` bloki `Received` ni `FileInfo.Length` dan yangilaydi —
lekin sinalmagan. Bitta test qo'shilsa yopiladi: oqimni bo'lak
o'rtasida uzish, so'ng `GET` yarim sonni qaytarishini va keyingi
`PUT` o'sha offsetdan qabul qilinishini tekshirish.

## 1. Qilingan ishlar

### Plan 01a — Backend poydevori ✅ TO'LIQ TUGADI

| Task | Commit | Natija |
|---|---|---|
| 1 — Solution skeleti | `ccb1d89` | 5 loyiha (Api→Services→Data→Core), `/api/v1/health` |
| 2 — `RecordId` + kontraktlar | `c028de3` | `test-vectors.json` bilan tekshirilgan |
| 3 — PostgreSQL sxemasi | `6acd969` | 10 jadval, ustunlar snake_case |
| 4 — `SettingsService` | `86e5377` | Runtime konfiguratsiya (K1 mexanizmi) |
| 5 — Qurilma ro'yxati | `00d110c` + `74f9f54` | Atomar enrollment kod, rotatsiyalanuvchi refresh token |
| 6 — JWT endpoint'lari | `e709bee` | JWT issuer, device/settings endpoint'lari |
| **6b — Avtorizatsiya rollari** | `59f2de7` + `57eb74d` | **Rejadan tashqari.** `device`/`admin` roli, darhol bekor qilish keshi |
| 7 — Serilog + audit log | `cb0c09c` + `45412f2` | Audit actor alohida ustunda |

### Plan 01b — Sync yadrosi ✅ TO'LIQ TUGADI

| Task | Commit | Natija |
|---|---|---|
| 1 — Cursor va monotonlik | `f476520` + `725174f` | `seq` qulflangan hisoblagichdan; dedup konflikt testlari |
| 2 — Push/pull endpoint'lari | `fab4487` + `466e8da` | Tombstone ikki yo'nalishli |
| 3 — Media saqlash | `4dea7ba` | Kontent-adresli bloblar, kvota → 507 |
| 4 — Kalit o'ramlari | `dad9c9f` + `f3f3ac2` | Qurilma bo'yicha rate limiting |
| 5 — Keyset pagination + statistika | `f25b5d0` + `41fc3b6` | K3 keyset, `seq` tiebreaker |
| 6 — WebSocket bildirishnoma | `3d27b48` | `{"type":"changes","seq":N}` signali |
| 7 — `.cmx` almashuv formati | `ebe9a5a` | Import `PushAsync` orqali o'tadi |
| 8 — Platformalararo vektorlar | `d1afa39` | Beshala oila .NET da tekshiriladi |
| 9 — Deployment | `2ee8970` + `.gitattributes` | systemd `Type=exec`, nginx WS `map`, deploy README |

### Plan 04 — Storage lifecycle 🟡 JARAYONDA (3/6)

Har task Gemini'ga prompt bilan berildi va hisobotiga ishonilmasdan
qayta tekshirildi: `dotnet test` mustaqil yurgizildi, har
implementatsiya **delegate'nikidan boshqa** usulda ataylab buzilib
testlar bo'sh emasligi isbotlandi. Uchala task'da ham xato topildi.

| Task | Commit | Natija | Tekshiruvda topilgan va tuzatilgan |
|---|---|---|---|
| 1 — Xotira metrikasi | `f0268d4` + `4b4162f` | `StatsService.SummaryAsync`; o'sish kuzatilgan oynaga bo'linadi va media'ni ham sanaydi | `DaysUntilFull` int'ga sig'masdan **manfiy** bo'lardi (1 TB / 16 bayt/kun → -1924509447 kun) |
| 2 — Retention siyosatlari | `9f9b7e6` + `88b635d` | `RetentionPolicyEntity` + migratsiya; sof `RetentionEvaluator`; `retention.min_days` = 30 pol | `never_delete` himoyasi o'z **yosh oynasiga** bog'langan edi — keng qoida himoyalangan peer'ning 90–365 kunlik arxivini jimgina o'chirardi |
| 3 — Arxiv target seam | `146e58d` + `338ce0c` | `IArchiveTarget` + `RequiresExplicitConfirmation`; `ManualDownloadTarget` | `DeleteStagedAsync` ro'yxatda ko'rinmaydigan pastki papkalarga yetardi; `ValidateRoots` media↔staging joylashuvini faqat bir tomondan tekshirardi |

Planning o'zidan chetlashishlar (sabablari prompt fayllarida:
`tdesktop/docs/superpowers/plans/04-task{1,2,3}-prompt.md`):

- **Task 1:** yangi `StorageMetricsService` yaratilmadi — `StatsService`
  ikkala metodni allaqachon implement qilgan edi. Plandagi o'sish
  formulasi doim 7 ga bo'lardi va media'ni sanamasdi.
- **Task 2:** plan 01a dan beri seed qilingan `retention.<kind>_days`
  sozlamalari **hech qayerda o'qilmas edi**. Endi ular evaluator ichida
  eng past ustuvorlikdagi yashirin siyosat — ikkinchi, raqobatlashuvchi
  mexanizm paydo bo'lmadi. Plan migratsiyani va testlarni unutgan edi.
- **Task 3:** planning interfeysi "qayta o'qish = zaxira xavfsiz" deb
  hisoblardi. Qo'lda yuklab olishda arxiv **o'sha diskda** turadi,
  ya'ni bu yolg'on — shuning uchun `RequiresExplicitConfirmation`
  qo'shildi. Path traversal va xato holatidagi yarim fayl ham tuzatildi.

---

## 2. 🔴 KEYINGI QADAM — plan 04 Task 6

### Kelishilgan tartib (2026-09-09)

    04 -> 05 -> 03 -> read_at -> TO'LIQ DEPLOY

Foydalanuvchi qarori. Dastlab 05 dan boshlash rejalashtirilgan edi;
plan 05 ning o'z kirish sharti (*"04 majburiy — xizmat doimiy ma'lumot
oqimi hosil qiladi va xotira boshqaruvisiz disk tez to'ladi"*) topilgach,
04 oldinga olindi.

⚠️ **Deploy oxirida — bu ongli qaror, xavfi yozib qo'yilgan.**
Klient bilan server hali hech qachon gaplashmagan: 135 server testi ham,
C++ selftest ham faqat o'z tomonini tekshiradi, `test-vectors.json` esa
HTTP xulqini (enroll, bir martalik refresh token, cursor semantikasi,
403, WebSocket handshake) qoplamaydi. Plan 05 shu kontraktning C# dagi
ikkinchi implementatsiyasi bo'ladi — nomuvofiqlik chiqsa, u ikkala kod
bazasida birdan topiladi. Deploy'gacha quyidagilar **bloklangan**:
qo'lda regressiya ro'yxatining 2–3-bo'limi, `_inFlight` watchdog, kalit
ulashish oqimi, WebSocket bildirishnomasi.

### Plan 04 ichidagi tartib: 3 -> 6 -> 7

| Task | Holat | Nima uchun shu o'rinda |
|---|---|---|
| 1, 2, 3 | ✅ | yuqoridagi jadval |
| **6 — ikki fazali o'chirish** | ⚪ **KEYINGISI** | 05 ni aslida shu bloklaydi: diskni to'lishdan saqlaydigan yagona narsa |
| 7 — rejalashtirilgan ishlar | ⚪ | usiz retention o'z-o'zidan hech qachon ishga tushmaydi |
| 4 — S3 / SFTP target | ⏸ keyinga | xavfsizlik qo'shmaydi, faqat manzil. Task 3 seam'i tufayli o'chirish oqimiga tegmasdan keyin qo'shiladi |
| 5 — Telegram bot target | ⏸ keyinga | xuddi shu sabab |
| 8 — Web UI | → plan 03 | Task 1-3 dagi kabi |

🔴 **Bu tartibning oqibatini bilib qo'ying:** `ManualDownloadTarget`
arxivni **o'sha VPS diskiga** yozadi va uning tekshiruvi — inson
tasdig'i. Ya'ni u joy bo'shatmaydi va **avtomatik ishlay olmaydi**.
3 → 6 → 7 dan keyin avtomatik himoya faqat `delete_only` siyosatlari
orqali ishlaydi — bu `activity` uchun maqbul (mijoz 30, server 90 kun
saqlaydi). Avtomatik `archive_then_delete` kerak bo'lsa — Task 4 majburiy,
chunki arxiv serverdan tashqariga chiqishi kerak.

### 🔴 Plan 05 ga kelganda hal qilinadigan: `libtdjson`

Qaror KEYINGA qoldirildi (2026-09-09), lekin tahlil qilingan:

**Windows build KERAK EMAS.** Capture xizmati VPS'da (Ubuntu)
ishlaydi, ya'ni `libtdjson.so` (linux-x64) kerak. Laptopda build
qilinsa `tdjson.dll` chiqadi — u faqat lokal ishlab chiqishga
yaraydi, deploy'ga emas.

Build og'irligi: ~8 GB RAM, `-j2` bilan 30-60+ daqiqa. Bu
foydalanuvchining og'ir-build taqig'iga tushadi.

Uchta yo'l, arzonidan boshlab:

1. **Tayyor native NuGet paketi** — agar `linux-x64` ni qoplasa,
   build umuman kerak emas. Ishlatishdan oldin nashr qiluvchisi va
   versiyasi tekshirilsin: bu akkauntga ulanadigan kutubxona.
2. **VPS'da build** — swap qo'shib. Sekin, lekin to'g'ri artefakt
   darhol kerakli joyda chiqadi va laptopga tegmaydi.
3. **Docker'da build** (mashinada Docker bor) — Linux `.so` ni
   laptopda olish, konteynerga CPU/RAM chegarasi bilan.

Yana ikkita to'siq, ikkalasini ham FOYDALANUVCHI hal qiladi:
`api_id`/`api_hash` (my.telegram.org, repoga commit qilinmaydi) va
telefon -> kod -> 2FA login. Agent bularni bajarmaydi.

TDLib faqat ishga tushganda kerak (P/Invoke runtime'da bog'lanadi),
kompilyatsiya uchun emas — ya'ni Task 1 ning skeleti va interop
qatlami `.so` siz ham yozilishi mumkin.

### Serverda qolgan, planga bog'liq bo'lmagan ish

`read_at` konflikt qoidasi hali spec §0 ga yozilmagan va `message`
kind payload sxemasida yo'q. Batafsil:
`docs/a17-read-at-sync-requirement.md`. Server kodiga hozir tegmaydi.

---

## 3. 🔴 Buzmaslik kerak bo'lgan narsalar

Bular implement paytida topilgan xatolarning tuzatilgan holati.
Har biri jimgina ma'lumot yo'qotadigan turdan edi.

- **`o.Events` ni QAYTA TAYINLAMANG** (`Program.cs`). Unda ikkita
  handler bor: `OnTokenValidated` (bekor qilingan qurilma) va
  `OnMessageReceived` (WebSocket query-string token). Yangi
  `JwtBearerEvents` obyekti ikkalasidan birini yo'q qiladi.
- **Tombstone ikki yo'nalishli.** Tombstone kelganda nishon o'chiriladi,
  VA nishon keyinroq kelganda saqlanmaydi (`UpsertSql` dagi
  `NOT EXISTS`). Ikkinchisisiz o'chirish jimgina bekor bo'ladi.
- **`RedeemAsync` atomar** — shartli `ExecuteUpdateAsync` tranzaksiya
  ichida. Oddiy o'qish-keyin-yozishga aylantirmang: bir martalik kod
  ikki marta ishlatiladigan bo'lib qoladi.
- **`SettingsService.Cache` connection string bo'yicha ajratilgan.**
  Bitta jarayonda bir nechta baza bo'ladi (har test klassi o'z
  vaqtinchalik bazasini oladi).
- **`DeviceRevocationCache` — singleton.** Scoped bo'lsa butun
  mexanizm ishlamaydi.
- **Ustunlar snake_case** (`EFCore.NamingConventions`). Yangi
  `DbContext` qurilgan HAR joyda `.UseSnakeCaseNamingConvention()`
  bo'lishi shart (`Program.cs` va `DatabaseFixture.cs`).

---

## 4. 🔴 Testlar haqida — uch marta yiqilgan xatolar

- **`pull?since=0` ISHLATMANG.** Dev bazasi umumiy va 500 qatordan
  oshgan; yozuv birinchi sahifadan chiqib ketadi va test tasodifiy
  yiqiladi. Pull'ni o'z push'ingiz qaytargan `seq` dan boshlang —
  namuna: `SyncEndpointsTests.SinceBeforePush`.
- **Global sanoqqa assertion qo'ymang.** `WebApplicationFactory`
  umumiy dev bazasiga ulanadi va unda oldingi tasklarning yozuvlari
  bor. Har testga o'z `peer_hash`i (GUID) berilsin va faqat o'ziga
  qaralsin.
- **Sozlamani o'zgartirgan test uni `try/finally` bilan qaytarsin.**
  Aks holda keyingi testlar tartibga qarab buziladi.
- **`IClassFixture<DatabaseFixture>` bitta bazani butun klassga**
  baham qiladi, metodga emas.

---

## 5. Ochiq qolgan mayda ishlar

Hech biri bloklamaydi, lekin unutilmasin:

| Nima | Qayerda hal qilinadi |
|---|---|
| `sync.push_max_bytes` sozlamasi mavjud, lekin qo'llanilmaydi | 01b keyingi revizyasi |
| Media PUT/GET blobni butunlay xotiraga yuklaydi (50MB) | Plan 04 yoki 09 |
| `record_media` da yetim qatorlar (tombstone o'chirgach qoladi, FK yo'q) | Plan 04 (storage lifecycle) |
| Server eksporti media bloblarni o'z ichiga olmaydi | Ataylab; spec §0.7 |
| Revocation keshi bitta jarayonga tegishli | Plan 04/06 — `LISTEN/NOTIFY` |
| `auth.wrap_rate_per_hour` da 0 ≠ cheksiz (xavfsiz standart 5) | Ataylab; kodda izohlangan |
| 🔴 `RequiresExplicitConfirmation` hali hech kim tomonidan **qo'llanmaydi** — Task 3 faqat bayroqni qo'shdi | **Plan 04 Task 6** — tasdiqsiz qo'lda arxivdan keyin o'chirish rad etilishi SHART |
| Staging papkasining o'zi cheksiz o'sadi; avtomatik tozalash ataylab yo'q (yuklab olinmagan arxiv — yagona nusxa) | Plan 04 Task 6 yoki 7 |
| `never_delete` siyosatida `OlderThanDays` endi ma'nosiz — formada yashirilmasa operator uni ishlayapti deb o'ylaydi | Plan 03 (UI) |
| `SummaryAsync` agregatsiyasi `StorageAsync` bilan takrorlanadi | Plan 04 Task 6 ga qo'shib, bir qatorlik tozalash |
| `ArchiveTargetTests.Test1` SHA-256 ni asl kontentdan hisoblaydi — checksum diskdan emas, kirish oqimidan olinsa ham o'tadi | Buzilgan fayl tizimi simulyatsiyasi kerak; hozircha oqlanmaydi |

---

## 6. Auth modeli — qisqacha

- JWT'da `role` claim'i: `device` yoki `admin`. Oq ro'yxat
  `DeviceService.IsValidRole` da.
- **`device` uchun ochiq:** `/sync/push`, `/sync/pull`, `/media/*`,
  `/ws/notify`, `/keys/wraps` (o'qish), o'zini `DELETE` qilish.
- **Faqat `admin`:** `/settings`, `/records`, `/stats`, `/import`,
  `/export`, `/devices` boshqaruvi, `/keys/wraps` yaratish va o'chirish.
- Bekor qilish `OnTokenValidated` da xotiradan tekshiriladi — sync
  hot-path'ga DB so'rovi tushmaydi.

---

## 7. Muhit — tekshirilgan faktlar

| Nima | Holat |
|---|---|
| .NET SDK | 8.0.405 **va** 10.0.400 → `global.json` 8.0.x ga qadaydi |
| PostgreSQL | 17.2 ishlab turibdi (plan 16 deydi — muammo emas) |
| `dotnet-ef` | global tool 9.0.1, EF Core 8 bilan ishlaydi |
| `test-vectors.json` | `C:\TBuild\tdesktop\docs\sync-protocol\test-vectors.json` |
| pg auth | `scram-sha-256` — parolsiz kirish yo'q |

🔴 **Paket qo'shganda ALBATTA `--version 8.0.*`** — versiyasiz
`dotnet add package` net10.0 uchun qurilgan paketni oladi va `NU1202`
beradi. Bu loyihada besh marta uchragan.

Connection string va `Jwt:SigningKey` —
`src\CustomSync.Api\appsettings.Development.json` da (gitignore'da).
`db-bootstrap.ps1` ni **qayta** ishga tushirish rolga yangi parol
qo'yadi va faylni qayta yozadi.

---

## 8. Plandan chetlashishlar (sabab bilan)

Bular plan matnida yo'q — ataylab qilingan, orqaga qaytarmang.

1. **`global.json`** SDK 8.0.x ga qadaydi (mashinada 10.0 ham bor).
2. **EF Core 8.0.11 ga tenglashtirilgan** — `Design` paketi `8.0.*`
   bilan 8.0.30 ni olardi, Npgsql esa 8.0.11 ga bog'liq (MSB3277).
3. **`Mvc.Testing` `8.0.*`** — versiyasiz 10.x olinardi.
4. **`RecordKind` da 8 ta kind, planda 6 ta** — `media_index` va
   `tombstone` spec §0.6 dan.
5. **Testlar `test-vectors.json` ni o'qiydi**, nusxa olinmagan.
6. **`appsettings.Development.json` gitignore'da.**
7. **`SettingsService.Defaults` → `CreateDefaults()` metodi** —
   static ro'yxat `UpdatedAt` ni muzlatib qo'yardi.
8. **`SettingsService.Cache` connection string bo'yicha ajratilgan.**
9. **`DatabaseFixture` parolni `appsettings.Development.json` dan
   o'qiydi**, plandagi `CHANGE_ME` literali o'rniga.
10. **Ustunlar snake_case** — 01b raw SQL uchun.
11. **`record_id` da `account_hash`** (spec §0.12), **tombstone
    nishoni ochiq maydonda** (spec §0.13).
12. **`audit_log.actor_device_id` alohida ustun**, JSON ichida emas.
13. **`/records`, `/stats`, `/import`, `/export` — admin** (planda
    bo'sh `RequireAuthorization()` edi).
14. **Rate limiter qurilma bo'yicha partitsiyalangan** (planda
    partitsiyasiz, ya'ni hammaga bitta chelak edi).

---

## 9. Protokol o'zgarishlari (tdesktop repo'sida)

Implement paytida ikkita protokol kamchiligi topildi va spec'ga yozildi.
tdesktop `Oybek` branch, commit `5c49103942`:

- **§0.12 — `account_hash`.** `record_id` da akkaunt o'lchovi yo'q edi;
  ikki akkaunt `(peer_hash, msg_id)` bo'yicha to'qnashib, serverda
  bir-birining ustiga yozardi. `activity` kind uchun `account_hash=""`
  (last-seen bypass akkauntlar bo'ylab birlashishi kerak).
- **§0.13 — tombstone nishoni ochiq maydonda.** §0.3 serverdan
  `payload.target_record_id` ni o'qishni talab qilardi, lekin `payload`
  shifrlangan.

`test-vectors.json` qayta generatsiya qilindi (`record_id` 7→11 holat,
`account_hash` yangi bo'lim). Generator va fayl mosligi tekshirilgan.

---

## 10. Hujjatlarni o'qish tartibi (yangi sessiya)

Nusxa yo'q — hammasi `C:\TBuild\tdesktop\docs\` da yagona nusxada.

```
1. Shu fayl (PROGRESS.md)
2. sync-protocol/STATUS.md          -- kim nimani bajardi
3. sync-protocol/CHANGELOG.md       -- protokolda nima o'zgardi
4. superpowers/specs/2026-07-29-multi-device-sync-backend-design.md
     -> §0 REVIZIYA dan boshlang, u asosiy matndan USTUN
5. superpowers/plans/2026-07-29-multi-device-sync-00-index.md
     -> K1-K7 qoidalari
```

`docs/` ichida har task uchun tayyorlangan prompt fayllari bor
(`01b-task*-prompt.md`) — ularda plan matnidagi eskirgan joylar va
tuzatishlar yozilgan.
