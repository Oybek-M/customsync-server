# Task 5 — plan matnidagi tuzatishlar

Bu **to'liq handoff emas.** Umumiy narsalar (muhit, K1–K7, §0 ustunligi,
chetlashishlar ro'yxati) `PROGRESS.md` da — uni o'qing, bu yerda
takrorlanmaydi.

Quyida faqat **Task 5 ning o'ziga xos** to'rtta nuqta: planga so'zma-so'z
ergashilsa noto'g'ri yoki yiqiladigan joylar.

Plan: `01a-backend-foundation.md`, `## Task 5: Qurilma ro'yxatdan
o'tkazish va JWT`.

---

## 1. 🔴 `Expired_code_is_rejected` testi yiqilishi kutilmoqda

`ExpireAllCodesAsync()` `ExecuteUpdateAsync` ishlatadi — u SQL'ni
to'g'ridan-to'g'ri yuboradi va **change tracker'ni yangilamaydi**
(EF hujjatida shunday yozilgan).

Testda bir xil `DbContext` ishlatiladi:
`CreateEnrollmentCodeAsync()` entity'ni qo'shib saqlaydi → u
**kuzatilayotgan** holatda qoladi. `ExpireAllCodesAsync()` bazadagi
qatorni yangilaydi, lekin xotiradagi obyekt eski `ExpiresAt` bilan
qoladi. Keyin `RedeemAsync` ichidagi `FirstOrDefaultAsync` EF identity
resolution tufayli **o'sha eski obyektni** qaytaradi →
`entry.ExpiresAt < DateTime.UtcNow` `false` bo'ladi → kod amal qilyapti
deb hisoblanadi → test yiqiladi.

**Tuzatish** — `RedeemAsync` dagi so'rovni tracker'dan mustaqil qiling:

```csharp
var entry = await db.EnrollmentCodes
    .AsNoTracking()
    .FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
```

Lekin keyin `entry.UsedAt`/`UsedBy` ni yozish uchun uni qayta
biriktirish kerak. Ikkita variant — ikkalasi ham to'g'ri, birini
tanlang va commit tanasida sababini yozing:

- `AsNoTracking()` + `db.EnrollmentCodes.Attach(entry)` va kerakli
  maydonlarni `Modified` deb belgilash;
- yoki `ExpireAllCodesAsync()` oxirida `db.ChangeTracker.Clear()`.

Ikkinchisi soddaroq, lekin u faqat test yordamchisi bo'lgani uchun
"test uchun tuzatish" hidini beradi. Birinchisi ishlab chiqarish
kodini ham to'g'rilaydi — chunki bu bug faqat testda emas: bitta
so'rov ichida kod ikki marta tekshirilsa, real API'da ham eskirgan
qiymat ishlatilishi mumkin.

---

## 2. `deviceId` kesilishi — to'qnashuv xavfi

```csharp
var deviceId = $"{platform}-{Guid.NewGuid():N}"[..24];
```

`platform` 23 belgidan uzun bo'lsa, `[..24]` GUID'ni **butunlay
kesib tashlaydi** va bir xil platformali ikki qurilma bir xil
`deviceId` oladi → PRIMARY KEY buzilishi.

Testlarda `"desktop-win"` va `"android"` ishlatilgani uchun bu
ushlanmaydi.

**Tuzatish:** `platform` uzunligini cheklang (masalan 16 belgi) yoki
GUID qismini kafolatlangan uzunlikda oling. Tanlaganingizni izohda
yozing.

---

## 3. `CreateEnrollmentCodeAsync` izohi noto'g'ri: 80 bit emas, 50 bit

```csharp
var code = Base32(RandomNumberGenerator.GetBytes(10)); // 80 bit
```

`Base32` har baytdan **faqat 5 bit** oladi (`b >> 3`), ya'ni 10 bayt →
10 belgi → **50 bit**.

50 bit bir martalik, 10 daqiqada muddati o'tadigan kod uchun amalda
yetarli — shuning uchun **kodni o'zgartirmang**, faqat izohni
to'g'rilang (`// 50 bit`), aks holda keyingi o'quvchi noto'g'ri
kafolatga tayanadi.

---

## 4. `Files:` ro'yxati Task 5 ga tegishli emas

Plan sarlavhasida `JwtIssuer.cs`, `DeviceEndpoints.cs`, `Program.cs`,
`appsettings.json` sanab o'tilgan — lekin Task 5 ning **birorta
Step'i ularga tegmaydi**. Ular **Task 6** ga tegishli.

Task 5 da faqat shu ikki fayl yaratiladi:
- `src/CustomSync.Services/DeviceService.cs`
- `tests/CustomSync.Tests/DeviceAuthTests.cs`

JWT kodini bu task'da yozmang.

---

## Tugallanganlik mezoni

- `dotnet test` → **21 test** o'tadi (hozir 17 + Task 5 dan 4 ta).
- `dotnet build` → 0 warning.
- K6: yangi testlar yiqilganini **ko'rgan** bo'lishingiz kerak.
- Bitta commit, K7 uslubi, `Co-Authored-By` yo'q.

Son 21 dan farq qilsa — to'xtang va xabar bering, testni songa
moslashtirmang.
