# Implement holati — bu fayldan boshlang

Oxirgi yangilanish: **2026-10-01**

> Bu fayl `customsync-server` ichidagi ish holatini kuzatadi.
> Protokol holati (barcha loyihalar bo'ylab) — `tdesktop/docs/sync-protocol/STATUS.md`.

**Hozir:** `dotnet test` → **352 test, hammasi o'tadi**. `dotnet build` → 0 warning.
Branch `Oybek`, ish daraxti toza.

### ▶️ Qayerda to'xtadik (2026-10-01, PC `DESKTOP-5CAUS66`)

| | |
|---|---|
| **Oxirgi tekshirib qabul qilingan** | **Plan 05 Task 6b — pull the owner's `setting` records into the capture scope** |
| **Keyingi bajariladigan** | **Plan 05 Task 7** |
| Undan keyin | Task 8–10, `photo` maydoni, sessiya himoyasi vazifasi |
| 🔴 DEPLOY TO'XTATILGAN | VPS 2026-09 da buzilgan (miner). Birgalikdagi to'liq xavfsizlik auditisiz VPS'ga hech narsa deploy qilinmaydi va ishga tushirilmaydi — pastdagi "Deploy oldidan xavfsizlik auditi" bo'limi |
| ✅ tdesktop javoblari (2026-09-29, `7db70efae8`) | (1) scope `setting` lar tdesktop'da GLOBAL, har startda har akkaunt nomidan qayta yuboriladi (spec §3.2.1a) → **6b qarori:** `account_hash` bo'yicha FILTRLANMAYDI, har kalit uchun eng katta `occurred_at` (teng bo'lsa `record_id`) g'olib; (2) master kalit faqat parol o'ramidan (spec §4.4.0) — vektorlarni 2026-09-30 da o'zim mustaqil tekshirdim (FP 3/3, unwrap 2/2, noto'g'ri parol rad etiladi) |
| Plan 05 holati | 1–2 ✅, 3 ✅, 4a ✅, 4b ✅, 5 ✅, 4c ✅, 6a ✅, 6a-2 ✅, 6b ✅, 7–10 ⏸ |
| Kelishilgan umumiy tartib | `04 → 05 → 03 → read_at → TO'LIQ DEPLOY` |
| ⚠️ PC muhiti | NuGet fallback papkasi E: da yo'qolgan — build yiqilsa §7 dagi aylanib o'tish |

2026-09-30 (kech): Task 6a-2 (Gemini, `8c27f60`) tekshirilib qabul qilindi —
2 nuqson va 4 test bo'shlig'i tuzatildi; hisobotdagi konsol dialogi
kodga mos emas edi (haqiqiysi tekshiruv bo'limida).

2026-09-30: Task 6a (Gemini, `aea2772`) tekshirilib qabul qilindi —
4 nuqson (ulardan 2 tasi productionda sync'ni butunlay to'xtatardi) va
6 test bo'shlig'i tuzatildi (pastdagi tekshiruv bo'limi).

2026-09-29: Task 4c (Gemini, `3b9b416`) tekshirilib qabul qilindi —
2 nuqson va 3 test bo'shlig'i tuzatildi (pastdagi tekshiruv bo'limi).

2026-09-27 qilinganlar: Task 4a, 5, 4b tekshirilib qabul
qilindi (har birida delegate o'tkazib yuborgan nuqsonlar tuzatildi);
Task 4b/4c/5 promptlari yozildi; `edited` protokol taklifi
(`docs/proposal-edited-edit-date.md`) tdesktop tomonida qabul qilindi;
activity protokol savollari (msg_id, `long_ago`, activity scope
kalitlari) yopildi.

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

### Plan 04 — Storage lifecycle 🟡 JARAYONDA (5/6)

Har task Gemini'ga prompt bilan berildi va hisobotiga ishonilmasdan
qayta tekshirildi: `dotnet test` mustaqil yurgizildi, har
implementatsiya **delegate'nikidan boshqa** usulda ataylab buzilib
testlar bo'sh emasligi isbotlandi. To'rtala task'da ham xato topildi va tuzatildi.

| Task | Commit | Natija | Tekshiruvda topilgan va tuzatilgan |
|---|---|---|---|
| 1 — Xotira metrikasi | `f0268d4` + `4b4162f` | `StatsService.SummaryAsync`; o'sish kuzatilgan oynaga bo'linadi va media'ni ham sanaydi | `DaysUntilFull` int'ga sig'masdan **manfiy** bo'lardi (1 TB / 16 bayt/kun → -1924509447 kun) |
| 2 — Retention siyosatlari | `9f9b7e6` + `88b635d` | `RetentionPolicyEntity` + migratsiya; sof `RetentionEvaluator`; `retention.min_days` = 30 pol | `never_delete` himoyasi o'z **yosh oynasiga** bog'langan edi — keng qoida himoyalangan peer'ning 90–365 kunlik arxivini jimgina o'chirardi |
| 3 — Arxiv target seam | `146e58d` + `338ce0c` | `IArchiveTarget` + `RequiresExplicitConfirmation`; `ManualDownloadTarget` | `DeleteStagedAsync` ro'yxatda ko'rinmaydigan pastki papkalarga yetardi; `ValidateRoots` media↔staging joylashuvini faqat bir tomondan tekshirardi |
| 6 — Ikki fazali xavfsiz o'chirish | `ce9a032` + `388d6f4` + `620214e` | `ArchiveRunEntity` + migratsiya; `PurgeService` (`PreviewAsync`, `ExecuteAsync`, `ConfirmAsync`, `SweepOrphanedMediaAsync`); yetim media 24h karantini; 17 ta xavfsizlik testi | 1) Plandagi xavfli `RetentionScope` o'rniga har bir yozuv `RetentionEvaluator.Evaluate` dan o'tkaziladi (`never_delete` buzilmaydi); 2) `tombstone` qatorlar mutlaqo o'chirilmaydi; 3) Faqat aynan arxivlangan versiya `(record_id, observed_at, device_id)` o'chiriladi (superseded push o'chib ketmaydi); 4) Yetim media nomzodlari faqat o'chgan yozuvlardan olinadi, `orphaned_at` 24h saqlanadi, `ExistsAsync` bayroqni tozalaydi, fayllar tranzaksiya commit'idan keyin o'chadi; 5) Arxiv diskdagi temp `.cmx` faylga yoziladi, xotirani to'ldirmaydi; 6) `RequiresExplicitConfirmation` targetlar faqat tasdiqdan keyin o'chiriladi; 7) Tekshiruvdagi K1-K3 va T1-T3 to'liq tuzatildi |
| 7 — Rejalashtirilgan arxiv joblari, dry-run va disk chegaralari | `95673b0` + `cf2e76e` | `ArchiveSchedule`, `IDiskProbe` + `SystemDiskProbe`, `archive_job_runs` jadvali + migratsiya, `ArchiveJobRunner`, `ArchiveJobService`, `CustomSyncWebApplicationFactory`; 17 ta test | **Tekshiruvda topilgan (2026-09-16):** sozlama qiymati buzuq bo'lsa (`warn_percent = "eighty"`) `int.Parse` butun fon xizmatini har daqiqada yiqitardi — tozalash butunlay va jimgina to'xtardi; `jobs_hour = 25` esa hech qanday signalsiz ishni o'chirib qo'yardi; partiya testi `<= 2` tekshirgani uchun sikl butunlay olib tashlansa ham o'tardi; testlar audit yozuvlarini bir-biridan meros qilib olardi. Delegate qilgan ishdan: 1) Test isolation: `CustomSyncWebApplicationFactory` orqali `ArchiveJobService` test muhitidan olib tashlandi; 2) Siyosatlar uchun alohida `IServiceScope` orqali nosozlik izolatsiyasi; 3) `disk_capacity_mb` int to'lib ketishidan himoyalandi; 4) Bekor qilish (cancellation) xatolik sifatida loglanmasligi ta'minlandi; 5) 6 ta ataylab buzish (breaks a–f) sinovdan o'tdi |

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
- **Task 6:**
  - Plandagi `RetentionScope` kodi butunlay rad etildi, o'rniga har nomzod
    `RetentionEvaluator.Evaluate` dan o'tkazilib, ustuvorlik va `never_delete`
    100% hurmat qilinishi ta'minlandi.
  - `kind = "tombstone"` qatorlari qat'iy chiqarib tashlandi — klientlar va
    upsert sinkxronizatsiyasi uchun tombstone abadiy saqlanadi.
  - O'chirish shartida plandagi `WHERE record_id IN (...)` o'rniga
    `(record_id, observed_at, device_id)` uchligi ishlatildi — bu tanlov
    va o'chirish orasida kelgan yangiroq versiyani o'chib ketishdan saqlaydi.
  - Yetim media boshqaruvi: plandagi "bazadagi barcha havolasiz bloblar"
    o'rniga faqat ayni shu purge paytida o'chgan yozuvlarga tegishli bloblar
    saralanadi, `orphaned_at` belgisi qo'yiladi va 24 soatdan keyin o'chiriladi.
    `MediaService.ExistsAsync` chaqirilsa `orphaned_at` NULL ga qaytariladi.
  - Arxiv `MemoryStream` da emas, diskdagi temp `.cmx` faylda yaratilib,
    `CmxReader` bilan qayta o'qilgach target'ga jo'natiladi.
  - `RequiresExplicitConfirmation` targetlarida run `awaiting_confirmation`
    holatiga o'tib kutadi, o'chirish faqat `ConfirmAsync` dan keyin bajariladi.
  - **Tekshiruv tuzatishlari (K1–K3, T1–T3):**
    - **K1:** `ConfirmAsync` kutish davrida qo'shilgan yangi `never_delete` yoki o'zgargan siyosatlarni chetlab o'tmasligi uchun yozuvlarni bazadan qayta yuklab, `FilterMatchedRecordsAsync` (`RetentionEvaluator`) orqali qayta tekshiradi.
    - **K2:** Yetim media o'chirish poygasi bartaraf etildi: tekshirish va o'chirish `SweepOrphanedMediaAsync` da alohida qisqa tranzaksiyada `LOCK TABLE record_media IN SHARE MODE` va atomar `DELETE FROM media_blobs mb WHERE mb.orphaned_at IS NOT NULL AND mb.orphaned_at <= @cutoff AND NOT EXISTS (SELECT 1 FROM record_media rm WHERE rm.hash = mb.hash) RETURNING mb.storage_path` orqali bajariladi. Fayllar diskdan faqat commit'dan keyin o'chadi.
    - **K3:** Nomzodlar ro'yxatida `query.Take(limit)` sababli yuzaga keladigan ochlik (starvation) bartaraf etildi: keyset pagination (`received_at, seq`) yordamida `MaxScanLimit = 50_000` gacha skan qilinib, `limit` ta yaroqli yozuv to'planadi.
    - **T1:** `PolicyId == policyId` sharti olib tashlanganda quyi prioritetli `delete_only` yuqori prioritetli `archive_then_delete` yozuvini o'chirib yubormasligi test bilan qoplandi (`Test_T1`).
    - **T2:** Arxivga yozuvga bog'langan media bloblar kiritilishi, ularning baytlari, hajmi va nonce'lari mosligi hamda diskda yo'q media'lar `MissingMedia` sifatida to'g'ri sanalishi sinovdan o'tkazildi (`Test_T2`).
    - **T3:** Arxivdagi yozuvlar sonini solishtiruvchi `VerifyArchiveRecordCount` alohida ajratilib, nomuvofiqlikda `InvalidDataException` tashlashi unit test bilan qamrab olindi (`Test_T3`).
    - **Qo'shimcha:** `ConfirmAsync` da stream seek qilib bo'lmasa temp faylga ko'chirish, `UPDATE archive_runs` dagi atomar status tekshiruvi, `ExecuteAsync` dagi xatoliklarni `failed_verification`/`failed_upload` qilib saqlash va testlardagi global siyosatlarni `finally` da tozalash amalga oshirildi.
- **Task 7:**
  - **Fayllar bo'linishi:** BackgroundService ichidagi taymerni to'g'ridan-to'g'ri ishonchli testlab bo'lmaydi. Shu sababli sof reja mantiqi (`ArchiveSchedule.cs`), disk o'lchov interfeysi (`IDiskProbe.cs` + `SystemDiskProbe.cs`), to'liq testlanadigan runner (`ArchiveJobRunner.cs`) va yengil xost xizmati (`ArchiveJobService.cs`) alohida yaratildi.
  - **Qayta yuklash va ikki nusxadan atomar himoya:** In-memory bayroq har restartdan keyin qayta yurgizib yuborardi. Yangi `archive_job_runs` jadvali va `INSERT INTO archive_job_runs ... ON CONFLICT (run_date) DO NOTHING` atomar zaxiralash mexanizmi joriy qilindi.
  - **`disk_capacity_mb` (bayt emas):** Plandagi `disk_capacity_bytes int` ~2 GiB da integer overflow berardi. U mavjud `storage.quota_total_mb` ga mos ravishda megabaytlarda olindi.
  - **`storage.jobs_dry_run` sukut bo'yicha true:** Avval audit logda nimalar o'chirilishini ko'rish, real o'chirish uchun esa ikkinchi ongli qaror bilan sozlash imkoniyati berildi.
  - **`storage.jobs_max_batches`:** Bir siyosat bo'yicha cheksiz siklga kirib qolmaslik uchun har yurgizishda partiyalar soni chegaralandi.
  - **Implicit siyosatlar:** `retention.<kind>_days` bo'yicha ko'rsatilgan kunlar avtomatik ravishda pastki prioritetli siyosat sifatida deterministic ro'yxatga qo'shildi.
  - **Staged fayllar o'chirilmaydi:** Tasdiqlangan arxiv foydalanuvchi tomonidan yuklab olinganiga kafolat yo'qligi sababli, `DeleteStagedAsync` avtomatik chaqirilmaydi; bu qaror Plan 03 Web UI doirasida qoldirildi.
  - **StatsService optimallashtirish:** `SummaryAsync` va `StorageAsync` dagi takroriy agregatsiya umumiy `GetStorageCountsAndBytesAsync` metodiga birlashtirildi.

### Task 7 tekshiruvi (2026-09-16) — qanday qabul qilindi

Testlar mustaqil yurgizildi (165/165 tasdiqlandi), so'ng Gemini'nikidan
**boshqa** oltita mutatsiya qilindi. Birinchi urinishda oltitadan faqat
bittasi ushlandi:

| Mutatsiya | Dastlab | Tuzatgandan keyin |
|---|---|---|
| Kritik chegara `>=` -> `>` | o'tib ketdi (test 85/95 bilan sinardi, aniq chegarada emas) | `Test15` yiqitadi |
| Partiya sikli birinchi partiyadan keyin to'xtaydi | o'tib ketdi (`runs.Count <= 2` sikl yo'q bo'lsa ham rost) | `Test10` yiqitadi |
| `at_least` bayrog'i doim false | umuman sinalmagan | `Test16` yiqitadi |
| Staging hisoboti yozilmaydi | umuman sinalmagan | `Test16` yiqitadi |
| Ogohlantirish/kritik shohobchalari almashtirildi | ✅ ushlandi (`Test12`) | — |
| Siyosat sikli boshidagi bekor qilish tekshiruvi olib tashlandi | o'tib ketdi | ataylab shunday qoldirildi: ichkarida ikkinchi tekshiruv bor, ikkalasi ham zarur emas |

Kod o'qishdan topilgan uchta kamchilik (`cf2e76e` da tuzatildi):

1. **Sozlama qiymati buzuq bo'lsa job butunlay to'xtardi.** `SetAsync`
   tip tekshirmaydi — admin endpoint'i har qanday matnni yozadi.
   `storage.warn_percent = "eighty"` -> `int.Parse` kun band qilinishidan
   OLDIN istisno tashlardi, fon xizmati har daqiqada qayta urinardi va
   rejalashtirilgan tozalash **butunlay to'xtardi**, faqat log qatori
   qolardi. Endi qiymat `TryParse` bilan o'qiladi, xavfsiz standart
   qiymatga qaytadi (`jobs_enabled` -> false, `dry_run` -> true) va
   `archive_job.config_invalid` audit yoziladi.
2. **`jobs_hour = 25` jimgina o'chirib qo'yardi.** `IsDue` shunchaki
   `false` qaytarardi. Endi `ArchiveSchedule.TryResolve` sababini aytadi,
   xost xizmati `archive_job.schedule_invalid` yozadi va standart 03:30
   ga **qaytmaydi** — hech kim sozlamagan vaqtda o'chirish kutilmagan
   o'chirish demakdir.
3. **Testlar audit yozuvlarini bir-biridan meros qilib olardi.**
   `ResetStateAsync` `audit_logs` ni tozalamasdi, shuning uchun test
   qo'shni testning `dry_run` yoki `threshold_*` qatorini ko'rib "o'tdi"
   deb xulosa qilardi.

Bundan tashqari `EnsureDefaultsAsync` dagi `catch (DbUpdateException)`
faqat `unique_violation` (23505) bilan cheklandi — avval u sxema
nomuvofiqligini ham yutardi va sozlamalar jimgina yo'q bo'lib qolardi.

Ochiq qolgan, ongli qaror: kun `jobs_enabled = false` bo'lganda ham band
qilinadi — kun davomida sozlama yoqilsa, ish ertasiga boshlanadi.

---

### Task 6 tekshiruvi (2026-09-14) — qanday qabul qilindi

Uch bosqich. Har birida testlar mustaqil yurgizildi va Gemini'nikidan
BOSHQA mutatsiyalar qilindi.

| Bosqich | Commit | Topilgan |
|---|---|---|
| 1. Implement (Gemini) | `ce9a032` | 146/146. Mutatsiyalardan 3 tasi ushlanmadi: siyosat `PolicyId` sharti, arxivdagi media, arxiv yozuvlar soni. Kod o'qishdan: **K1** `ConfirmAsync` retention'ni qayta baholamasdi (kutish paytida qo'shilgan `never_delete` chetlab o'tilardi); **K2** yetim blob `DELETE` shartni qayta tekshirmasdi (poyga); **K3** nomzodlar evaluator'dan oldin `Take(limit)` — siyosat och qolardi. Prompt: `tdesktop/docs/superpowers/plans/04-task6-fixes-prompt.md` |
| 2. Tuzatish (Gemini) | `388d6f4` | 152/152. K1–K3, T1–T3 to'g'ri tuzatilgan. Yangi bo'shliq: K3 testi (limit 5, 7 yozuv) sahifa hajmi `Math.Clamp(limit, 100, 1000)` tufayli bitta sahifaga sig'ib, sahifalash sikli sinalmagan — "faqat birinchi sahifa" mutatsiyasi o'tib ketdi |
| 3. Yakuniy (Claude) | `620214e` | Pastki chegara 1 ga tushirildi; o'sha mutatsiya endi `Test_K3` ni yiqitadi. 152/152 |

Ma'lum, ongli qoldirilgan cheklovlar (Task 7 da e'tibor bering):

