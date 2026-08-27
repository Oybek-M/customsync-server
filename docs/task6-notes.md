# Task 6 — plan matnidagi tuzatishlar

To'liq handoff emas. Umumiy narsalar `PROGRESS.md` da — avval uni o'qing.

Plan: `01a-backend-foundation.md`, `## Task 6: JWT chiqarish va
endpoint'lar`.

⚠️ Task 6 plandagi eng zaif task: u **birorta avtomatik test
yozmaydi** va tekshiruvni qo'lda `curl` ga qoldiradi. Quyidagi
tuzatishlar shuni ham hal qiladi.

---

## 0. Paketni versiya bilan qo'shing

```bash
dotnet add src/CustomSync.Api package Microsoft.AspNetCore.Authentication.JwtBearer --version 8.0.*
```

Versiyasiz `dotnet add` 10.x ni oladi va `net8.0` bilan mos kelmaydi
(`NU1202`). Bu loyihada uch marta uchragan.

---

## 1. 🔴 Step 5 va 7 ni BAJARMANG — o'rniga integratsion test yozing

Plan `dotnet run` + `curl` bilan qo'lda tekshirishni aytadi. Ikki
sabab bilan bajarilmaydi:

- **Bu loyihada server ishga tushirilmaydi.** Qat'iy qoida.
- **K6 buziladi.** Qolgan hamma task avtomatik testga ega; Task 6
  faqat qo'lda tekshirilsa, u yagona test qoplamasiz qism bo'lib
  qoladi va keyingi har bir o'zgarish uni jimgina buzishi mumkin.

O'rniga `WebApplicationFactory<Program>` bilan yozing —
`tests/CustomSync.Tests/HealthTests.cs` da tayyor namuna bor.

Yangi fayl: `tests/CustomSync.Tests/DeviceEndpointsTests.cs`.
Kamida shu to'rttasi:

| Test | Kutilgan |
|---|---|
| Kod bilan `POST /enroll` | 200, javobda `deviceId`, `refreshToken`, `accessToken`, `expiresAt` |
| O'sha kod bilan ikkinchi marta `POST /enroll` | 400 `invalid_or_used_code` |
| `POST /refresh` to'g'ri token bilan | 200, **yangi** `refreshToken` (eskisidan farqli) |
| Tokensiz `GET /api/v1/devices` | 401 |

Kodni test ichida olish uchun `DeviceService.CreateEnrollmentCodeAsync()`
ni to'g'ridan-to'g'ri chaqiring (`WebApplicationFactory.Services` dan
scope oling) — `POST /codes` autentifikatsiya talab qiladi va
tovuq-tuxum muammosini beradi.

🔴 **Assertion'larni faqat O'Z yozuvingizga qarating.**
`WebApplicationFactory` haqiqiy dev bazasiga ulanadi (HealthTests
allaqachon shunday) va u boshqa testlarning qurilmalarini ham saqlaydi.
"Umumiy qurilmalar soni = 1" degan tekshiruv **yiqiladi** — bu xato
Task 5 da allaqachon bir marta yuz bergan. Yaratgan `deviceId`
yoki `name` bo'yicha filtrlang.

---

## 2. 🔴 Imzo kaliti: placeholder bilan ishlab ketmasin

Plan `appsettings.json` ga shuni qo'shishni aytadi:

```json
"SigningKey": "CHANGE_ME_AT_LEAST_32_BYTES_LONG_RANDOM"
```

Bu satr 32 baytdan uzun, ya'ni HMAC-SHA256 uni **qabul qiladi** va
tizim jimgina ishlaydi — hammaga ma'lum, gitga commit qilingan kalit
bilan. Kimdir bu repo'ni ko'rsa, istalgan qurilma uchun amal
qiladigan token yasay oladi.

**Bajaring:**

1. `appsettings.json` da `Issuer` va `Audience` qolsin, `SigningKey`
   esa **bo'sh satr** (`""`) bo'lsin — placeholder emas.
