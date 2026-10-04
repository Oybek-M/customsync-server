# Deployment

Bu papkada faqat konfiguratsiya fayllari bor. Ularni VPS ga qo'llash —
qo'lda bajariladigan ish.

| Fayl | Qayerga |
|---|---|
| `customsync.service` | `/etc/systemd/system/` |
| `nginx-customsync.conf` | `/etc/nginx/sites-available/customsync` |

`sync.example.uz` — namuna domen. Ikkala faylda ham o'zingiznikiga
almashtiring.

---

## Birinchi o'rnatish

### 1. Foydalanuvchi va kataloglar

```bash
sudo useradd -r -s /bin/false customsync
sudo mkdir -p /var/www/customsync/logs /var/lib/customsync/media
sudo chown -R customsync:customsync /var/lib/customsync /var/www/customsync
```

`logs/` katalogini oldindan yaratish shart: `ProtectSystem=strict`
ostida jarayon `/var/www/customsync` ga yoza olmaydi, faqat
`ReadWritePaths` da sanab o'tilgan yo'llarga yozadi.

### 2. PostgreSQL

```bash
sudo -u postgres psql -c "CREATE USER customsync WITH PASSWORD '<parol>';"
sudo -u postgres psql -c "CREATE DATABASE customsync OWNER customsync;"
```

Migratsiyalarni qo'lda qo'llash shart emas — ilova ishga tushganda
`Database.MigrateAsync()` ni o'zi chaqiradi. Buning teskari tomoni ham
bor: migratsiya yiqilsa xizmat umuman ko'tarilmaydi. Birinchi
`systemctl start` dan keyin `journalctl` ni tekshiring.

### 3. Publish va nusxalash

```bash
dotnet publish src/CustomSync.Api -c Release -o ./publish
rsync -av ./publish/ user@vps:/var/www/customsync/
```

### 4. Sozlash

`/var/www/customsync/appsettings.Production.json`:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=customsync;Username=customsync;Password=<parol>"
  },
  "Jwt": { "SigningKey": "<kamida 32 belgi>" }
}
```

Kalit yaratish: `openssl rand -base64 48`

`Jwt:SigningKey` bo'sh yoki 32 belgidan qisqa bo'lsa, ilova **ataylab
ishga tushmaydi** (`Program.cs` da tekshiruv bor). Bu jimgina zaif kalit
bilan ishlab ketishdan ko'ra yaxshiroq — xatoni darhol ko'rasiz.

Bu faylda DB paroli va imzo kaliti bor:

```bash
sudo chown customsync:customsync /var/www/customsync/appsettings.Production.json
sudo chmod 600 /var/www/customsync/appsettings.Production.json
```

Uni **hech qachon git ga qo'shmang.**

### 5. Xizmatlar

```bash
sudo cp deploy/customsync.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now customsync
sudo systemctl status customsync
```

Tekshirish:

```bash
curl http://127.0.0.1:5080/api/v1/health
```

Keyin nginx:

```bash
sudo cp deploy/nginx-customsync.conf /etc/nginx/sites-available/customsync
sudo ln -s /etc/nginx/sites-available/customsync /etc/nginx/sites-enabled/
sudo certbot --nginx -d sync.example.uz
sudo nginx -t && sudo systemctl reload nginx
```

`certbot --nginx` sertifikat yo'llarini o'zi to'g'rilaydi. Agar
sertifikat hali yo'q bo'lsa, `nginx -t` `ssl_certificate` topilmadi deb
yiqiladi — bu kutilgan holat, certbot dan keyin qayta urinib ko'ring.

### 6. Firewall

```bash
sudo ufw allow 22,80,443/tcp
sudo ufw enable
```

Backend `127.0.0.1:5080` da tinglaydi, ya'ni tashqaridan to'g'ridan-to'g'ri
ochiq emas — faqat nginx orqali.

### 7. Birinchi qurilma kodi

```bash
cd /var/www/customsync
sudo -u customsync ASPNETCORE_ENVIRONMENT=Production \
    dotnet CustomSync.Api.dll --create-enrollment-code --admin