- Keyset skani har run eng eskidan boshlanadi va `MaxScanLimit = 50 000`
  da to'xtaydi. Eng eski 50 000+ yozuv himoyalangan bo'lsa, siyosat yana
  och qoladi. Task 7 cursor'ni run'lar orasida saqlashi mumkin.
- Ikki parallel `ConfirmAsync`: ikkalasi ham o'chiradi (faqat arxivlangan
  versiyalar — ma'lumot yo'qolmaydi), keyin holat yangilanishida biri
  istisno bilan tugaydi va `deleted_count` to'liq bo'lmasligi mumkin.
  To'liq yechim — avval run'ni `confirming` holatiga "egallash".
- Vaqtinchalik arxiv `Path.GetTempPath()` da. Ba'zi VPS'larda `/tmp` —
  tmpfs (RAM); deploy'da staging diskiga ko'chirishni o'ylang.
- `SweepOrphanedMediaAsync` public — Task 7 uni purge'dan mustaqil
  chaqirishi kerak (aks holda karantindagi bloblar faqat mos yozuv
  topilgan run'da tozalanadi). ✅ Task 7 da bajarildi.

---

### Plan 05 — Always-on capture service 🟡 JARAYONDA (1–5 + 4a/4b/4c + 6a/6a-2 tekshirildi; keyingi: 6b)

| Task | Commit | Natija | Tekshiruvda topilgan va tuzatilgan |
|---|---|---|---|
| 1 — TDLib interop va TdClient | `2fca499` | `CustomSync.Capture` worker service, `TdJsonInterop`, `NativeLibrary.SetDllImportResolver`, `ITdTransport`, `TdClient` (@extra correlation, timeout cleanup, TDLib error handling); 5 test | Native library mavjud bo'lmaganda ham build/test o'tishi ta'minlandi; timeout'da pending so'rovlar tozalanadi |
| 2 — Autentifikatsiya, preflight va redaction | `02c3a9d` + `8fb237c` | `TdAuthenticator` (interaktiv va xizmat rejimlari), `CapturePreflight`, `TdRedactor` (maxfiy ma'lumotlarni yashirish); 10 test (jami 15 ta capture testi, 184 umumiy test) | 1) `waitRegistration` va notanish holatlarda xatolik bilan to'xtash; 2) `use_secret_chats = false`; 3) maxfiy qiymatlarni loglarda hech qachon chiqarmaslik; 4) 7 ta ataylab buzish (a–g) tekshirildi. **Tekshiruvda topilgan (2026-09-17):** `TdRedactor` ishlab chiqarish kodida umuman chaqirilmasdi (o'lik himoya); xizmat avtorizatsiyani hech qachon tekshirmasdi va nol kod bilan chiqardi; qabul sikli xatoda kechikishsiz aylanardi; marshalling qatlami butunlay sinovsiz edi |
| 3 — Mahalliy xabarlar keshi (MessageCache) | `b456dae` + `4280548` | SQLite mahalliy kesh (`MessageCache`), `CachedMessage` (manfiy message_id, null text qo'llab-quvvatlaydi), `CacheStats`, `MessageCacheException`, `PeriodicCachePruner` (har 6 soatda tozalash, 30 kun retention), `CapturePreflight` kesh katalogi tekshiruvi; 15 test (jami 205 test) | Atomar `Put` avvalgi qatorni qaytaradi (`old_text` saqlanishi uchun), WAL va busy_timeout (5000ms), `PRAGMA user_version = 1`, `TimeProvider` (injectable clock), matn xavfsizligi (loglar va istisnolarda private text yo'qligi) kafolatlangan. **Tekshiruvda topilgan (2026-09-26):** `Get` ning chat izolyatsiyasi sinovsiz edi; `Worker` keshni `GetService` + `?.` bilan ulardi (ro'yxat yo'qolsa jimgina keshsiz ishlardi) va bu butunlay sinovsiz edi; tozalash sikli kuzatuvsiz `Task.Run` edi; `Stats` `-wal` faylini sanamasdi; tozalash birinchi marta faqat 6 soatdan keyin ishga tushardi. 7 yangi test, jami 212 |
| 4a — O'chirilgan va tahrirlangan xabarlarni saqlash (outbox) | `7d9b98f` + tekshiruv tuzatishlari | `capture_outbox` jadvali (SQLite, durativ outbox), `TdIdMapper` (peer << 48, msg_id >> 20), `PayloadBuilder` (BuildDeleted/BuildEdited UTF-8 mosligi, non-ASCII/emojilar saqlanadi), `NoneCaptureScope` (fail-closed placeholder), atomar delete+outbox tranzaksiyasi (`DeleteMessagesAndRecordOutbox`), ketma-ket qayta ishlash navbati va early queue; `AddCaptureHandlers` ishlab chiqarish simi; 17+8 test (jami 236) | 9 ta buzish (a–i) + 14 ta mustaqil mutatsiya; 6 ta nuqson tuzatildi |
| 5 — Capture scope: chatlar saralanishi (cache, delete, edit) | `640066e` + tekshiruv tuzatishlari | 4 qatlamli iyerarxiya (Server Block > Server Allow > Synced snapshot > DefaultEnabled); tdesktop zanjiriga mos 3 qaror (ShouldCache, ShouldAntiDelete, ShouldAntiEdit); ScopeSettingsSnapshot va ISyncedScopeSettingsSource choki; CapturePreflight decimal int64 va kesishtirmaslik tekshiruvi; privacy loglar (hech qanday peer_id loglanmaydi); 11 test (jami 247) | 10 ta ataylab buzish (a–j) ushlandi; DI da factory lambda orqali konstruktor noaniqligi bartaraf etildi; tdesktop background-edit nomuvofiqligi qayd etildi (server ShouldAntiEdit ga qaraydi) |
| 4b — Activity capture (status, name, username) | `457b92f` + tekshiruv tuzatishlari | Activity kuzatuvi (`updateUser`, `updateUserStatus`); tdesktop bilan baytma-bayt moslik (status holatlari, `langFullName`, username, discriminator SHA256[0:8]); 60s offline shovqin filtri; `activity_latest` kesh jadvali; 4 qatlamli `IActivityScope` va preflight Check 6; 12 yangi test (jami 261 test) | 10 ta ataylab buzish (a–j) to'liq tasdiqlandi; discriminator CustomSync.Capture ichida tdesktop bilan 100% mos qilindi; boshlang'ich kuzatuvda bo'sh qiymatlar tashlanadi |
| 4c — `edited` yozuvlarida Telegram'ning `edit_date` ishlatilishi | `3b9b416` + tekshiruv tuzatishlari | `pending_edits` SQLite jadvali (chat_id, message_id juftligi), `updateMessageContent` va `updateMessageEdited` ni ikki tomonlama juftlash (content-first yoki edit-first), oraliq versiyalarni saqlash, timeout zaxirasi (`msg_date`), `SweepPendingEdits`, `PeriodicCachePruner` va DI konfiguratsiyasi (`Capture:EditPairingTimeoutSeconds`, default 60s); 16+6 test (jami 288) | 10 buzish (a–j) + 18 mustaqil mutatsiya; o'chirishda kutayotgan tahrir yo'qolardi, sweep xatosi update'ni yutardi — tuzatildi |
| 6a — Capture sync client: shifrlash va outbox push | `aea2772` + tekshiruv tuzatishlari | HKDF kalit derivatsiyasi (content, peer, account), AES-256-GCM shifrlash (12-byte random nonce, 16-byte tag, payload = ciphertext ‖ tag), outbox row -> SyncRecord konvertatsiyasi (account_hash faqat activity uchun "", peer_hash barcha turlar uchun account-less), §0.14 pre-validation (poisoned rows karantini), CLI buyruqlari (--set-key, --enroll, 0600 ruxsat tekshiruvi), in-memory access token va faylga avval yoziluvchi refresh token rotatsiyasi, TimeProvider davriy push sikli (30s interval, 500 qator/5MB batch, 400 backoff, 5xx/tarmoq xatosida 1s..300s eksponentsial backoff), SQLite schema user_version = 2 (retry_count, next_retry_at, last_error); 18 test (jami 306) | 11 buzish (a–k) + 14 mustaqil mutatsiya; token javobi (`expires_at` ISO sana) o'qilmasdi, zaharlangan qatorlar navbatni to'sardi, v1 baza migratsiyasi jadvallarni jimgina tashlab ketardi — tuzatildi (jami 317) |
| 6a-2 — `--set-key` parol o'ramidan master kalitni ochish | `8c27f60` + tekshiruv tuzatishlari | `SyncCrypto.UnwrapMasterKey` (PBKDF2-SHA256 KEK + AES-256-GCM, salt 16B, nonce 12B, wrapped 48B, `MaxWrapIterations`), `SyncCrypto.Fingerprint` (SHA256["customsync-fingerprint-v1" ‖ master][0..8] hex), umumiy token refresh + faylga avval saqlash (`RefreshAndPersistTokenAsync`), runner 401 re-read state retry, `--set-key` interaktiv oqimi (wrap tanlash, 429 bir martalik tekshiruv, max 3 urinish, FP ko'rsatib tasdiqlash, mavjud kalitni almashtirish himoyasi, 0600 atomar yozish), maxfiy ma'lumotlar log/ekranga chiqmasligi; 14 test (jami 331) | 10 buzish (a–j) + 18 mustaqil mutatsiya; o'qib bo'lmaydigan kalit fayli tasdiqsiz almashtirilardi, serverdan kelgan `wrap_id` so'rov yo'lini o'zgartira olardi — tuzatildi (jami 337) |
| 6b — Setting yozuvlarini pull qilish va capture scope snapshot'lari | `<commit>` | Server `/api/v1/sync/pull` da `kind` filtri (ixtiyoriy, 400 unknown kind); `CaptureSyncRunner.PullCycleAsync` (`kind=setting`, `PullBatchSize`, `MaxPullPagesPerCycle`, token rotatsiyasi); 6 bosqichli qat'iy yozuv validatsiyasi; `account_hash` bo'yicha filtrlanmaydi (§3.2.1a); LWW merge (eng katta `(occurred_at, record_id)` g'olib); bitta tranzaksiyada cursor va merge commit; SQLite v2 -> v3 migratsiyasi (`synced_settings`, `sync_state`); `SyncedScopeSettingsSource` (8 xabar kaliti -> `ScopeSettingsSnapshot`, 3 faollik kaliti -> `ActivityScopeSettingsSnapshot`, to'liq bo'lmaganda fail-closed `null`, atomar swap, startda bazadan yuklash); DI da `services.Replace`; 15 test (jami 352) | 11 ta ataylab buzish (a–k) to'liq ushlandi |

