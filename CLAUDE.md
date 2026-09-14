# customsync-server — sessiya boshlanish yo'riqnomasi

## 🖥️ Avval: qaysi kompyuterdasiz

Bu loyiha laptop va PC'da olib boriladi. Pastdagi `C:\TBuild\tdesktop`
va `Projects programming\...` yo'llari **laptop'niki**. `hostname` ni
aniqlang va yo'llarni `<tdesktop>\docs\MACHINES.md` jadvalidan oling
(`<tdesktop>` — shu kompyuterdagi tdesktop repo; topish tartibi o'sha
faylda). Yo'l mavjudligini ishlatishdan oldin tekshiring.

## Ishni boshlashdan oldin SHU TARTIBDA o'qing

```
0. PROGRESS.md  (shu papkada)
   -> implement qayerda to'xtaganini va keyingi aniq qadamni beradi

1. C:\TBuild\tdesktop\docs\sync-protocol\STATUS.md
   → kim nimani bajardi, keyingi qadam nima

2. C:\TBuild\tdesktop\docs\sync-protocol\CHANGELOG.md
   → oxirgi sessiyadan beri protokolda nima o'zgardi

3. C:\TBuild\tdesktop\docs\superpowers\specs\
     2026-07-29-multi-device-sync-backend-design.md
   → §0 REVIZIYA dan boshlang (2026-08-25, 11 ta qaror)
   → u asosiy matndan USTUN turadi

4. C:\TBuild\tdesktop\docs\superpowers\plans\
     2026-07-29-multi-device-sync-00-index.md
   → tartib va umumiy qoidalar K1-K7

5. Joriy plan (01a dan boshlanadi) — uning boshidagi
   "REVIZIYA 2026-08-25" blokini albatta o'qing
```

## Sessiya oxirida

`STATUS.md` da bu loyihaning qatorini yangilang. Protokolga
tegdingizmi — `CHANGELOG.md` ga bir qator.

## Bu loyihada nima YO'Q

- Spec va planlarning nusxasi — ular `tdesktop/docs/` da, bitta nusxa
- `test-vectors.json` nusxasi — u ham o'sha yerda

Sabab: nusxa eskiradi va ikki loyiha sezdirmay uzoqlashadi.

## Muhim kontekst

- **Papka:** `Projects programming\Telegram\customsync-server`
  (tdesktop build daraxti 2026-08-14 da `C:\TBuild` ga ko'chgan,
  shuning uchun bu papka endi bo'sh konteyner — maxfiy kalitlar
  bilan bir joyda emas)
- **tdesktop repo:** `C:\TBuild\tdesktop`, branch `Oybek`
- **Stack:** .NET 8 + PostgreSQL, Vue 3 + Vite
- **VPS:** Contabo (Ubuntu), `/var/www/`, systemd, Nginx

## Buzilmaydigan qoidalar

1. Local-first — server o'chsa mijoz to'liq ishlaydi
2. Server ishonchli tomon emas — hamma narsa E2E shifrlangan
3. Imzo lokalda — maxfiy kalitlar serverga chiqmaydi
4. `test-vectors.json` — mashina tekshiradigan kontrakt
5. Konfiguratsiya kodda emas — `server_settings` jadvalida (K1)
6. Retention tombstone yaratmaydi (spec §0.3)
7. `sha256` shifrlashdan OLDIN hisoblanadi (spec §0.5)