2. Haqiqiy kalit `appsettings.Development.json` ga yoziladi — u
   `.gitignore` da (DB paroli bilan bir xil naqsh).
   Generatsiya: `openssl rand -base64 48` yoki PowerShell'da
   `[Convert]::ToBase64String((1..48|%{Get-Random -Max 256}))`.
3. `Program.cs` da, `builder.Build()` dan **oldin**, tez yiqiladigan
   tekshiruv qo'ying:

```csharp
var signingKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey sozlanmagan yoki 32 belgidan qisqa. " +
        "appsettings.Development.json ga tasodifiy kalit yozing.");
}
```

Plandagi `config["Jwt:SigningKey"]!` dagi `!` ni olib tashlang —
u muammoni yashiradi. `JwtIssuer` tekshirilgan qiymatga tayansin.

---

## 3. ⚠️ Bu qarorni MEN emas, foydalanuvchi qabul qiladi — so'rang

Plan `RequireAuthorization()` ni hech qanday siyosatsiz ishlatadi,
ya'ni **har qanday ro'yxatdan o'tgan qurilma** quyidagilarni qila
oladi:

- `POST /devices/codes` — yangi qurilma ulash uchun kod yaratish
- `DELETE /devices/{deviceId}` — **istalgan** qurilmani bekor qilish
- `PUT /settings/{key}` — istalgan server sozlamasini o'zgartirish

Ya'ni bitta o'g'irlangan telefon butun serverni egallaydi. Plan
`DELETE` endpoint'iga `ClaimsPrincipal user` parametrini olgan,
lekin uni **ishlatmagan** — muallif bu yerda tekshiruv bo'lishi
kerakligini bilgan, lekin yozmagan.

**Buni o'zingiz hal qilmang.** Plandagidek yozing, keyin hisobotda
shu bandni aniq eslatib o'ting. Qaror (admin roli, qurilma egaligi,
yoki alohida admin tokeni) foydalanuvchi bilan kelishiladi.

---

## 4. ⚠️ Bekor qilingan qurilma tokeni darhol o'lmaydi

`RefreshAsync` `RevokedAt` ni tekshiradi, lekin **allaqachon
berilgan** JWT `auth.jwt_lifetime_minutes` (standart 60 daqiqa)
tugagunicha amal qiladi. Ya'ni bekor qilingan qurilma bir soatgacha
kirish huquqini saqlaydi.

`DeviceService.IsActiveAsync` mavjud, lekin hech qayerga ulanmagan.

Bu JWT'ning odatiy murosasi — **hozir tuzatmang**, faqat hisobotda
eslatib o'ting va `RevokeAsync` ustiga bitta izoh qo'ying.

---

## 5. Step 5 ni butunlay tashlab keting

U serverni ishga tushirib, `enrollment_codes` bo'shligini tekshirishni
aytadi va "bootstrap kodi keyingi qadamda hal qilinadi" deb tugaydi.
Hech narsa qilmaydigan qadam.

Step 6 (`--create-enrollment-code` CLI) esa **kerak** — uni yozing.
U tovuq-tuxum muammosini hal qiladi: kod yaratish autentifikatsiya
talab qiladi, birinchi qurilmada esa token yo'q.

---

## Tugallanganlik mezoni

- `dotnet test` → **27 test** (hozir 23 + §1 dan 4 ta).
- `dotnet build` → 0 warning.
- K6: yangi testlar yiqilganini **ko'rgan** bo'lishingiz kerak.
- `appsettings.json` da haqiqiy imzo kaliti **yo'q**.
- `git status` da `appsettings.Development.json` **ko'rinmaydi**.
- Bitta commit, K7: sarlavha imperativ, tana **nima uchun**ni
  tushuntiradi (nima qilinganini diff ko'rsatadi), `Co-Authored-By`
  yo'q.

Son 27 dan farq qilsa — to'xtang va xabar bering.

## Hisobotda albatta yozing

1. `dotnet test` chiqishi.
2. §3 (avtorizatsiya hammaga ochiq) va §4 (bekor qilish kechikishi) —
   ikkalasi ham ochiq qolgani.
3. Planda noto'g'ri yoki noaniq ko'ringan boshqa narsa — testlar
   o'tgan bo'lsa ham ayting.