```

`cd` majburiy: `appsettings*.json` joriy papkadan o'qiladi (content root),
boshqa papkadan `Jwt:SigningKey` va ulanish satri topilmaydi.

Kod chop etiladi va jarayon darhol chiqadi — server ishga tushmaydi.
`--admin` bo'lmasa oddiy `device` roli beriladi. Birinchi qurilma admin
bo'lishi kerak, aks holda boshqaruv endpoint'lariga hech kim kira
olmaydi.

⚠️ Buni **4-qadamdan keyin** bajaring: bu buyruq ham `Jwt:SigningKey`
tekshiruvidan o'tadi.

---

## Yangilash

```bash
dotnet publish src/CustomSync.Api -c Release -o ./publish
sudo systemctl stop customsync
rsync -av --exclude appsettings.Production.json ./publish/ user@vps:/var/www/customsync/
sudo systemctl start customsync
```

`--exclude` shart — busiz rsync konfiguratsiyani repo dagi
placeholder'lar bilan almashtirib yuboradi va xizmat ko'tarilmaydi.

---

## Loglar

```bash
journalctl -u customsync -f          # stdout
tail -f /var/www/customsync/logs/customsync-*.log   # Serilog
```

Serilog oxirgi 14 ta kunlik faylni saqlaydi (`RetainedFileCountLimit`),
ya'ni alohida logrotate sozlash shart emas.

---

## Zaxira

Kechasi 03:00 da (cron):

```bash
pg_dump -U customsync customsync | gzip > /backup/db-$(date +%F).sql.gz
rsync -a /var/lib/customsync/media/ /backup/media/
```

Ikkalasi ham kerak: DB da media bloblarining metama'lumoti va sha256
havolalari turadi, bloblarning o'zi esa fayl tizimida. Bittasisiz
ikkinchisi foydasiz.

**MUHIM:** `/backup` VPS ning O'ZIDA bo'lmasligi kerak — VPS yo'qolsa
zaxira ham yo'qoladi. Uni boshqa xostga ko'chiring. Plan 04 buni
avtomatlashtiradi.

---

## Capture xizmati (customsync-capture)

⚠️ **OGOHLANTIRISH: Birgalikdagi xavfsizlik auditi (joint security audit) yakunlanmaguncha VPS ga deploy qilish TO'XTATILGAN.** Capture xizmati egasining Telegram akkauntiga to'liq kirish huquqiga va master kalitga ega.
Quyidagi barcha buyruqlar hali jonli tizimda bajarilmagan — ularni audit davomida tekshiring.

---

### 1. Fayllar va ruxsatlar inventari

| Yo'l | Nima saqlanadi | Egasi:guruhi va huquq | Zaxiralanadimi? |
|---|---|---|---|
| `/var/lib/customsync-capture/tdlib` | TDLib ma'lumotlar bazasi (Telegram sessiyasi; akkauntga to'liq kirish huquqi) | `customsync-capture:customsync-capture` `0700` | **YO'Q** (zaxiraga kiritilmaydi; qayta `--login` qilish orqali tiklanadi) |
| `/var/lib/customsync-capture/files` | TDLib fayllari katalogi | `customsync-capture:customsync-capture` `0700` | **YO'Q** |
| `/var/lib/customsync-capture/message-cache.db` | Xabarlar keshi (xabarlar matnlari) | `customsync-capture:customsync-capture` `0600` (katalog `0700`) | **YO'Q** |
| `/var/lib/customsync-capture/media` | Yuklab olingan medialar katalogi | `customsync-capture:customsync-capture` `0700` | **YO'Q** |
| `/var/lib/customsync-capture/device-state.json`, `master.key` | Sinxronizatsiya qurilma holati va master kalit fayllari | `customsync-capture:customsync-capture` `<= 0600` (katalog `0700`) | **YO'Q** |
| `/etc/customsync-capture/tdlib-db-key` | TDLib bazasini shifrlash kaliti (32 bayt base64) | `root:root` `0400` (yoki `customsync-capture:customsync-capture` `0400`) | **YO'Q** (hech qachon zaxiraga kiritilmaydi) |
| `/var/www/customsync-capture/appsettings.Production.json` | Konfiguratsiya (api_id, api_hash, server manzili) | `customsync-capture:customsync-capture` `<= 0640` | **YO'Q** (repo'ga ham kiritilmaydi) |

> [!CAUTION]
> **Zaxira qoidasi:** Capture holati (sessiya, kesh, kalitlar) hech qachon zaxira nusxalariga (backups) kiritilmaydi. Agar VPS yo'qolsa yoki buzilsa, yagona xavfsiz tiklash yo'li — toza tizimda yangidan `--login` va `--enroll` qilishdir.
> **Contabo snapshot'lari:** Virtual server snapshot'lari butun fayl tizimini (shu jumladan shifrlanmagan kesh va kalitlarni) to'liq o'z ichiga oladi. Snapshot'larni o'ta maxfiy deb biling va audit/testdan so'ng darhol o'chirib tashlang.

---

### 2. TDLib baza shifrlash kalitini yaratish va o'rnatish

TDLib ma'lumotlar bazasi diskda shifrlangan holda saqlanishi shart. Kalit kamida 32 baytlik tasodifiy ma'lumotning base64 satridan iborat bo'lishi kerak:

```bash
# not yet run — verify during the audit
sudo mkdir -p /etc/customsync-capture
sudo chmod 0700 /etc/customsync-capture
openssl rand -base64 32 | sudo tee /etc/customsync-capture/tdlib-db-key > /dev/null
sudo chmod 0400 /etc/customsync-capture/tdlib-db-key
sudo chown root:root /etc/customsync-capture/tdlib-db-key
```

`customsync-capture.service` unit faylida quyidagi qator mavjud:
`LoadCredential=tdlib-db-key:/etc/customsync-capture/tdlib-db-key`

Bu orqali systemd kalitni faqat xizmat ishga tushganda `$CREDENTIALS_DIRECTORY/tdlib-db-key` ga xavfsiz tarzda ulaydi. Kalitni o'zgartirish mavjud bazani o'qib bo'lmas holga keltiradi (bu holatda eski sessiyani o'chirib, yangidan `--login` qilish talab etiladi).

---

### 3. Ikki bosqichli tasdiqlash (2FA / Cloud Password)

Birinchi marta sessiyaga kirishdan (`--login`) oldin, Telegram akkauntingizda **ikki bosqichli tasdiqlash (Two-Step Verification)** yoqilgan bo'lishi shart (Telegram → Settings → Privacy and Security → Two-Step Verification).
Agar 2FA yoqilgan bo'lsa, TDLib telefon va SMS koddan so'ng bulutli parolni ham so'raydi. Bu VPS egasi bo'lmagan shaxs faqat SMS tutib olish orqali akkauntni qo'lga kiritishining oldini oladi.

---

### 4. Xavfsiz login va sozlash buyruqlari (`systemd-run --pty`)

`--login` buyrug'i oddiy terminalda `sudo -u` bilan bajarilsa, umask (odatda 0022) ochiq qolishi, `StateDirectory` va systemd `LoadCredential` sozlamalari ishlamay qolishi mumkin.
Shu sababli, CLI buyruqlarini xizmatning o'zi bilan **bir xil sandbox muhitida** ishga tushirish uchun `systemd-run --pty` dan foydalaning:

```bash
cd /var/www/customsync-capture

