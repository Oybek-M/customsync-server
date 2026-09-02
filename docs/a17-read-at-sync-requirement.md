# A17 — `read_at` maydonini sync qamroviga kiritish

**Sana:** 2026-08-31
**Manba:** foydalanuvchi so'rovi
**Holat:** 🔴 OCHIQ — tdesktop tomoni implement qilinmoqda
**To'liq dizayn:** `tdesktop/docs/superpowers/specs/2026-08-31-a17-message-read-time-design.md`

---

## Qisqacha

tdesktop tomonida `actioned_messages` jadvaliga yangi ustun
qo'shilmoqda:

```sql
read_at INTEGER NOT NULL DEFAULT 0
```

Bu — arxivlangan xabarning **suhbatdosh tomonidan o'qilgan vaqti**.
AntiDelete xabarni saqlaydi, lekin hozir o'qilgan vaqt yo'qoladi.

| Qiymat | Ma'nosi |
|---|---|
| `0` | Noma'lum — hali aniqlanmagan |
| `> 0` | Unix timestamp — o'qilgan lahza |
| `-1` | Maxfiylik cheklovi |
| `-2` | Xabar juda eski (server bilmaydi) |

## 🔴 Sxema versiyasiga ta'siri

**tdesktop sxemasi v13 shu ish uchun band bo'ldi.**

`sync_outbox` + `sync_state` jadvallari uchun rejalashtirilgan
versiya endi **v14**.

Band versiyalar tarixi:

| Versiya | Nima uchun |
|---|---|
| v9 | placeholder tozalash |
| v10 | `account_id` (akkaunt izolyatsiyasi) |
| v11 | `activity_history.source` |
| v12 | story backfill |
| **v13** | **`actioned_messages.read_at` (A17)** |
| **v14** | ← Track C uchun keyingi bo'sh versiya |

## Sync protokoliga ta'siri

### `record_id` ga ta'sir qilmaydi

`read_at` — mavjud `message` kind'ining yangi **maydoni**, yangi
kind emas. Ya'ni `record_id` formulasi (spec §0.12) o'zgarmaydi va
test vektorlarini qayta generatsiya qilish **shart emas**.

### Payload'ga qo'shiladi

`message` kind'ining payload'iga `read_at` maydoni kiradi.

### Konflikt qoidasi (YANGI — spec ga yozilishi kerak)

Ikki qurilma bir xil xabar uchun turli `read_at` bersa:

1. **Aniq qiymat noma'lumdan ustun.** `0` — "bilmayman" degani,
   har qanday nolga teng bo'lmagan qiymat undan ustun turadi.
2. **Ikkalasi ham musbat bo'lsa — KICHIKROG'I yutadi.** O'qilgan
   lahza bitta; kichikroq qiymat haqiqiy birinchi o'qishga yaqinroq.
3. **Musbat manfiydan ustun.** `-1`/`-2` — "urinib ko'rdik,
   bo'lmadi" degani; bir qurilma haqiqiy vaqtni bilsa, o'sha yutadi.

Ya'ni ustuvorlik: `musbat (eng kichigi)` > `manfiy` > `0`.

**Nima uchun:** bir qurilma xabar o'qilgan lahzada onlayn bo'lgan va
real-vaqt signalini olgan bo'lishi mumkin, boshqasi esa keyinroq
ulanib `MESSAGE_TOO_OLD` olgan. Bilgan tomon har doim ustun.

## Bajarilishi kerak

- [ ] Spec §0 ga yangi qaror sifatida yozish (`read_at` konflikt qoidasi)
- [ ] `message` kind payload sxemasiga `read_at` qo'shish
- [ ] `sync_outbox`/`sync_state` migratsiyasini **v14** deb rejalash
- [ ] `CHANGELOG.md` ga yozuv

## Tartib

tdesktop tomoni **oldin** tugaydi (u ma'lumot manbai). Track C bu
maydonni faqat 02-plan (tdesktop agenti) bosqichida ko'radi, ya'ni
**shoshilinch emas** — lekin sxema versiyasi surilishi (v13 → v14)
**hoziroq** hisobga olinishi kerak, aks holda migratsiya raqami
to'qnashadi.