> ⚠️ **Muhim eslatma:** Capture xizmati egasi (owner) quyidagilarni bajarmaguncha VPS'da ishlay olmaydi:
> 1) `libtdjson.so` kutubxonasini taqdim etish (prebuilt package, VPS'da build, yoki Docker orqali);
> 2) `api_id` va `api_hash` ni `appsettings.Production.json` ga kiritish;
> 3) VPS konsolida bir martalik `dotnet run --project src/CustomSync.Capture -- --login` orqali autentifikatsiyadan o'tish.

---

### Plan 05 Task 1–2 tekshiruvi (2026-09-17) — qanday qabul qilindi

Testlar mustaqil yurgizildi (184/184 tasdiqlandi), so'ng Gemini'nikidan
**boshqa** oltita mutatsiya qilindi — oltalasi ham ushlandi. Lekin kod
o'qishda to'rtta kamchilik chiqdi (`8fb237c` da tuzatildi):

1. **`TdRedactor` o'lik kod edi.** U faqat o'z testlaridan chaqirilardi;
   ishlab chiqarish kodi umuman payload loglamasdi. Ya'ni himoya bor
   ko'rinardi, lekin ulanmagandi — Task 3/4 da qo'yiladigan birinchi
   `LogDebug(raw)` `api_hash`, telefon raqami, kirish kodi va 2FA
   parolini journal'ga yozardi. Endi `TdClient` yuborilgan va kelgan
   har bir payload'ni redaktor orqali loglaydi.
2. **Xizmat avtorizatsiyani hech qachon tekshirmasdi.** `Worker`
   preflight'dan keyin "muvaffaqiyatli ishga tushdi" deb bo'sh
   aylanardi; `TdAuthenticator` DI'da ro'yxatdan o'tgan, lekin hech kim
   chaqirmasdi. Ya'ni avtorizatsiya qilinmagan deploy sog'lom ko'rinardi.
   Yangi `AuthorizationGate` `updateAuthorizationState` oqimini
   boshqaradi va tayyor bo'lmasa xizmat to'xtaydi.
3. **Chiqish kodi doim 0 edi.** systemd nol kodni "muvaffaqiyat" deb
   biladi: `Restart=on-failure` ishlamaydi va monitoring jim turadi.
   Ikkala nosozlik yo'li ham endi `ExitCode = 1` qo'yadi. Shu bilan
   birga `ITdClient` faqat preflight o'tgandan keyin olinadi — uni
   yaratish preflight tekshiradigan kutubxonaga P/Invoke qiladi.
4. **Marshalling qatlami butunlay sinovsiz edi.** Promptdagi UTF-8
   talabi (lotin bo'lmagan matnni saqlaydi) hech qanday test bilan
   bog'lanmagandi. `TdMarshal` ajratildi (P/Invoke'siz) va
   `ANSI` ga almashtirish endi testni yiqitadi.

Qo'shimcha: qabul sikli xatoda kechikishsiz qayta urinardi — kutubxona
uzilganda 24/7 jarayon protsessorni 100% band qilardi.

Ochiq qolgan, ongli qaror: `AuthorizationGate` ni faqat soxta klient
bilan sinaymiz; haqiqiy TDLib bilan uchi-uchiga sinov plan 05 Task 10 da.

---

### Plan 05 Task 3 tekshiruvi (2026-09-26) — qanday qabul qilindi

Testlar mustaqil yurgizildi (205/205 tasdiqlandi, build 0 ogohlantirish),
so'ng Gemini'nikidan **boshqa** beshta mutatsiya qilindi. Uchtasi
ushlandi (`BEGIN IMMEDIATE` -> `BEGIN`, `Prune` ning `cached_at` o'rniga
`date`, `Put` ning qaytish qiymati), **ikkitasi o'tib ketdi** — ikkalasi
ham jiddiy:

1. **`Get` `chat_id` ni e'tiborsiz qoldirsa ham hamma test o'tardi.**
   Turli chatlarda bir xil `message_id` bo'lishi normal holat, ya'ni
   Task 4 boshqa chatning matnini o'chirilgan xabar matni deb yozib
   qo'yardi. Chat izolyatsiyasi testi qo'shildi.
2. **`Worker` keshni umuman ulamasa ham hamma test o'tardi.** Kod
   `GetService` + `cache?.Initialize()` ishlatardi: ro'yxatdan o'tmagan
   xizmat xatosiz "kesh yo'q" holatiga olib kelardi, ya'ni xabarlar
   keshlanmasdi va `old_text` yo'qolardi. Delegate testi esa o'zining
   qo'lda yasagan `ServiceCollection` nusxasini sinardi, `Program.cs` ni
   emas — `TdRedactor` bilan aynan bir xil "ulanmagan himoya" xatosi.

Tuzatishlar:

- `CaptureCacheStartup` ajratildi: `GetRequiredService` bilan baland
  ovozda yiqiladi, `Worker` nosozlikda `ExitCode = 1` bilan to'xtaydi.
