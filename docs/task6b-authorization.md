# Task 6b — avtorizatsiya rollari va darhol bekor qilish

Bu Task 6 dan keyin, **plan 01b dan OLDIN** bajariladi.

Sabab: 01b sync endpoint'larini quradi. Ular yassi auth modeli ustiga
qurilsa, keyin rol qo'shish butun sync qatlamini qayta ochishni talab
qiladi. Hozir migratsiya arzon — birinchi deploydan keyin emas.

Umumiy qoidalar `PROGRESS.md` da.

---

## Nima uchun kerak

Hozir har qanday ro'yxatdan o'tgan qurilma quyidagilarni qila oladi:

- `POST /devices/codes` — yangi qurilma ulash uchun kod yaratish
- `DELETE /devices/{id}` — **istalgan** qurilmani bekor qilish
- `PUT /settings/{key}` — istalgan server sozlamasini o'zgartirish

Ya'ni bitta o'g'irlangan telefon serverni egallaydi. Eng og'iri:
`retention.*_days` ni `1` ga qo'yib butun markaziy arxivni yo'q qilish.

---

## Qaror: A (rol) + C (o'z-o'ziga ruxsat), B esa CLI shaklida

Foydalanuvchi 2026-08-26 da uchala variantni uyg'unlashtirishni so'radi.
Amalda ular teng emas:

| Variant | Qaror |
|---|---|
| **A — JWT `role` claim** | ✅ asos |
| **C — o'z-o'ziga ruxsat** | ✅ A ustiga qo'shiladi |
| **B — statik admin kaliti** | ❌ umumiy auth yo'li sifatida QO'SHILMAYDI |

🔴 **B nima uchun qo'shilmaydi.** Config'dagi statik admin kaliti
rotatsiya qilinmaydi, kim ishlatganini audit qilib bo'lmaydi (Task 7
aynan audit log haqida) va web app uni o'zida saqlashga majbur bo'ladi
— ya'ni sir yana bir joyda ko'payadi. U mavjud modelni kuchaytirmaydi,
zaiflashtiradi.

B ning **foydali maqsadi** — qurilma tokenidan mustaqil admin yo'li —
allaqachon qoplangan: `--create-enrollment-code` CLI. U serverga shell
kirishini talab qiladi, bu esa baribir eng yuqori huquq, shuning uchun
yangi hujum yuzasi qo'shmaydi. Task 6b unga `--admin` bayrog'ini
qo'shadi, xolos.

---

## 1. Sxema — yangi migratsiya

`devices` jadvaliga:

```
role TEXT NOT NULL DEFAULT 'device'     -- 'device' | 'admin'
```

`enrollment_codes` jadvaliga:

```
grants_role TEXT NOT NULL DEFAULT 'device'
```

Kod qanday rol berishini **o'zi** belgilaydi — shunda admin qurilma
ulash oddiy qurilma ulashdan ajralib turadi va tasodifan admin
yaratib qo'yish mumkin emas.

⚠️ Migratsiyalarni **birlashtirmang** — yangi migratsiya qo'shing
(`PROGRESS.md` dagi eslatma).

---

## 2. Rol claim'i

`JwtIssuer.IssueAsync` `deviceId` bilan birga rolni ham oladi va
tokenga qo'shadi:

```csharp
claims: [
    new Claim(ClaimTypes.NameIdentifier, deviceId),
    new Claim(ClaimTypes.Role, role)
]
```

`Program.cs` da siyosat:

```csharp
builder.Services.AddAuthorization(o =>
    o.AddPolicy("admin", p => p.RequireRole("admin")));
```

---

## 3. Endpoint'lar bo'yicha ruxsat