# 1. Telegram sessiyasiga kirish (telefon, kod, 2FA parol so'raladi):
# not yet run — verify during the audit
systemd-run --pty --same-dir \
    --unit=customsync-capture-login \
    --service-type=exec \
    --property=User=customsync-capture \
    --property=Group=customsync-capture \
    --property=WorkingDirectory=/var/www/customsync-capture \
    --property=StateDirectory=customsync-capture \
    --property=StateDirectoryMode=0700 \
    --property=UMask=0077 \
    --property=LoadCredential=tdlib-db-key:/etc/customsync-capture/tdlib-db-key \
    --property=Environment=DOTNET_ENVIRONMENT=Production \
    /usr/bin/dotnet /var/www/customsync-capture/CustomSync.Capture.dll --login

# 2. Sinxronizatsiya qurilmasi sifatida ro'yxatdan o'tish (kod so'raladi):
# not yet run — verify during the audit
systemd-run --pty --same-dir \
    --unit=customsync-capture-enroll \
    --service-type=exec \
    --property=User=customsync-capture \
    --property=Group=customsync-capture \
    --property=WorkingDirectory=/var/www/customsync-capture \
    --property=StateDirectory=customsync-capture \
    --property=StateDirectoryMode=0700 \
    --property=UMask=0077 \
    --property=Environment=DOTNET_ENVIRONMENT=Production \
    /usr/bin/dotnet /var/www/customsync-capture/CustomSync.Capture.dll --enroll