- Ro'yxatga olish `Program.cs` dan `CaptureCacheRegistration` ga
  ko'chirildi, endi haqiqiy ro'yxat (sozlama kalitlari bilan birga)
  sinaladi.
- Kesh preflight'dan **oldin** ulanadi (TDLib ga bog'liq emas) — shu
  tartib ulanishni preflight yiqilgan holatda ham sinash imkonini beradi.
- Tozalash sikli endi kuzatuvsiz emas: xatosi log'ga tushadi.
- `RunLoopAsync` ishga tushishda darhol tozalaydi (avval 6 soat kutardi,
  ya'ni tez-tez qayta ishga tushadigan xizmatda kesh hech tozalanmasligi
  mumkin edi) va kutish mexanizmi buzilsa aylanib ketmaydi.
- `Stats().FileSizeBytes` `-wal` va `-shm` ni ham sanaydi. WAL rejimida
  yangi yozuvlar `-wal` da turadi, ya'ni Task 9 dagi kvota diskdagi
  haqiqiy hajmni kam ko'rsatardi.

So'ngra sakkizta mutatsiya qayta yurgizildi — sakkiztasi ham ushlandi.
Jami 212 test.

Kelasi task uchun eslatma: `CachedMessage.CachedAt` ni chaqiruvchi
o'zi berib soatni chetlab o'tishi mumkin (Test07 shunga tayanadi).
Task 4 da bu qiymat TDLib'dan kelgan ma'lumotdan **olinmasligi** kerak.

### Plan 05 Task 6a tekshiruvi (2026-09-30) — qanday qabul qilindi

Delegate (Gemini) hisoboti (306/306 x3, 11 buzish) mustaqil tasdiqlandi:
306/306 x3. Kripto qismi to'g'ri (Core primitivlari, `ciphertext‖tag`,
activity `account_hash ""`). O'z mutatsiyalarim: 14 ta (X1–X14), 6 tasi
tirik qoldi. Topilgan va tuzatilgan (`CaptureSyncVerificationTests`):

1. 🔴 **Token yangilash productionda HECH QACHON ishlamasdi.** Server
   `expires_at` ni `DateTime` (ISO satr) qilib yuboradi
   (`JwtIssuer.cs:11`), klient `GetInt64()` qilardi → har refresh
   yiqiladi → birorta yozuv yuborilmaydi. Testlardagi soxta server son
   yuborgani uchun ko'rinmagan. Endi ISO satr ham, son ham o'qiladi (S01).
2. 🔴 **O'sha xato qurilmani abadiy bloklardi.** Server refresh token'ni
   almashtirib bo'lgan, klient javobni o'qiy olmay yangi token'ni
   saqlamasdi → eskisi o'lik → keyingi refresh 401 → qayta enroll. Endi
   `refresh_token` boshqa maydonlardan qat'i nazar saqlanadi (S02).
3. **Zaharlangan (§0.14) qatorlar navbatni to'sardi:** belgilanmay,
   har siklda batch boshini egallardi. Endi saqlanadi, lekin
   `next_retry_at = MAX` bilan navbatdan chiqadi (S03; Test06 shunga
   moslandi). Protokolda yo'q push holati endi `error` kabi (S04).
4. 🔴 **v1 baza migratsiyasi jadvallarni jimgina tashlab ketardi.**
   `Microsoft.Data.Sqlite` da qator qaytaradigan `PRAGMA journal_mode`
   dan keyingi statement xatosi YUTILADI va skript qolgani bajarilmaydi
   (tajribada tasdiqlandi). Yangi `idx_capture_outbox_retry` indeksi
   ustun qo'shilishidan oldin turardi → Task 4b davridagi bazada
   `pending_edits` yaratilmay qolar, versiya baribir 2 bo'lardi. Endi
   PRAGMA alohida, indeks migratsiyadan keyin, `ALTER` lar
   tranzaksiyada (S05).
5. Kalit fayli endi darhol `0600` bilan yaratiladi (oldin umask bilan
   yozilib keyin chmod — bir lahza o'qiladigan edi). Testsiz: oynani
   Windows'da kuzatib bo'lmaydi.
6. **Testlanmagan joylar (tirik mutatsiyalar):** har siklda refresh
   (S06), qator backoff'i o'smasligi (S07), javobda yo'q yozuv (S08 —
   hisobot "f buni qamraydi" degan, qamramagan), sikl backoff'i (S09;
   `CaptureSyncLoop.NextDelay` ajratildi), `platform = "service"` va
   kod chiqarilmasligi (S10), `BuildRecord` ichidagi §0.14 (S11).

Tuzatishdan keyin 14/14 ushlanadi. 317/317 x3.

### Plan 05 Task 6a-2 tekshiruvi (2026-09-30) — qanday qabul qilindi

Delegate (Gemini) hisoboti (331/331 x3, 10 buzish) mustaqil tasdiqlandi:
331/331 x3. Kripto to'g'ri (vektorlar), soxta server bu safar haqiqiy
shaklda (snake_case, `DateTime`), refresh mantiqi bitta joyda
(`RefreshAndPersistTokenAsync`), runner 401 da holat faylini qayta
o'qiydi. O'z mutatsiyalarim: 12 ta (Y1–Y12), 5 tasi tirik qoldi
(Y1 — ekvivalent: xabar baribir "rate limit" deydi). Topilgan va
tuzatilgan (`CaptureKeyWrapVerificationTests`):

1. **O'qib bo'lmaydigan kalit fayli tasdiqsiz almashtirilardi**
   (buzilgan yoki ruxsati noto'g'ri — egasining yagona nusxasi bo'lishi
   mumkin). Endi so'raladi (K01).
2. **Serverdan kelgan `wrap_id` URL'ga escape qilinmasdi:** `/` yoki `?`
   so'rovni Bearer token bilan boshqa endpoint'ga burardi (server
   ishonchli emas). Endi `Uri.EscapeDataString` (K06).
3. Master kalit massivi saqlangandan keyin nollanadi (testsiz).
4. **Testlanmagan joylar:** aynan 3 urinish (K02), bir xil FP'da fayl
   tegilmasligi (K03), `MaxWrapIterations` CLI'da parol so'ralishidan
   oldin (K04), preflight (K05).

Tuzatishdan keyin 6/6 ushlanadi. 337/337 x8.

**Tekshiruv paytida ikki eski beqaror test topildi va tuzatildi** (faqat
test kodi):
- `StatsTests.Stats_peers_sorts_by_bytes_count_and_recent` 10 dan 7
  marta yiqildi: test bazasi yugurishlar orasida tozalanmaydi, test esa
  o'z peer'larini top-500 ichidan qidirardi — baza o'sgani sari kichik
  peer tushib qolardi. Endi butun ro'yxat olinadi.
- `CaptureUpdateHandlerTests.Test12_Ordering...` (12 s da yiqilardi):
  thread pool band bo'lsa `Task.Delay` deadline'dan keyin qaytadi va
  sikl qatorlarni qayta o'qimay chiqardi. Endi sikldan keyin bir marta
  qayta o'qiladi.