| Endpoint | Kim |
|---|---|
| `POST /devices/enroll` | ochiq (kodning o'zi maxfiy) |
| `POST /devices/refresh` | ochiq (refresh token o'zi maxfiy) |
| `GET /devices` | **admin** |
| `POST /devices/codes` | **admin** |
| `DELETE /devices/{id}` | **admin**, YOKI o'z `deviceId` si (C) |
| `GET /settings` | **admin** |
| `PUT /settings/{key}` | **admin** |

`DELETE` dagi C qoidasi — qurilma o'zini "chiqarish" imkoniyati:

```csharp
var callerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
var isAdmin  = user.IsInRole("admin");
if (!isAdmin && callerId != deviceId) return Results.Forbid();
```

Plandagi ishlatilmagan `ClaimsPrincipal user` parametri aynan shu
uchun turgan edi.

---

## 4. 🔴 Darhol bekor qilish — sync sekinlashmasdan

**Talab:** bekor qilingan qurilma tokeni **darhol** ishlamay qolsin,
lekin sync hot-path'ga qo'shimcha DB so'rovi **tushmasin**.

Har so'rovda `IsActiveAsync` chaqirish bu talabni buzadi — 01b da
push/pull juda tez-tez chaqiriladi.

**Yechim: xotiradagi bekor qilinganlar to'plami.**

Yangi singleton, masalan `DeviceRevocationCache`:

```csharp
public sealed class DeviceRevocationCache
{
    private readonly ConcurrentDictionary<string, byte> _revoked = new();

    public bool IsRevoked(string deviceId) => _revoked.ContainsKey(deviceId);
    public void Add(string deviceId)       => _revoked[deviceId] = 0;

    public void Load(IEnumerable<string> revokedDeviceIds)
    {
        foreach (var id in revokedDeviceIds) _revoked[id] = 0;
    }
}
```

Ulanishi:

1. **Startup'da** bir marta yuklanadi:
   `devices WHERE revoked_at IS NOT NULL` → `Load(...)`.
   (`Program.cs` da migratsiya/`EnsureDefaultsAsync` yonida.)
2. **`DeviceService.RevokeAsync`** bazaga yozgach `cache.Add(deviceId)`
   chaqiradi — ya'ni keyingi so'rovdayoq token rad etiladi.
3. **JWT tekshiruvida** — `AddJwtBearer(o => o.Events = ...)`:

```csharp
o.Events = new JwtBearerEvents
{
    OnTokenValidated = ctx =>
    {
        var cache = ctx.HttpContext.RequestServices
            .GetRequiredService<DeviceRevocationCache>();
        var deviceId = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

        if (deviceId is null || cache.IsRevoked(deviceId))
            ctx.Fail("device_revoked");

        return Task.CompletedTask;
    }
};
```

Bu `O(1)` xotira qidiruvi — **DB so'rovi yo'q**, sync tezligiga
ta'siri yo'q.

⚠️ **Ma'lum cheklov — yozib qo'ying, hozir tuzatmang.** Bu kesh bitta
jarayonga tegishli. Server bir nechta instansiyada ishlatilsa, bir
instansiyada bekor qilish boshqasiga yetib bormaydi. Yechim —
PostgreSQL `LISTEN/NOTIFY` — plan 04/06 da ko'p instansiyali deploy
ko'rilganda qo'shiladi. Hozir bitta instansiya.

**Qo'shimcha himoya (arzon):** `auth.jwt_lifetime_minutes` ni
`60` → `15` ga tushiring. Bu `server_settings` dagi qiymat, kod
o'zgarmaydi — `SettingsService.CreateDefaults()` dagi standartni
yangilang. Kesh ishlamay qolgan holatda ham oyna 4 barobar qisqaradi.

---

## 5. Bootstrap CLI

```
dotnet run --project src/CustomSync.Api -- --create-enrollment-code --admin
```

`--admin` bo'lsa `grants_role='admin'`, aks holda `'device'`.

⚠️ Buni **siz** ishga tushirasiz — agent server ishga tushirmaydi.

---

## Testlar (K6 — avval yiqilsin)

`tests/CustomSync.Tests/AuthorizationTests.cs`:

| Test | Kutilgan |
|---|---|
| `device` roli bilan `POST /devices/codes` | 403 |
| `admin` roli bilan `POST /devices/codes` | 200 |
| `device` roli bilan `PUT /settings/{key}` | 403 |
| `device` o'zini `DELETE` qiladi | 204 |
| `device` boshqa qurilmani `DELETE` qiladi | 403 |
| Bekor qilingandan keyin **o'sha** token bilan so'rov | 401 |

Oxirgisi eng muhimi — "darhol" talabini aynan u isbotlaydi:
token bekor qilishdan **oldin** olinadi, keyin `RevokeAsync`
chaqiriladi, keyin o'sha token bilan so'rov yuboriladi.

🔴 Assertion'larni faqat o'z yozuvingizga qarating — `WebApplicationFactory`
umumiy dev bazasiga ulanadi (bu xato ikki marta yuz bergan).

---

## Tugallanganlik mezoni

- `dotnet test` → **33 test** (hozir 27 + 6 ta yangi).
- `dotnet build` → 0 warning.
- `git status` da `appsettings.Development.json` ko'rinmaydi.
- Bitta commit, K7: tana **nima uchun**ni tushuntiradi.
- Sync hot-path'da bekor qilish uchun **qo'shimcha DB so'rovi yo'q**
  (kesh orqali).

Son 33 dan farq qilsa — to'xtang va xabar bering.
