# Plan 05 (always-on capture) uchun amaliy dalil

**Sana:** 2026-09-01
**To'liq tashxis:** `tdesktop/docs/superpowers/specs/2026-09-01-offline-deletion-gap-evidence.md`

---

## Qisqacha

Desktop klientda 6 ta o'chirilgan xabar yo'qoldi. Uchta nazariya
tekshirilib rad etildi — xabarlarning matni, sanasi va jo'natuvchisi
mahalliy keshda (`text_cache`) TO'LIQ turgan edi.

Haqiqiy sabab: ilova **oflayn** turgan paytdagi o'chirishlar uchun
Telegram `updateDeleteMessages` ni qayta yubormaydi. Qayta ulanganda
`updates.getDifference` faqat yakuniy holatni beradi.

**Bu klient kodidagi xato emas — klient-asosli yondashuvning tub
cheklovi.** Ilova yopiq bo'lsa, hech qanday mahalliy mantiq o'chirish
hodisasini ushlab qololmaydi.

## Nima uchun bu plan 05 ni oqlaydi

Bir vaqtning o'zida:

| Klient | Holati | Natija |
|---|---|---|
| CustomMod (desktop) | oflayn edi | 6 ta xabar **yo'qoldi** |
| NovaGram (telefon) | doimiy ulangan | hammasini **ushlab qoldi** |

Plan 05 — VPS'da 24/7 ishlaydigan TDLib xizmati — aynan shu
bo'shliqni yopadi. Bugungi hodisa uning ehtiyoji **nazariy emas,
amaliy** ekanini ko'rsatdi.

## Implementga ta'siri

Yangi talab qo'shilmaydi — plan 05 allaqachon shu ssenariyni
nazarda tutgan. Lekin ustuvorlikni baholashda bu dalilni hisobga
oling: muammo real foydalanuvchida, real ma'lumot yo'qolishi bilan
takrorlandi.

Plan 05 dagi mavjud cheklov o'z kuchida qoladi: TDLib ham
`updateDeleteMessages` da faqat ID oladi, shuning uchun xizmat har
bir kelgan xabarni OLDINDAN keshlashi shart.
