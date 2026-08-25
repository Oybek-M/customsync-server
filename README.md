# customsync-server

Multi-device sync backend — CustomMod ekotizimining server qismi.

**Holat:** ⚪ implement boshlanmagan. Spec va planlar tayyor.

---

## 🔴 BIRINCHI NAVBATDA O'QING

Bu loyihaning spec va planlari **tdesktop repo'sida** turadi va
u yerda **yagona nusxa** bo'lib qoladi. Bu yerda nusxa saqlanmaydi
— nusxa olingan hujjat darhol eskiradi.

```
C:\TBuild\tdesktop\docs\
```

| Nima | Yo'l |
|---|---|
| **Har sessiya shundan boshlanadi** | `docs/sync-protocol/STATUS.md` |
| Protokol o'zgarishlari | `docs/sync-protocol/CHANGELOG.md` |
| **Test vektorlari (kontrakt)** | `docs/sync-protocol/test-vectors.json` |
| Spec — **§0 REVIZIYA dan boshlang** | `docs/superpowers/specs/2026-07-29-multi-device-sync-backend-design.md` |
| Planlar indeksi | `docs/superpowers/plans/2026-07-29-multi-device-sync-00-index.md` |

⚠️ Spec asosiy matni 2026-07-29 da yozilgan. **§0 REVIZIYA** (2026-08-25)
undan **ustun turadi** — 11 ta qaror bor.

---

## Tartib

```
01a  backend poydevori     ← SHUNDAN BOSHLANADI
01b  backend sync yadrosi
02   tdesktop sync agenti   (tdesktop repo'sida bajariladi)
03   server-controller web app
04   storage lifecycle
05   always-on capture service
06   reliz boshqaruvi
```

## Stack

| Qism | Texnologiya |
|---|---|
| `server-backend` | .NET 8 + PostgreSQL |
| `server-controller` | Vue 3 + Vite |
| Deployment | Contabo VPS (Ubuntu), systemd + Nginx |

## Buzilmaydigan qoidalar

1. **Local-first.** Server o'chsa mijozlar to'liq ishlashda davom
   etadi. Sync — qo'shimcha qatlam, majburiyat emas.
2. **Server ishonchli tomon EMAS.** Butun kontent E2E shifrlangan;
   server faqat opaque blob'larni saqlaydi va `peer_hash` bo'yicha
   guruhlaydi.
3. **Imzo lokalda.** Reliz paketlarining maxfiy kalitlari serverga
   hech qachon chiqmaydi (plan 06).
4. **`test-vectors.json` — kontrakt.** Kripto implement qilinganda
   birinchi ish — shu vektorlarni qayta hosil qilish.
5. **Konfiguratsiya kodda bo'lmaydi** — `server_settings` jadvalida
   va web app'dan tahrirlanadi (K1 qoidasi).

## Nima uchun hujjatlar bu yerda emas

Ikki loyiha bir xil protokolni bajaradi. Hujjat ikki joyda tursa,
ular sezdirmay bir-biridan uzoqlashadi va interop buziladi — bu
ko'p platformali loyihalarda eng keng tarqalgan nosozlik sababi.

Shuning uchun: **bitta nusxa, ko'p havola.**

Protokolga tegadigan o'zgarish qilsangiz —
`tdesktop/docs/sync-protocol/CHANGELOG.md` ga bir qator yozing.