# 3. Master kalitni o'rnatish (parol so'raladi):
# not yet run — verify during the audit
systemd-run --pty --same-dir \
    --unit=customsync-capture-set-key \
    --service-type=exec \
    --property=User=customsync-capture \
    --property=Group=customsync-capture \
    --property=WorkingDirectory=/var/www/customsync-capture \
    --property=StateDirectory=customsync-capture \
    --property=StateDirectoryMode=0700 \
    --property=UMask=0077 \
    --property=Environment=DOTNET_ENVIRONMENT=Production \
    /usr/bin/dotnet /var/www/customsync-capture/CustomSync.Capture.dll --set-key
```

> [!WARNING]
> **Hech qachon maxfiy kalitlarni `Environment=` orqali bermang!** `systemctl show` buyrug'i tizimdagi barcha mahalliy foydalanuvchilarga jarayonning `Environment=` qiymatlarini ko'rsatib beradi. Kalitlar faqat `LoadCredential` yoki 0600 rejimdagi fayllar orqali berilishi shart.
> Agar `sudo -u customsync-capture` ishlatilsa, `/etc/customsync-capture/tdlib-db-key` fayli `customsync-capture` guruhi o'qiy oladigan bo'lishi (0400 yoki 0440) va `Telegram:DatabaseEncryptionKeyFile` sozlamasi orqali ko'rsatilishi kerak.

---

### 5. Telegram'da sessiya nomi va identifikatsiyasi

Telegram ilovasida (Settings → Devices) mazkur sessiya quyidagi parametrlar bilan ko'rinadi:
- **Device Model:** `CustomSync Capture`
- **Application Version:** `1.0`

Bu parametrlar kodda qat'iy belgilangan va testlar orqali himoyalangan. Egasi ushbu nom orqali sessiyani boshqa qurilmalardan osongina ajrata oladi.

---

### 6. Sessiyani bekor qilish (Revocation tartibi)

Agar sessiyani to'xtatish kerak bo'lsa:
1. **Har doim avval egasining telefonidagi yoki kompyuterdagi Telegram ilovasidan uzing:**
   Telegram → Settings → Devices → "CustomSync Capture" sessiyasini tanlang → **Terminate Session**.
   *(Kompromissga uchragan bo'lishi mumkin bo'lgan VPS dagi hech bir harakatga ishonib bo'lmaydi — Telegram'ning Devices oynasi yagona mutlaq vakolatli yo'ldir! Shuningdek telefondagi "Terminate all other sessions" ham ushbu sessiyani to'xtatadi).*
2. **VPS journalctl da nima ko'rinadi:**
   Xizmat yangilangan `updateAuthorizationState` xabarini qabul qiladi va darhol quyidagicha log yozadi:
   `Telegram authorization state changed to authorizationStateLoggingOut. Session is terminated or revoked; stopping capture.`
   Barcha ichki sikllar to'xtatiladi va jarayon **78 (EX_CONFIG)** chiqish kodi bilan to'xtaydi.
3. **Qayta ishga tushish sikli bo'lmaydi:**
   Unit faylidagi `RestartPreventExitStatus=78` sozlamasi tufayli systemd xizmatni har 15 soniyada qayta ishga tushirishga urinmaydi (restart storm yuz bermaydi).
4. **Eski sessiya qoldiqlarini tozalash:**
   ```bash
   # not yet run — verify during the audit
   sudo rm -rf /var/lib/customsync-capture/tdlib /var/lib/customsync-capture/files /var/lib/customsync-capture/message-cache.db
   ```
5. **Qayta kirish:**
   Yuqoridagi 4-banddagi `systemd-run ... --login` buyrug'i orqali yangi sessiya yaratiladi.

---

### 7. VPS buzilishi (Compromise) shubhasi tug'ilganda harakatlar rejasi

Agar VPS buzilgan deb taxmin qilinsa (masalan oldingi crypto miner hodisasi kabi):
1. **Darhol telefondan uzing:** Telefoningizdagi Telegram orqali "CustomSync Capture" sessiyasini uzing (Terminate Session).
2. **Sinxronizatsiya qurilmasini bekor qiling:** Serverdagi boshqa admin qurilmadan yoki admin API orqali capture qurilmasining tokeni va ruxsatini bekor qiling (Device Revocation).
3. **Bazaviy shifrlash kalitini almashtiring:** `/etc/customsync-capture/tdlib-db-key` fayliga yangi `openssl rand -base64 32` kaliti yozing.
4. **Qayta kirishni faqat toza tizimda bajaring:** Buzilgan VPS dagi dasturlarni tekshirmasdan qayta ishga tushirmang. Tizim to'liq tozalangach yoki yangi VPS ga o'rnatilgach, qayta `--login` qiling.


