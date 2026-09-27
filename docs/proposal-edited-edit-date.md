# Protokol taklifi — `edited` yozuvida `occurred_at` = tahrir sanasi

Holat: **TAKLIF** (2026-09-27). Egasi tasdiqladi: "oraliq versiyani ham
saqlash kerak". Protokol hujjatlari (spec, `CHANGELOG.md`,
`test-vectors.json`) tdesktop repo'sida — ularni **tdesktop sessiyasi**
o'zgartiradi. Bu fayl — o'sha sessiyaga uzatiladigan asos.

## Muammo

Hozir (spec §3.1, tdesktop `custom_db.cpp` `Outbox::Enqueue(Kind::Edited,
..., msg.msgDate ...)`) `edited` uchun `occurred_at` = xabarning **asl
yuborilgan sanasi**. Demak bitta xabarning HAR tahriri bir xil
`record_id` oladi:

```
record_id = SHA256(edited ‖ account_hash ‖ peer_hash ‖ msg_id ‖ msg_date)
```

Oqibat — tahrirlar bir-birini yeb qo'yadi, va har joyda boshqacha:

| Joy | Nima saqlanadi |
|---|---|
| Lokal outbox (tdesktop `INSERT OR REPLACE`, capture xuddi shunday) | **oxirgi** tahrir; yuborilmagan oldingilari yo'qoladi |
| Server (§3.4, `observed_at` kichigi g'olib) | **birinchi kuzatilgan** tahrir; keyingilari dedup bo'lib tashlanadi |

Ya'ni A→B→C tahrirlaridan faqat bittasi qoladi, qaysi biri — vaqtga
bog'liq. Qurilmalar orasida natija har xil bo'lishi mumkin.

## Taklif

`edited` uchun `occurred_at` = **shu tahrirning Telegram `edit_date`
qiymati**.

| Manba | Qayerdan |
|---|---|
| tdesktop, xotirada yo'q xabar | `Data::Session::updateEditedMessage` — `MTPDmessage::vedit_date()` |
| tdesktop, xotiradagi xabar | `applyEdition` → `HistoryMessageEdition` / `HistoryMessageEdited::date` |
| TDLib (capture) | `updateMessageEdited.edit_date` (yoki `message.edit_date`) |

`edit_date` Telegram serveri qo'yadi — ikki qurilma bir xil tahrirni
ko'rsa, bir xil qiymat oladi. Shuning uchun:

- har tahrir o'z `record_id` sini oladi → A→B va B→C ikkalasi saqlanadi;
- bir tahrirni ikki qurilma ko'rsa → bir xil `record_id` → dedup
  avvalgidek ishlaydi (§3.1 xossasi saqlanadi);
- lokal outbox'da ham unique kalit tahrir bo'yicha farq qiladi —
  `INSERT OR REPLACE` endi hech narsani yo'qotmaydi.

**Payload:** o'zgarmaydi (`{account_id, peer_id, old_text, new_text,
is_out}`). Ixtiyoriy qo'shimcha (tdesktop sessiyasi hal qiladi):
`msg_date` — asl yuborilgan sana, UI "qachon yozilgan" ni ko'rsatishi
uchun. Qo'shimcha maydon eski o'quvchilarni buzmaydi.

**`edit_date` yo'q bo'lsa** (nazariy holat): `occurred_at` = kuzatilgan
vaqt. Bunda qurilmalararo dedup ishlamaydi (dublikat, lekin yo'qotish
emas).

## Chegaralar va o'tish davri

- **Bir soniyada ikki tahrir** — `edit_date` soniya aniqligida, ikkalasi
  bir xil `record_id` oladi va biri tushib qoladi. Amalda juda kam.
- **Eski tdesktop build'lari** asl sanani ishlatishda davom etadi → bir
  tahrir eski va yangi klientdan ikki xil `record_id` bilan kelishi
  mumkin: **dublikat, yo'qotish emas.** Klient `edited` ni
  (`peer_id`, `msg_id`, `new_text`) bo'yicha ko'rsatishda birlashtirishi
  mumkin.
- **Backend o'zgarmaydi** — `record_id` unga shaffof emas, u faqat
  saqlaydi.
- Retention/tombstone qoidalari (§0.3) ta'sirlanmaydi.

## Kim nima qiladi

| Kim | Ish |
|---|---|
| tdesktop sessiyasi | spec §3.1/§3.2 jadvaliga `edited` → `occurred_at = edit_date`; `CHANGELOG.md`; `test-vectors.json` ga `edited` vektori; tdesktop kodida ikkala yo'l (xotirada bor/yo'q) |
| customsync-server (capture, Task 4c) | `updateMessageContent` + `updateMessageEdited` juftlash: hodisa `edit_date` bilan yoziladi; `edit_date` siz content o'zgarishi (media yangilanishi va h.k.) tahrir hisoblanmaydi |
| backend | hech narsa |

Capture tomoni (Task 4c) protokol tasdiqlanib, test vektori paydo
bo'lgandan keyin boshlanadi — aks holda ikki implementatsiya yana
ajralib ketadi.