⚠️ **Hisobot aniq emas edi:** "6-band" dagi konsol dialogi to'qilgan
("Fetching...", ro'yxatda iteratsiyalar — serverning ro'yxat javobida
iteratsiya YO'Q), (b) buzishda "100 000 iteratsiya" (vektorda 1 000),
"`DeviceEndpoints.cs` dagi `KeyWrapSummary`" (aslida `KeyEndpoints.cs`,
`WrapSummary`). Kod to'g'ri, hisobot matni emas. **Haqiqiy dialog**
(kodga ko'ra, deploy eslatmalari uchun):

```
Enter passphrase: ********
Key fingerprint (FP): <16 hex>
Does this match 'Kalit barmoq izi (FP)' in tdesktop Sync tab? [y/N]: y
Master key successfully saved to /var/lib/customsync-capture/master.key.
Key fingerprint (FP): <16 hex>
```
Bir nechta o'ram bo'lsa oldin: `Available passphrase wraps:` →
`  1. Label: "...", Created: ...` → `Select wrap [1-N]: `.
Xato parol: `Incorrect passphrase. Please try again (attempt 1/3).`

### 🔴 Deploy oldidan xavfsizlik auditi (VPS buzilgan)

2026-09 da egasining Contabo VPS'iga buzib kirilgan va miner
o'rnatilgan (CPU 4+ kun 99–100%). Egasi to'xtatdi, lekin qayta kira
olmasligiga 100% ishonch yo'q (boshqa sessiyada muhokama qilinmoqda).
**Qaror:** VPS'ga to'liq ishonilmaydi; birgalikda to'liq audit va
hujumlarga tayyorgarlik tugamaguncha bu loyiha u yerga deploy
qilinmaydi va ishga tushirilmaydi.

Nega bu loyiha uchun ayniqsa muhim: capture xizmati VPS'da master
kalitni (butun E2E arxiv — barcha qurilmalar), TDLib sessiyasini
(Telegram akkauntiga to'liq kirish) va ochiq matnli xabar keshini
saqlaydi. U yerda root = E2E himoyasi yo'q.

Auditda hal qilinadigan savollar:
- Qayta o'rnatish (toza image) yoki tozalash — root bilan buzilgan
  tizimga tozalashdan keyin ishonib bo'lmaydi (cron, systemd, SSH
  kalitlari, o'zgartirilgan binarlar, rootkit).
- U serverda bo'lgan har bir sirni almashtirish (SSH, DB parollari,
  JWT kaliti, API kalitlari — iBOS CRM ham).
- Capture qayerda ishlaydi: alohida host; kalit faqat xotirada (har
  restartda qo'lda — plan 05 Task 6 muqobili); yoki ikkalasi.
- Izolyatsiya: alohida foydalanuvchi, systemd cheklovlari
  (`NoNewPrivileges`, `ProtectSystem=strict`, `MemoryMax`,
  `IPAddressAllow`), faqat kerakli tarmoq chiqishlari.
- Monitoring: CPU/jarayon/login ogohlantirishlari — buzilishni kunlar
  emas, daqiqalarda sezish.

### Integratsiya auditi uchun yig'ilayotgan ro'yxat

Egasi qarori (2026-09-29): ikki loyiha birlashtirilayotganda to'liq
audit bo'ladi. Shu vaqtgacha chegaradagi xavflar shu yerga yoziladi
(tdesktop kodi ko'rib chiqilmaydi):

- tdesktop `setting` larni `occurred_at` taqqoslamay pull tartibida
  qo'llaydi, capture esa eng yangisini — bir vaqtdagi o'zgarishda ikki
  tomon turli qiymatda qolishi mumkin.
- tdesktop har startda har akkaunt nomidan barcha scope kalitlarini
  qayta yuboradi — serverda `setting` yozuvlari to'planadi
  (retention/hajm).
- Refresh rotatsiyasi: server token'ni almashtirib, javob klientga
  yetmasa (tarmoq uzilishi) qurilma bloklanadi — server tomonida
  oldingi token uchun qisqa imtiyoz oynasi yo'q (`DeviceService.RefreshAsync`).
- 🔴 **Setting qiymatini orqaga qaytarish (replay).** Payload `occurred_at`
  ga bog'lanmagan (AAD yo'q, payload ichida vaqt yo'q). Buzilgan server
  eski setting payload'ini yangi `occurred_at` bilan qayta o'rab (record_id
  ochiq maydonlardan qayta hisoblanadi) oq/qora ro'yxatni eski holatga
  qaytara oladi — capture ham, tdesktop ham buni sezmaydi. Yopish faqat
  protokol darajasida: `occurred_at` (va kind/msg_id) ni AAD yoki
  payload'ga kiritish — ikkala tomon birga (6b tayyorlashda topildi,
  2026-10-01).

### Plan 05 Task 4c tekshiruvi (2026-09-29) — qanday qabul qilindi

Delegate (Gemini) hisoboti (282/282 x3, 10 buzish) mustaqil tasdiqlandi:
282/282 x3, build 0 ogohlantirish. O'z mutatsiyalarim: 10 ta (M1–M10),
3 tasi tirik qoldi. Topilgan va tuzatilgan (`CaptureEditDateVerificationTests`):

1. **Tahrirdan keyin 60 s ichida o'chirish eski matnni yo'qotardi.**
   `DeleteMessagesAndRecordOutbox` kutayotgan pending qatorni jimgina
   o'chirardi; kesh va `deleted` yozuvida yangi matn, eski matn hech
   qayerda qolmasdi. Endi o'sha tranzaksiyada `FlushPendingEdit` uni
   zaxira `occurred_at` bilan `edited` qilib chiqaradi (V01, V02).
2. **Sweep xatosi joriy update'ni yutardi.** Sweep `HandleUpdate` ning
   umumiy `try` ichida edi: bitta buzuq pending qator har kelgan
   o'chirish/tahrirni yo'qotardi. Endi alohida `try`, `ErrorCount` (V03).
3. **Har update'da `BEGIN IMMEDIATE`.** Sweep har update'da yozish
   qulfini olardi. Endi soniyasiga ko'pi bilan bir marta (V04) va ish
   bo'lmasa qulfsiz `EXISTS` bilan qaytadi.
4. **Testlanmagan simlar (tirik mutatsiyalar):** pruner'dagi sweep
   (ishga tushish/davriy — jim akkauntda yagona yo'l) va
   `Capture:EditPairingTimeoutSeconds` ikki ro'yxatda (V05, V06).
   Ikki nusxa parser bitta `ReadPositiveInt` ga birlashtirildi.

Tuzatishdan keyin: M1/M5/M6 + yangi N1–N5, hammasi ushlanadi.
288/288 x3.

⚠️ Ma'lum cheklov (hujjatlandi, tuzatilmadi): faqat reply_markup
o'zgargan `updateMessageEdited` (masalan bot tugmalari) `edit_date` ni
60 s saqlab turadi; shu oraliqda matn tahriri content-birinchi kelsa,
eski `edit_date` bilan juftlanadi → tdesktop'nikidan boshqa `record_id`
(bitta tahrir ikki marta). Kam uchraydi; kerak bo'lsa content kelganda
`getMessage` dan `edit_date` olish bilan yopiladi.

### Plan 05 Task 4b tekshiruvi (2026-09-27) — qanday qabul qilindi

Delegate hisoboti (261/261 x3, 10 buzish) mustaqil tasdiqlandi.
tdesktop bilan moslik delegate o'qishiga tayanadi — bu sessiya
tdesktop kodini o'qimaydi (egasi qarori). Protokol fayli bilan tekshirildi:
`DiscriminatorFor("scope.whitelist")` = test-vectors'dagi
`3528686638094831585` (big-endian; hisobotdagi "LittleEndian" so'zi xato,
kod to'g'ri). Topilgan va tuzatilgan:

1. **Scope fail-open bo'lib qolgan edi.** Delegate `ICaptureScope` ni
   ixtiyoriy qilib `_scope is not null && !...` yozgan: scope berilmasa
   HAMMA xabar ushlanardi. Endi `null` → `NoneCaptureScope` /
   `NoneActivityScope` (Test13).
2. **Activity sinxron manbasi DI'da e'tiborsiz qolishi testlanmagan**
   (Task 5 dagi bo'shliqning aynan o'zi, o'z mutatsiyam tirik qoldi).
   Test16.
3. **Atomiklik testi yo'q edi** (hisobotda (c) buzish uchun test nomi
   yo'q). Test14: outbox'ga INSERT trigger bilan yiqitiladi —
   `activity_latest` o'zgarmay qolishi shart, aks holda o'zgarish abadiy
   yo'qoladi.
4. Discriminator protokol vektoriga bog'landi (Test15).
5. `CaptureCacheVerificationTests.Test17` (Task 3): ochiq ulanish
   checkpoint'ni to'xtatmaydi, faol o'qish tranzaksiyasi to'xtatadi —
   yuklama ostida bir marta yiqildi, tuzatildi.

O'z mutatsiyalarim: 8 ta (Q1–Q8), hammasi ushlanadi.
Natija: 265/265 x5, build 0 ogohlantirish.
Hisobotda yo'q edi (promptda so'ralgan): tdesktop bilan moslik jadvali
file:line bilan va qamrab olinmagan maydonlar ro'yxati.

### Plan 05 Task 5 tekshiruvi (2026-09-27) — qanday qabul qilindi

Delegate hisoboti (247/247 x3, 10 buzish) mustaqil tasdiqlandi. Zanjir
tdesktop'niki bilan mos. Topilgan va tuzatilgan:

1. **Preflight "son"ni tekshirardi, evaluator esa satrni aniq
   solishtiradi.** `-1002827825432` (TDLib/Bot API chat_id — eng
   ehtimoliy xato), `"0123"`, `" 123"`, `"+123"` preflight'dan o'tardi,
   lekin hech qachon mos kelmasdi: Block yozuvi jimgina ishlamay, chat
   ushlanaverardi. Endi faqat kanonik musbat tdesktop peer id
   (`IsCanonicalPeerId`), xato matnida to'g'ri shakl aytiladi.
2. **`Capture:Scope:DefaultEnabled` xato yozilsa** (`"ture"`) jimgina
   `false` bo'lardi. Endi preflight xatosi.
3. **DI'dagi sinxron manba e'tiborsiz qolishi testlanmagan edi** (o'z
   mutatsiyam tirik qoldi). Task 6 aynan shu manbani qo'yadi. Test10b —
   manba `AddCaptureHandlers` dan oldin ham, keyin ham ro'yxatga olinsa
   ishlatiladi.
4. `CaptureClientTests.Test2` mutatsiya yuklamasida 2 s kutishga
   sig'masdi — 10 s.

O'z mutatsiyalarim: 7 ta; N5 (WL kategoriyada `!exactBL` sharti) — teng
kuchli mutant: aniq BL zanjirda WL'dan oldin `false` qaytaradi.
Natija: 249/249 x3, build 0 ogohlantirish.

### Plan 05 Task 4a tekshiruvi (2026-09-27) — qanday qabul qilindi

Delegate hisoboti: "229/229, 9 ta buzish ushlandi, promptdan chetlanish
yo'q". Mustaqil tekshiruvda (PC, `DESKTOP-5CAUS66`) topilgan va tuzatilgan:

1. **Ulanish bitta qatorga bog'liq edi.** Handler `Worker` dagi
   `GetRequiredService<CaptureUpdateHandler>()` qatori tufayligina
   obuna bo'lardi; qator o'chsa xizmat "ishlab turib" hech narsa
   ushlamasdi, testlar o'tardi. Endi `AddCaptureHandlers` ITdClient
   registratsiyasini o'raydi: mijoz yaratilgan zahoti `handler.Attach`.
   ITdClient'siz chaqirilsa — `InvalidOperationException`.
2. **Tahrir kesh-miss'ida to'qima qator yozilardi** (`date=0`,
   `is_out=false`, `sender=NULL`). Keyingi `deleted` yolg'on maydonlar va
   `occurred_at = now` (tdesktop'nikidan boshqa `record_id`) berardi.
   Endi hech narsa yozilmaydi; handler `getMessage` bilan to'liq xabarni
   olib `TryAdd` qiladi (bor qator ustiga yozmaydi).
3. **`my_id` kelmasa bufer cheksiz o'sardi** va hamma narsa jim
   yo'qolardi. `authorizationStateReady` da `getMe` zaxira yo'li
   qo'shildi; buferga faqat 3 ta ushlanadigan tur tushadi.
4. **SQLite busy_timeout 5s** — yuklama ostida `database is locked`
   (Task 3 `CaptureCacheTests.Test09` flaky edi). O'chirish tranzaksiyasi
   shunday yiqilsa hodisa qaytmas yo'qoladi. 30s ga oshirildi.
5. **Ushlanmagan mutatsiyalar:** o'chirish/tahrirda scope tekshiruvini
   olib tashlash va `PayloadBuilder` da `"`/`\`/boshqaruv belgisi
   escape'ini olib tashlash — hech bir test yiqilmasdi. Test16b, 16c.
6. `DatabaseFixture.DisposeAsync`: `DROP DATABASE ... WITH (FORCE)`
   ba'zan `42501` (boshqa rol jarayoni) — "Class Cleanup Failure" bilan
   3 ta toza test yiqilardi. Retry qo'shildi.

Yangi testlar: 10 (qayta yozildi), 10b, 10c, 16b, 16c, 18, 18b, 19.
14 ta o'z mutatsiyam (M1–M9, M6b, M7b, M7c) — hammasi ushlanadi.
Natija: 236/236, ketma-ket 8 marta yashil; build 0 ogohlantirish.

Ochiq savol (egasiga, protokol): bir xabar ikki marta tahrirlanib,
birinchisi hali yuborilmagan bo'lsa, oraliq versiya yo'qoladi —
tdesktop'da ham xuddi shunday (`INSERT OR REPLACE`).

---

## 2. 🔴 KEYINGI QADAM — plan 05 Task 6 (sync klienti: synced scope settings)

Task 4c tugadi va tekshirildi (266 + 16 delegate + 6 tekshiruv testi = 288; tekshiruv bo'limi yuqorida).
Qilingan ishlar (Task 4c):
- `MessageCache`: `pending_edits` jadvali qo'shildi (`chat_id`, `message_id`, `peer_id`, `account_id`, `old_text`, `new_text`, `is_out`, `msg_date`, `edit_date`, `observed_at`).
- `updateMessageContent` va `updateMessageEdited` ni ikki tomonlama juftlash:
  - Content avval kelsa: `pending_edits` ga yoziladi va `edit_date` kutiladi; agar avvalgi tahrir juftlanmagan bo'lsa, oraliq versiya zaxira `occurred_at` bilan outbox'ga chiqarilib, yangisi kutishga qo'yiladi.
  - Edited avval kelsa: `edit_date` saqlab turiladi va keyingi `updateMessageContent` kelishi bilan darhol juftlanib chiqariladi.
  - `ShouldAntiEdit = false` bo'lganda kesh yangilanadi, lekin `pending_edits` ga qator qo'shilmaydi va outbox'ga yozilmaydi.
- `SweepPendingEdits(now, timeoutSeconds)`: yetim qolgan (timeout o'tgan) content qatorlarini zaxira `occurred_at = msg_date` (yoki `observed_at`) bilan chiqaradi, yolg'iz yetim `edit_date` qatorlarini esa jimgina o'chiradi.
- `CaptureUpdateHandler`: har bir update qabulida va `PeriodicCachePruner` davriy tozalashida `TimeProvider` orqali sweep chaqiriladi; `UnpairedEditCount` metrikasi kiritildi.
- `Capture:EditPairingTimeoutSeconds` (sukut bo'yicha 60 soniya) sozlamasi va DI registratsiyasi ulandi.
- 16 ta unit/integratsiya testi (`CaptureEditDateTests.cs`), 10 ta ataylab buzish (a–j) to'liq tasdiqlandi.

### Plan 05 navbati (2026-09-29 holati)

1. **Task 5 — scope.** ✅ YAKUNLANDI va tekshirildi (`640066e` + tuzatish, 249 test).
2. **Task 4b — activity.** ✅ YAKUNLANDI va tekshirildi (`457b92f` + tuzatish,
   265 test). Protokol savollari tdesktop sessiyasida yopildi
   (`a06ed12373`, CHANGELOG 2026-09-27):
   (a) activity `msg_id` = `DiscriminatorFor(field)` — spec §3.2 va
   vektorlar to'g'rilandi; `test-vectors.json` dagi yangi `discriminator`
   bo'limini `CaptureActivityTests.Test15b` to'liq tekshiradi.
   (b) activity kalitlari belgilandi (§3.2.1): `scope.activity_track_all_contacts`,
   `scope.activity_include`, `scope.activity_exclude` — tdesktop ularni
   HALI yubormaydi. 🔴 **Task 6 uchun:** sinxron snapshot qurilganda bu
   kalitlar yo'q bo'lsa — `track_all=true`, ro'yxatlar bo'sh (spec
   talabi). Snapshot umuman yo'q bo'lsa (juftlanmagan xizmat) server
   standarti `Capture:Activity:TrackAllContacts` = false qoladi.
   (c) `userStatusEmpty` → `long_ago` (§3.2.2); `1375315204` dan eski
   online/offline va noma'lum holat ham `long_ago`; `empty` hech qachon
   yozilmaydi. Delegate `empty` yozib, testda mustahkamlagan edi —
   tuzatildi (`b6d3e22`, 266 test).
   Qamrab olinmagan: `photo` maydoni (§3.2.2: story/rasm signali
   `online:<vaqt>`, `photo` belgisi bilan) — alohida kichik vazifa.
3. **Task 4c — `edited` uchun `occurred_at = edit_date`.** ✅ YAKUNLANDI va tekshirildi (`3b9b416` + tuzatish, 288 test).
   `pending_edits` jadvali, ikki tomonlama juftlash, timeout fallback,
   10 ta buzish (a–j) sinovdan o'tdi.
4. **Task 6 — Synced scope settings.**
   Scope `setting` kalitlari ham belgilandi (spec §3.2.1:
   `scope.whitelist`, `scope.blacklist`, `scope.wl_categories`,
   `scope.bl_categories`, `scope.antidelete_global`,
   `scope.antiedit_global`, `scope.antidelete_per_peer`,
   `scope.antiedit_per_peer`; `value` doim satr) — Task 6 da scope
   snapshot shulardan to'ldiriladi.
5. Task 6 (sync klienti) va keyingilari.

**tdesktop sessiyasiga uzatiladigan ishlar** (bu sessiya u yerga
yozmaydi):
- `edited` protokol o'zgarishi — `docs/proposal-edited-edit-date.md`.
- Scope ro'yxatlari uchun `setting` kalitlari va qiymat formati (WL, BL,
  kategoriyalar, AntiDelete/AntiEdit per-peer va global) — ular
  bo'lmasa capture foydalanuvchining tdesktop sozlamalarini ko'rmaydi.
- Topilgan nomuvofiqlik: fon tahrir yo'li (`updateEditedMessage` →
  `RecordBackgroundEdit`) faqat `ShouldBackgroundCache` ni tekshiradi,
  AntiEdit o'chiq chatda ham tahrir yozadi; xotiradagi yo'l
  (`history_item.cpp`) esa `ShouldAntiEdit` ni tekshiradi.

### Task 4 promptiga majburiy kiritiladigan shartlar

1. **`CachedMessage.CachedAt` ni TDLib ma'lumotidan olmaslik.** Bu
   maydon chaqiruvchiga soatni chetlab o'tish imkonini beradi (Test07
   shunga tayanadi), ya'ni TDLib'dan kelgan `date` ni unga berish
   retention'ni buzadi: eski xabar darhol tozalanib ketishi mumkin.
   Vaqt faqat `TimeProvider` dan olinadi.
2. **Keshga `GetMany` va `Delete` qo'shiladi.** `messageDeleted` bir
   nechta id bilan keladi, ya'ni har biriga alohida `Get` + o'chirish
   noatomar bo'ladi. Ikkalasi ham bitta tranzaksiyada.
3. **`edited` payload = `{old_text, new_text, is_out}`** (spec §0.8).
   `old_text` faqat keshdan keladi; kesh bo'sh bo'lsa nima yozilishi
   promptda aniq yozilishi kerak (jimgina `null` emas).
4. **Manfiy `msg_id`** (avatar/story, spec §0.6) va **media yo'llari
   yozilmasligi** (faqat `media_id`) saqlanadi.
5. **Har bir himoya ulangan bo'lishi shart.** Uch task ketma-ket bir xil
   xato bilan keldi: himoya yozilgan, lekin chaqirilmagan yoki faqat
   test o'z nusxasini sinagan (`TdRedactor`, avtorizatsiya, kesh
   ulanishi). Promptda: ishlab chiqarish yo'li orqali o'tadigan test
   talab qilinadi, qo'lda yasalgan `ServiceCollection` nusxasi emas.

### Kelishilgan tartib (2026-09-09)

    04 -> 05 -> 03 -> read_at -> TO'LIQ DEPLOY

Foydalanuvchi qarori. Dastlab 05 dan boshlash rejalashtirilgan edi;
plan 05 ning o'z kirish sharti (*"04 majburiy — xizmat doimiy ma'lumot
oqimi hosil qiladi va xotira boshqaruvisiz disk tez to'ladi"*) topilgach,
04 oldinga olindi.

⚠️ **Deploy oxirida — bu ongli qaror, xavfi yozib qo'yilgan.**
Klient bilan server hali hech qachon gaplashmagan: 146 server testi ham,
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
| 6 — ikki fazali o'chirish | ✅ | diskni to'lishdan saqlaydigan xavfsiz ikki fazali purge mexanizmi |
| **7 — rejalashtirilgan ishlar** | ✅ | ArchiveSchedule, ArchiveJobRunner, ArchiveJobService, archive_job_runs jadvali, 17 test (jami 169 ta test) |
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

### ✅ `libtdjson` — qaror qabul qilindi (2026-09-16)

**Lokal Docker'da build qilinadi, tayyor `.so` VPS'ga ko'chiriladi.**
Foydalanuvchi qarori.

Nega shu yo'l: to'g'ri `linux-x64` artefakt chiqadi (Windows build'dan
chiqadigan `tdjson.dll` deploy'ga yaramaydi), ishlab turgan VPS'da 8 GB
lik build va OOM xavfi yo'q, va akkauntga ulanadigan kutubxona uchun
uchinchi tomon binarysiga ishonish shart emas. Rad etilgan variantlar:
VPS'da swap bilan build (ishlab turgan serverda og'ir), tayyor native
NuGet paketi (nashr qiluvchiga ishonish talab qiladi).

Buni **foydalanuvchi o'zi bajaradi** — og'ir build agentlarga taqiqlangan:

```bash
git clone https://github.com/tdlib/td.git
cd td
git checkout <tag>          # versiya QADALSIN, commit hash yozib qo'yilsin
docker run --rm -v "$PWD":/src -w /src --memory=8g --cpus=2 ubuntu:22.04 bash -c "
  apt-get update && apt-get install -y cmake g++ make zlib1g-dev libssl-dev gperf &&
  mkdir -p build && cd build &&
  cmake -DCMAKE_BUILD_TYPE=Release .. &&
  cmake --build . --target tdjson -- -j2"
# natija: build/libtdjson.so -> VPS'ga ko'chiriladi
```

Ko'chirgandan keyin: `.so` **repoga commit qilinmaydi**, yo'li
`Telegram:TdJsonPath` sozlamasidan olinadi, va `sha256` i yozib
qo'yilsin (keyin qayta build qilinsa nima o'zgarganini bilish uchun).

**`api_id` / `api_hash`:** foydalanuvchida bor, o'zida saqlanadi.
Repoga ham, chatga ham tushmaydi — faqat VPS'dagi
`appsettings.Production.json` da (u `.gitignore` da).

**Login (telefon -> kod -> 2FA):** faqat foydalanuvchi, bir marta,
VPS'da `--login` rejimida. Agent bajarmaydi.

TDLib faqat ishga tushganda kerak (P/Invoke runtime'da bog'lanadi), ya'ni
Task 1-2 ning kodi va testlari `.so` siz yoziladi va tekshiriladi.

### 🔴 Plan 05 ga qo'shiladigan alohida vazifa: sessiya himoyasi

Foydalanuvchi qarori (2026-09-16): **alohida vazifa qilib qo'shiladi.**

Sabab: capture xizmati VPS'da TO'LIQ Telegram sessiyasini saqlaydi —
u o'g'irlansa akkauntga to'liq kirish demakdir, va bu parol yoki 2FA
bilan to'xtatilmaydi. Qamrovi (prompt/checklist alohida yoziladi):
systemd cheklovlari (`ProtectSystem`, `PrivateTmp`, `NoNewPrivileges`,
alohida foydalanuvchi), TDLib bazasi papkasining ruxsatlari,
`appsettings.Production.json` ruxsatlari, zaxira nusxalarida sessiya
qanday saqlanishi (yoki umuman saqlanmasligi) va sessiyani bekor qilish
tartibi.

Deploy'dan OLDIN bajariladi.

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
| ✅ `RequiresExplicitConfirmation` Task 6 da **qo'llandi**: qo'lda target'da purge `AwaitingConfirmation` yozadi, audit'ga qayd qiladi va hech narsa o'chirmaydi (`PurgeService.cs`) | Hal qilindi (tekshirildi 2026-09-14); tasdiqdan keyin o'chirishni davom ettirish yo'li ham bor: `src/CustomSync.Services/Storage/PurgeService.cs:1003`, `src/CustomSync.Services/Storage/PurgeService.cs:173`, `src/CustomSync.Services/Storage/PurgeService.cs:896` |
| Staging papkasining o'zi cheksiz o'sadi; avtomatik tozalash ataylab yo'q (yuklab olinmagan arxiv — yagona nusxa) | **Plan 03 (UI)** — Task 7 da ongli ravishda o'chirish qilinmadi: job faqat `archive_job.staging_report` audit yozadi (fayllar soni va hajmi). O'chirishni odam bosadi, chunki "tasdiqlangan" arxiv yuklab olinganini isbotlamaydi |
| `never_delete` siyosatida `OlderThanDays` endi ma'nosiz — formada yashirilmasa operator uni ishlayapti deb o'ylaydi | Plan 03 (UI) |
| ✅ `SummaryAsync` agregatsiyasi `StorageAsync` bilan takrorlanardi | Hal qilindi (Task 7): ikkalasi `GetStorageCountsAndBytesAsync` ni chaqiradi |
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
| `test-vectors.json` | `<tdesktop>\docs\sync-protocol\test-vectors.json` (laptop: `C:\TBuild\tdesktop`) |
| ⚠️ PC NuGet (2026-09-27) | E: tozalangandan keyin `C:\Program Files (x86)\NuGet\Config\Microsoft.VisualStudio.FallbackLocation.config` yo'q papkani (`E:\Application's datas\...\NuGetPackages`) ko'rsatadi → build `MSB4018`. Tizim fayliga tegilmadi. E: papkasi paket MANBASI sifatida ham turibdi (`NU1301`). Aylanib o'tish: repo tashqarisida `packageSources` (faqat nuget.org) va `fallbackPackageFolders` ichida `<clear/>` bo'lgan vaqtinchalik nuget.config → bir marta `dotnet restore --configfile <fayl>`, keyin HAR DOIM `dotnet build --no-restore` / `dotnet test --no-build` (oddiy build qayta restore qilib yiqiladi; 2026-09-29 tasdiqlandi). Doimiy yechim — egasi: VS Installer'da "Repair" yoki o'sha config faylini o'chirish (admin) |
| pg auth | `scram-sha-256` — parolsiz kirish yo'q |

🔴 **Paket qo'shganda ALBATTA `--version 8.0.*`** — versiyasiz
`dotnet add package` net10.0 uchun qurilgan paketni oladi va `NU1202`
beradi. Bu loyihada besh marta uchragan.

Connection string va `Jwt:SigningKey` —
`src\CustomSync.Api\appsettings.Development.json` da (gitignore'da).
`db-bootstrap.ps1` ni **qayta** ishga tushirish rolga yangi parol
qo'yadi va faylni qayta yozadi.

### 🔴 Boshqa kompyuterda davom ettirish (2026-09-14)

**Yo'llar kompyuterga bog'liq.** Laptop (`DESKTOP-L2J53IK`) va PC'da
yo'llar farq qiladi. Sessiya boshida `hostname` ni aniqlang va yo'llarni
`<tdesktop>\docs\MACHINES.md` jadvalidan oling (topish tartibi
`CLAUDE.md` da). Bu faylda, promptlarda va xotirada uchragan
`C:\TBuild\...` / `C:\Users\Oybek\Documents\Projects programming\...`
— laptop yo'llari: ishlatishdan oldin jadval orqali "tarjima" qiling va
mavjudligini tekshiring. Yangi hujjatlarda nisbiy yozing: `<tdesktop>/...`,
`<server>/...`. PC qatori `MACHINES.md` da hali ❓ — uni tdesktop
sessiyasi to'ldiradi (bu sessiya tdesktop'ga yozmaydi); topilgan PC
yo'llarini foydalanuvchiga ayting.

Git bilan **ketmaydigan** narsalar va yangi mashinada nima qilish kerak:

| Nima | Nega git'da yo'q | Yangi mashinada |
|---|---|---|
| `src\CustomSync.Api\appsettings.Development.json` | gitignore — ichida DB paroli va `Jwt:SigningKey` | Faylni **ko'chirmang**. PostgreSQL o'rnating va `scripts\db-bootstrap.ps1` ni ishga tushiring — u rolni yaratib, faylni o'zi yozadi. Usiz `dotnet test` darhol yiqiladi |
| Claude xotira fayllari (4 ta qoida: faqat customsync-server, Co-Authored-By yo'q, faqat o'zbek tilida, server ishga tushirma) | `~\.claude\projects\<papka>\memory\` — git'dan tashqarida | `agent-sync pull` (`Oybek-M/agent-sync-vault`) tiklaydi. ⚠️ Papka nomi sessiya **ochilgan yo'ldan** hosil bo'ladi (belgilar `-` ga): laptopda `C--Users-Oybek-Documents-Projects-programming-Telegram-customsync-server`. PC'da repo boshqa yo'lda bo'lsa, Claude boshqa (bo'sh) papkaga qaraydi — o'shanda laptop papkasidagi `memory\` ni ustiga yozmay nusxalang. Xotira yo'qolsa ham ish to'xtamaydi: barcha qoidalar shu faylda va `CLAUDE.md` da ham bor |
| tdesktop repo'si | alohida repo, **boshqa sessiya boshqaradi** | Faqat o'qish uchun kerak: spec, planlar, `STATUS.md`, `MACHINES.md`, `test-vectors.json` va plan 04 promptlari (`docs/superpowers/plans/04-task{1,2,3,6}-prompt.md`, `04-task6-fixes-prompt.md`). Yo'li — `MACHINES.md` jadvalidan |
| GitHub credential | har mashinada alohida | `gh auth login` yoki Git Credential Manager — aks holda push o'tmaydi |
| `backups\` (DB dump) | gitignore | Faqat eski ma'lumot kerak bo'lsa ko'chiring; ishlab chiqish uchun shart emas |

Task 7 prompti tayyor (`docs/04-task7-prompt.md`). Keyingi qadam:
Gemini hisobotini olib, mustaqil tekshirish (§2).

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
15. **`archive_job_runs` jadvali va atomar claim (`INSERT ... ON CONFLICT (run_date) DO NOTHING`)** — in-memory bayroq restartda qayta yurgizib yuborardi.
16. **`ArchiveJobRunner` va `ArchiveJobService` ajratilishi** — taymerli BackgroundService unit testlar uchun mos emas, alohida testlanadigan runner va sof `ArchiveSchedule` ga ajratildi.
17. **`storage.disk_capacity_mb` (bayt emas)** — int 2 GiB da overflow bo'lmasligi uchun MB da olindi.
18. **`CustomSyncWebApplicationFactory`** — integratsiya testlarida `ArchiveJobService` tasodifan ishlab test bazasiga ta'sir qilmasligi uchun test xostidan chiqarildi.
19. **`ITdTransport` ajratilishi** — P/Invoke'ni to'g'ridan-to'g'ri chaqirish o'rniga transport interfeysi qo'yildi; bu barcha TDLib klient va autentifikatsiya testlarini native `libtdjson`siz va tarmoqsiz ishonchli yurgizish imkonini berdi.
20. **`NativeLibrary.SetDllImportResolver`** — `Telegram:TdJsonPath` orqali native kutubxona joylashuvini ixtiyoriy papkadan yoki muhitdan yuklash imkoniyati yaratildi.
21. **`TdRedactor` orqali maxfiy ma'lumotlarni yashirish** — loglarga `api_hash`, `phone_number`, `code`, `password` va h.k. chiqib ketishining oldini olish uchun yagona tozalovchi kiritildi.
22. **`Put` avvalgi qatorni qaytarishi (read-and-replace atomar tranzaksiyasi)** — Plandagi `INSERT OR REPLACE` eski matnni yo'q qilardi. Ammo `updateMessageContent` hodisasida `edited` yozuvi `{old_text, new_text, is_out}` talab qilgani uchun `old_text` saqlanib qolishi shart. Shuning uchun `Put` almashtirilgan eski satrni qaytaradi.
23. **WAL va busy_timeout sozlamalari** — SQLite doimiy xizmat rejimida `Prune` va parallel yozuvchilar to'qnashuvida `database is locked` xatoligi chiqmasligi uchun `PRAGMA journal_mode = WAL` va `PRAGMA busy_timeout = 5000` o'rnatildi.
24. **Sxema versiyasi (`PRAGMA user_version = 1`)** — Kelgusi vazifalarda migratsiyalarni aniq boshqarish uchun sxema versiyasi belgilandi.
25. **Injectable clock (`TimeProvider`)** — Vaqt o'tishi va `Prune` tozalash chegaralarini 30 kun kutmasdan aniq sinovdan o'tkazish uchun keshga vaqt provayderi ulandi.
26. **`PeriodicCachePruner` ajratilishi va Worker'da ulanishi** — `Prune` o'lik kod bo'lib qolmasligi uchun alohida fon komponenti sifatida Worker'da sozlanuvchi interval (`Capture:CachePruneIntervalHours`, default 6) va retention (`Capture:CacheRetentionDays`, default 30) bilan ishga tushirildi.
27. **`CacheStats Stats()` metodi** — Monitoring uchun keshdagi qatorlar soni va diskdagi SQLite fayl hajmini qaytaruvchi metod kiritildi.
28. **`_sync.Enqueue` o'rniga durativ `capture_outbox` jadvali** — Task 6 gacha sinxronizatsiya mijozi, kalitlar va shifrlash yo'q. tdesktop kalit yo'qligida hodisani tashlab yuboradi (`if (!KeysAvailable()) return;`), lekin capture xizmatida bu ma'lumot yo'qolishiga olib kelishi sababli xabarlar SQLite fayldagi `capture_outbox` jadvaliga durativ saqlanadi.
29. **`TdIdMapper` orqali identifikatorlar moslashtirilishi** — TDLib va tdesktop identifikatorlari tubdan farq qiladi: `peer_id` tdesktop kabi tip bitlari (`<< 48`) bilan, server `msg_id` esa faqat quyi 20 biti 0 bo'lganda `tdlib_id >> 20` orqali olinadi. Maxfiy chatlar (secret chats) qamrab olinmaydi (`null`).
30. **`NoneCaptureScope` fail-closed xavfsizlik** — Task 5 doirasida haqiqiy scope joriy qilinguncha xizmat hech narsani saqlamaydigan placeholder bilan xavfsiz holatda (fail-closed) turadi.
31. **`activity` alohida Task 4b ga ajratilishi** — `updateUserStatus` va `updateUser` hodisalarini qayta ishlash, status kodlash hamda tdesktop shovqin filtrini aniq ko'chirish alohida topshiriq sifatida ajratildi.
32. **Capture scope: 3 ta mustaqil qaror va tdesktop zanjiri** — Plandagi bitta `ShouldCapture` o'rniga tdesktop kabi uchta mustaqil qaror (`ShouldCache`, `ShouldAntiDelete`, `ShouldAntiEdit`) va 4 qatlamli iyerarxiya (Server Block > Server Allow > Synced Snapshot > DefaultEnabled) joriy qilindi. Sinxronlangan sozlamalar uchun `ISyncedScopeSettingsSource` choki qoldirildi (Task 6 gacha null qaytaradi). Preflight Block/Allow ro'yxatlaridagi har bir element decimal int64 ekani va ular kesishmasligini tekshiradi (xatoliklarda peer_id chiqmaydi).
33. **Activity capture va shovqin filtri (Plan 05 Task 4b)** — Foydalanuvchi faolligi (`status`, `name`, `username`) tdesktop bilan baytma-bayt mos kodlandi. Status holatlari (`online:<expires>`, `offline:<was_online>`, `recently`, `within_week`, `within_month`, `long_ago`; `empty` hech qachon yozilmaydi — `b6d3e22`), ism (`langFullName`), username (`active_usernames[0]` yoki `editable_username`) va diskriminator (`SHA256(field)[0:8] & 0x7FFFFFFFFFFFFFFF`) tdesktop'ga to'liq moslandi. 60 soniyalik offline bump shovqin filtri (`|oldAge - newAge| < 60`) SQLite'dagi `activity_latest` jadvali bilan atomar tranzaksiyada birlashtirildi. Xavfsizlik uchun alohida `IActivityScope` (Server Exclude > Server Include > Synced Snapshot > TrackAllContacts) va Preflight Check 6 kiritildi. Maxfiy ma'lumotlar (status, ism, username, peer_id) hech qachon loglarga yozilmaydi.
34. **`edited` yozuvlarida Telegram'ning `edit_date` ishlatilishi va `pending_edits` juftlash mexanizmi (Plan 05 Task 4c)** — Spec §3.1/§3.2 talabiga ko'ra `edited` yozuvlarining `occurred_at` vaqti tahrir qilingan vaqt (`edit_date`) bo'lishi shart. TDLib tahrirni ikki alohida yangilanishga (`updateMessageContent` va `updateMessageEdited`) ajratgani sababli ular `pending_edits` SQLite jadvalida `(chat_id, message_id)` bo'yicha bog'lanadi. Qaysi biri birinchi kelishidan qat'i nazar (content-then-edited yoki edited-then-content) juftlanib `capture_outbox` ga chiqariladi. Oraliq tahrirlar yo'qolmasligi uchun birinchi tahrir zaxira vaqt bilan darhol chiqariladi. Agar `updateMessageEdited` kelmasa, sozlanuvchi timeout (`Capture:EditPairingTimeoutSeconds`, standart 60s) asosida `SweepPendingEdits` orqali zaxira `occurred_at = msg_date` bilan chiqariladi. Tozalash har yangilanishda (soniyasiga ko'pi bilan bir marta, xatosi update'ni to'xtatmaydi) va `PeriodicCachePruner` da (ishga tushishda va davriy) `TimeProvider` orqali amalga oshiriladi. Xabar o'chirilganda kutayotgan tahrir avval chiqariladi.
35. **Capture sync mijozi va outbox push arxitekturasi (Plan 05 Task 6a)** —
    - **Yangi konfiguratsiya kalitlari**: `Capture:Sync:Enabled` (bool, default false, bo'sh bo'lganda false, faqat aniq `true` qiymatda ishlaydi), `Capture:Sync:ServerUrl` (string, preflight da `https://` yoki `http://localhost|127.0.0.1` ekani tekshiriladi), `Capture:Sync:MasterKeyPath` (string, default `/var/lib/customsync-capture/master.key`), `Capture:Sync:StatePath` (string, default `/var/lib/customsync-capture/device-state.json`), `Capture:Sync:IntervalSeconds` (int, default 30s), `Capture:Sync:PushBatchSize` (int, default 500 ta qator yoki taxminan 5 MB).
    - **Kalitlar va shifrlash**: 32-bayt master kalitdan HKDF-SHA256 orqali 3 ta subkey olinadi (`customsync-content-v1`, `customsync-peer-v1`, `customsync-account-v1`). AES-256-GCM har bir yozuv uchun yangi 12-bayt tasodifiy nonce ishlatadi, AAD yo'q, 16-bayt autentifikatsiya tegi. Tarmoqqa `ciphertext ‖ tag` (birlashtirilgan) Base64 qilib uzatiladi.
    - **Outbox -> SyncRecord va §0.14 validatsiyasi**: `account_hash` = HMAC-SHA256(account_key, account_id)[0..16] hex, faqat `activity` uchun `""` (bo'sh satr). `peer_hash` = HMAC-SHA256(peer_key, peer_id)[0..16] hex barcha turlar uchun (account-less formula). §0.14 talabiga ko'ra `payload_json` ichidagi `account_id` va `peer_id` outbox qatoridagi bilan solishtiriladi; zaharlangan qatorlar (poisoned rows) serverga hech qachon yuborilmaydi, bazada qoldiriladi va karantin qilinadi.
    - **CLI va fayl xavfsizligi**: `--set-key` (interaktiv 64 hex belgili kiritish, ekranga chiqarmasdan, atomar yozish), `--enroll` (interaktiv enrollment code va device name, `POST /api/v1/devices/enroll` chaqirib natijani atomar yozish). Linux/macOS tizimlarida fayl ruxsatnomasi `0600` ekani tekshiriladi (boshqacha bo'lsa xato beradi; Windows'da esa hujjatlashtirilgan sabab bilan o'tkazib yuboriladi).
    - **Token aylanishi va krash oynasi (Crash Window)**: Access token faqat xotirada saqlanadi, muddati tugashiga 60s qolganda yoki 401 qaytganda yangilanadi. Yangilangan `refresh_token` yangi access token ishlatilishidan **AVVAL** diskka (`StatePath`) atomar yozilishi shart. Agar server token'ni yangilasa, lekin yangi token diskka yozilishidan oldin xizmat to'xtab qolsa/krash bo'lsa (refresh-token rotation crash window), serverdagi eski refresh token bekor qilingan bo'ladi va keyingi refresh 401 qaytaradi. Bunday holatda sync mijozi xavfsiz to'xtaydi, outbox qatorlari bazada saqlanib qoladi va egasi xizmatni `--enroll` orqali qayta ro'yxatdan o'tkazishi kerak. Refresh vaqtidagi 401 xatoligi outbox'ni o'chirmaydi.
    - **Outbox push va qayta ishlash**: Push natijasida `created`, `duplicate`, `superseded` bo'lgan qatorlar bazadan **faqat birlamchi kalit `id` bo'yicha** o'chiriladi (unique key bo'yicha emas — parvoz vaqtidagi yangi versiyalar o'chib ketmasligi uchun). `error` yoki javobda tushib qolgan qatorlar uchun `retry_count` oshirilib, `next_retry_at` (1s..300s eksponentsial backoff) belgilanadi. `400 batch_too_large` xatoligida batch hajmi ikkiga bo'linadi (min 1). 5xx yoki tarmoq xatolarida butun sikl kechiktiriladi (1s..300s).
36. **`--set-key` parol o'ramidan master kalitni ochish va `Capture:Sync:MaxWrapIterations` (Plan 05 Task 6a-2)** —
    - **Parol o'ramidan kalitni ochish**: Spec §4.4.0 talabiga binoan tdesktop master kalitni to'g'ridan-to'g'ri ko'rsatmaydi — u serverdagi parol o'rami (`passphrase` wrap) orqali ochiladi. `SyncCrypto.UnwrapMasterKey` PBKDF2-SHA256 (KEK) va AES-256-GCM yordamida master kalitni oladi. KEK va oraliq buferlar xotirada darhol tozalanishi (`CryptographicOperations.ZeroMemory`) kafolatlangan. Noto'g'ri parol yoki teg mos kelmasligi `KeyUnwrapStatus.WrongPassphrase` qaytaradi va istisno bilan yiqilmaydi.
    - **Iteratsiyalar chegarasi va Preflight**: DoS va CPU qotib qolishining oldini olish uchun `Capture:Sync:MaxWrapIterations` (standart 10,000,000) sozlamasi va CapturePreflight da musbat butun son ekani tekshiruvi kiritildi.
    - **Token aylanishi va 401 holatini qayta tekshirish**: Token refresh va yangilangan refresh tokenni yangi access token ishlatilishidan avval faylga saqlash mantig'i umumiy `RefreshAndPersistTokenAsync` ga birlashtirildi. Agar runner refresh paytida 401 xatoligi olsa, `--set-key` yangi token yozgan bo'lishi ehtimolini hisobga olib diskdagi holat faylini qayta o'qiydi va tokenni yangilangan bo'lsa refresh'ni bir marta qayta urinadi.
    - **Egasi uchun joylashtirish (deployment) ketma-ketligi**:
      1) `--enroll` orqali qurilmani serverda ro'yxatdan o'tkazish;
      2) `--set-key` orqali serverdagi parol o'ramidan master kalitni ochib `master.key` ga saqlash;
      3) `appsettings.Production.json` da `Capture:Sync:Enabled` ni `true` qilib xizmatni ishga tushirish.
37. **Setting yozuvlarini pull qilish, verifikatsiya va scope snapshot'lari (Plan 05 Task 6b)** —
    - **Server kind filtri**: `/api/v1/sync/pull` ga ixtiyoriy `kind` query parametri qo'shildi (`PullAsync(since, limit, kind = null)`). Berilgan holda faqat shu turdagi qatorlar `seq > since` tartibida sahifalanadi, `next_since` oxirgi qaytgan `seq` (yoki 0 qator bo'lsa `since`), `has_more` odatdagidek. Noto'g'ri/noma'lum kind berilsa `400 Bad Request` qaytaradi; `kind` berilmaganda avvalgi xatti-harakat baytma-bayt saqlanadi.
    - **Capture pull konfiguratsiyasi**: `Capture:Sync:PullBatchSize` (int, default 500) va `Capture:Sync:MaxPullPagesPerCycle` (int, default 20) `CapturePreflight` da musbat butun son ekani tekshiriladi.
    - **Yozuvlarni tekshirish (6 bosqich)**:
      1) `kind == "setting"`;
      2) `record_id == RecordId.Compute(...)`;
      3) `SyncCrypto.DecryptPayload(contentKey, nonce, payload)` AES-256-GCM orqali muvaffaqiyatli ochiladi;
      4) Payload JSON `{key, value, account_id, peer_id}` barchasi satr, `peer_id == "0"`, `HMAC(peer_key, "0") == peer_hash`, `HMAC(account_key, account_id) == account_hash` (§0.14);
      5) `msg_id == ActivityMapper.DiscriminatorFor(key)`;
      6) `key` 11 ta kanonik scope kalitidan biri (noma'lum sozlama kalitlari xatosiz jimgina e'tiborsiz qoldiriladi).
      - **Akkaunt ajratmasi bo'yicha muhim qaror (§3.2.1a)**: `setting` yozuvlari `account_hash` bo'yicha **filtrlanmaydi** — egasining master kaliti ochadigan har qanday akkauntdan kelgan sozlamalar qabul qilinadi.
    - **LWW birlashtirish va atomar kursor tranzaksiyasi**: Har bir kalit bo'yicha eng katta `(occurred_at, record_id)` g'olib bo'ladi (`record_id` durang bo'lganda leksikografik taqqoslanadi). Eskiroq yozuv keyinroq kelsa ham yangirog'ini bosib ketmaydi. SQLite `synced_settings` va `sync_state` jadvallari bitta tranzaksiyada yangilanadi — sahifani birlashtirish va `pull_cursor` bir tranzaksiyada commit qilinadi. Krash bo'lganda kursor ham, sozlamalar ham o'zgarmaydi, keyingi siklda sahifa qayta o'qiladi (at-least-once, K4 idempotent).
    - **SQLite v2 -> v3 migratsiyasi**: `PRAGMA user_version = 3` ga ko'tarildi. Yangi `synced_settings` va `sync_state` jadvallari tranzaksiya ichida yaratiladi, mavjud ma'lumotlar to'liq saqlanadi.
    - **Scope snapshot'lari va fail-closed xavfsizlik**:
      - `ScopeSettingsSnapshot` (8 xabar kaliti: `scope.whitelist`, `scope.blacklist`, `scope.wl_categories`, `scope.bl_categories`, `scope.antidelete_global`, `scope.antiedit_global`, `scope.antidelete_per_peer`, `scope.antiedit_per_peer`);
      - `ActivityScopeSettingsSnapshot` (3 faollik kaliti: `scope.activity_track_all_contacts`, `scope.activity_include`, `scope.activity_exclude`).
      - Barcha kalitlar mavjud va to'g'ri bo'lsagina snapshot yaratiladi; bitta kalit yetishmasa yoki bitta ro'yxatda nokanonik peer ID bo'lsa butun kalit bekor qilinadi va snapshot `null` bo'ladi.
      - **Eski plan eslatmasi o'zgartirildi**: "missing activity keys → tdesktop defaults" qarori bekor qilinib, tdesktop har startda barcha 11 kalitni yuborgani sababli to'liq bo'lmagan to'plam "hali sinxronlanmagan" deb qaraladi va server standartiga (fail-closed / capture nothing) qaytadi.
      - `SyncedScopeSettingsSource` `ISyncedScopeSettingsSource` va `ISyncedActivityScopeSettingsSource` ni amalga oshiradi, snapshot'lar `Interlocked.Exchange` orqali atomar almashtiriladi (o'quvchilar yarim qurilgan holatni ko'rmaydi). Xizmat ishga tushganda tarmoqqa chiqmasdan oldin SQLite'dagi qatorlardan snapshot tiklanadi.
      - DI da `services.Replace` orqali `AddCaptureHandlers` qo'ygan `Null...` manbalar to'liq almashtiriladi.
      - Maxfiylik: sozlama qiymatlari, payload'lar, tokenlar, kalitlar, `account_id` yoki `peer_id` hech qachon loglanmaydi.

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
