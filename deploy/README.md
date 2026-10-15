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

### 0. .NET 10 Runtime

VPS da .NET 10 LTS runtime'lari talab qilinadi (`customsync.service` — ASP.NET Core Runtime 10.0, `customsync-capture.service` — .NET Runtime 10.0).

⚠️ VPS da boshqa .NET 8 ilovalar ishlaydi: 10.0 runtime 8.0 **yonida** o'rnatiladi, 8.0 o'chirilmaydi va almashtirilmaydi. O'rnatishdan keyin `dotnet --list-runtimes` da 8.0.x ham, 10.0.x ham ko'rinishi kerak. Paket manbai (Ubuntu arxivi yoki Microsoft repo'si) deploy paytida hal qilinadi — quyidagi buyruqlar hali sinalmagan:

```bash
# not yet run — decided at deploy
sudo apt-get update
sudo apt-get install -y dotnet-runtime-10.0 aspnetcore-runtime-10.0
dotnet --list-runtimes
```

Yoki Microsoft rasmiy paket repositoriyasi orqali:
```bash
# not yet run — decided at deploy
# https://learn.microsoft.com/dotnet/core/install/linux-ubuntu
```

#### 0a. Muqobil: self-contained publish (birinchi deploy shu yo'l bilan)

Agar VPS paketlariga tegish istalmasa (boshqa .NET ilovalar bor, paket manbai
hali tartibga keltirilmagan), runtime ilova ichida yetkaziladi va `/usr/bin/dotnet`
kerak bo'lmaydi:

```bash
dotnet publish src/CustomSync.Api -c Release -r linux-x64 --self-contained true -o ./publish/api
dotnet publish src/CustomSync.Capture -c Release -r linux-x64 --self-contained true -o ./publish/capture
```

`appsettings.Development.json` VPS'ga ko'chirilmaydi. Unit fayllar o'zgarmaydi — faqat drop-in qo'shiladi
(`/etc/systemd/system/customsync.service.d/20-self-contained.conf`):

```ini
[Service]
ExecStart=
ExecStart=/var/www/customsync/CustomSync.Api
```

Capture uchun ham xuddi shunday (`/var/www/customsync-capture/CustomSync.Capture`); pastdagi
`systemd-run` buyruqlarida ham `/usr/bin/dotnet ….dll` o'rniga shu fayl yoziladi.
Kamchiligi: runtime xavfsizlik yamoqlari apt orqali kelmaydi — ular uchun qayta publish kerak.

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

Keyin nginx va sertifikat. Sertifikat **webroot** bilan olinadi: certbot nginx
fayllariga tegmaydi, boshqa saytlar bor serverda ham xavfsiz. Avval faqat
80-port va ACME bilan vaqtinchalik sayt, sertifikatdan keyin esa to'liq sayt:

```bash
DOMAIN=sync.example.uz        # o'z domeningiz; A yozuv shu serverga
sudo mkdir -p /var/www/letsencrypt/.well-known/acme-challenge
sudo tee /etc/nginx/sites-available/customsync >/dev/null <<EOF
server {
    listen 80;
    server_name $DOMAIN;
    location ^~ /.well-known/acme-challenge/ { root /var/www/letsencrypt; default_type text/plain; }
    location / { return 404; }
}
EOF
sudo ln -s /etc/nginx/sites-available/customsync /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot certonly --webroot -w /var/www/letsencrypt -d "$DOMAIN" \
    --deploy-hook "systemctl reload nginx"

sed "s/sync\.example\.uz/$DOMAIN/g" deploy/nginx-customsync.conf \
    | sudo tee /etc/nginx/sites-available/customsync >/dev/null
sudo nginx -t && sudo systemctl reload nginx
sudo certbot renew --cert-name "$DOMAIN" --dry-run
```

`--deploy-hook` yangilangan sertifikatni nginx'ga yuklaydi (webroot
installer'siz o'zi reload qilmaydi). `nginx-customsync.conf` dagi 80-port
bloki ACME yo'lini ochiq qoldiradi — busiz 90 kundan keyingi yangilanish
yiqiladi.

Tekshirish: `curl https://$DOMAIN/api/v1/health` → `200`; WebSocket yo'li
token'siz `401` qaytarishi kerak (`400` — Upgrade sarlavhasi backend'ga
yetmagan):

```bash
curl -s -o /dev/null -w '%{http_code}\n' --http1.1 -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
    -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' https://$DOMAIN/ws/notify
```

Sozlamadagi uch qaror sababi faylning o'zida yozilgan: noyob `map` nomi
(boshqa saytning `$connection_upgrade` i bilan to'qnashmaslik), query'siz
log formati (tdesktop WebSocket token'ini URL'da yuboradi) va `http2` yo'qligi
(u port bo'yicha hamma saytlarga global yoqiladi).

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

Self-contained o'rnatishda (`/usr/bin/dotnet` yo'q) xizmat bilan bir xil
sandbox'da:

```bash
sudo systemd-run --pipe --wait --quiet --collect --service-type=exec \
    -p User=customsync -p Group=customsync -p WorkingDirectory=/var/www/customsync \
    -p Environment=ASPNETCORE_ENVIRONMENT=Production \
    /var/www/customsync/CustomSync.Api --create-enrollment-code --admin
```

Kod `XXXX-XXXX-XX` ko'rinishida, `auth.enroll_code_minutes` (standart 10)
daqiqa amal qiladi va bir marta ishlatiladi — tdesktop'ni oldindan oching.
Hozircha kod aynan solishtiriladi: katta harflar va ikkala defis bilan
(normallashtirish ochiq — `PROGRESS.md` §8, 53-band).

#### Sync'ni ulash tartibi (birinchi marta; 2026-10-15 da shunday qilindi)

1. **tdesktop:** Sozlamalar → Sinxronizatsiya → server URL va **admin** kodi →
   ro'yxatdan o'tish → arxiv parolini yaratish. Birinchi qurilma master kalitni
   yaratadi va uning o'ramini serverga yuklaydi (POST admin rolini talab qiladi).
   Arxiv paroli kalitning yagona nusxasi.
2. **Capture:** oddiy (`device`) kod bilan `--enroll`, so'ng `--set-key`: o'ram
   tanlanadi, arxiv paroli kiritiladi va kalit barmoq izi (FP) tdesktop'dagi bilan
   solishtiriladi. `Capture:Sync:ServerUrl` = `http://127.0.0.1:5080` — capture API
   bilan bir serverda, so'rovlar tarmoqqa chiqmaydi.
3. `Capture:Sync:Enabled=true` va capture restart. Birinchi siklda capture
   navbatidagi (outbox) yozuvlar serverga ketadi.

Keyingi qurilmalar (masalan, ikkinchi kompyuter) oddiy kod bilan ro'yxatdan o'tadi
va arxivga parol orqali qo'shiladi.

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

Yangi bazada birinchi startda bitta `ERR Failed executing DbCommand … FROM "__EFMigrationsHistory"`
chiqadi — bu normal: EF Core tarix jadvalini o'qiy olmagach uni yaratadi va migratsiyalarni qo'llaydi
(ortidan `Applying migration '…_InitialSchema'`). Keyingi startlarda takrorlanmaydi.
Hozircha har SQL buyrug'i `INF Executed DbCommand` sifatida ham yoziladi (parametr qiymatlarisiz);
darajani pasaytirish ochiq — `PROGRESS.md` §8, 51-band.

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

⚠️ **OGOHLANTIRISH:** capture xizmati egasining Telegram akkauntiga to'liq kirish huquqiga va master kalitga ega. Deploy faqat shartli: xavfsizlik auditi va hardening'dan keyin, hosting paneli 2FA, VPS root paroli va Telegram 2FA tayyor bo'lgach (egasi qarori, 2026-10-13; shartlarni egasi 2026-10-13 da bajardi).
Quyidagi buyruqlar hali jonli tizimda bajarilmagan — ularni audit davomida tekshiring. Capture'ning haqiqiy `libtdjson.so` bilan ishga tushishi tarmoqsiz konteynerda tekshirilgan (2026-10-14, `PROGRESS.md` §8, 50-band).

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
| `/var/www/customsync-capture/appsettings.Production.json` | Konfiguratsiya (api_id, api_hash, server manzili) | `root:customsync-capture` `0640` (xizmat o'qiydi, o'zgartira olmaydi; preflight `0640` dan kengini rad etadi) | **YO'Q** (repo'ga ham kiritilmaydi) |
| `/var/www/customsync-capture/libtdjson.so` | TDLib kutubxonasi (1.8.67, quyidagi 1a-band) | `root:root` `0644` | Kerak emas (qayta build qilinadi) |

> [!CAUTION]
> **Zaxira qoidasi:** Capture holati (sessiya, kesh, kalitlar) hech qachon zaxira nusxalariga (backups) kiritilmaydi. Agar VPS yo'qolsa yoki buzilsa, yagona xavfsiz tiklash yo'li — toza tizimda yangidan `--login` va `--enroll` qilishdir.
> **Contabo snapshot'lari:** Virtual server snapshot'lari butun fayl tizimini (shu jumladan shifrlanmagan kesh va kalitlarni) to'liq o'z ichiga oladi. Snapshot'larni o'ta maxfiy deb biling va audit/testdan so'ng darhol o'chirib tashlang.

---

### 1a. TDLib kutubxonasi (`libtdjson.so`)

Capture TDLib'ning JSON interfeysi (`td_create_client_id`, `td_send`, `td_receive`, `td_execute`) bilan ishlaydi.
Birinchi deploy versiyasi: **TDLib 1.8.67**, commit `bc9c263e2bfee06aaab41e82db51a103376030bc` ("Update version to 1.8.67.", 2026-08-24).
Kutubxona maqsad tizim bilan bir xil distributivda build qilinadi (Ubuntu 24.04 uchun `ubuntu:24.04` konteyneri).
Natija GLIBC 2.38+ va OpenSSL 3 ni talab qiladi.

1. Paketlar: `ca-certificates git cmake g++ make zlib1g-dev libssl-dev gperf php-cli`.
2. Manba aniq commit bo'yicha olinadi: `git fetch --depth 1 https://github.com/tdlib/td.git <commit>`, so'ng `CMakeLists.txt` da `project(TDLib VERSION 1.8.67` borligi tekshiriladi.
3. `build/` da `cmake -DCMAKE_BUILD_TYPE=Release ..` va `cmake --build . --target prepare_cross_compiling`.
4. **1.8.67 dagi upstream xatosi:** `SplitSource.php` da `'MessageContentUploadId' => 'MessageContentUploadId'` qatoridan keyin vergul tushib qolgan (PHP parse error).
   Upstream'da `58ce288687` ("Fix SplitSource.", 2026-09-21) aynan shu vergulni qo'shadi. Uni qo'shing, `php -l SplitSource.php` bilan tekshiring, keyin `php SplitSource.php`.
   Bu faqat build yordamchisi, kutubxona kodiga tegmaydi.
5. `cmake --build . --target tdjson -- -j3`. Laptop'da 3 job va 6 GB xotira limiti bilan ~27 daqiqa.
6. `ldd libtdjson.so` da `not found` bo'lmasin. `sha256sum` ni yozib qo'ying va VPS'ga ko'chirilgandan keyin solishtiring.

O'rnatish va sozlama:

```bash
sudo install -m 0644 -o root -g root libtdjson.so /var/www/customsync-capture/libtdjson.so
```

`appsettings.Production.json` da: `"Telegram": { "TdJsonPath": "/var/www/customsync-capture/libtdjson.so" }`.

Tekshiruv: avtorizatsiya qilinmagan sessiyada capture `--login` siz ishga tushirilsa, `setTdlibParameters` dan keyin `WaitPhoneNumber` gacha yetadi.
U `--login` ni ko'rsatib `78` bilan to'xtaydi; buning uchun tarmoq kerak emas.
`TDLib did not report an authorization state` chiqsa, TDLib nusxasi ishga tushmagan.

---

### 2. TDLib baza shifrlash kalitini yaratish va o'rnatish

TDLib ma'lumotlar bazasi diskda shifrlangan holda saqlanishi shart. Kalit kamida 32 baytlik tasodifiy ma'lumotning base64 satridan iborat bo'lishi kerak:

```bash
# 2026-10-14: birinchi deploy'da shu natija deploy skripti bilan yaratildi (papka 0700, kalit 0400 root:root)
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

`--login` da raqam, kod va 2FA parolni kiritish uchun 15 daqiqa beriladi (fon rejimida 2 daqiqa).
`--login` buyruqning **oxirida** tursin: qiymatsiz kalit o'zidan keyingi argumentni qiymat qilib yutadi.
Kod, parol yoki raqam xato terilsa (Telegram `..._INVALID` deb rad etsa) yoki bo'sh Enter bosilsa, o'sha qadam sababi bilan qayta so'raladi, 3 martagacha.
FLOOD yoki eskirgan kod bo'lsa, qayta urinish Telegram'ga yuborilmaydi. Telegram login email talab qilsa, email va unga kelgan kod so'raladi.
Login tugab, `Capture service authorized and running.` chiqqach, Ctrl+C bilan to'xtatib, xizmatni yoqing.

`--login` buyrug'i oddiy terminalda `sudo -u` bilan bajarilsa, umask (odatda 0022) ochiq qolishi, `StateDirectory` va systemd `LoadCredential` sozlamalari ishlamay qolishi mumkin.
Shu sababli, CLI buyruqlarini xizmatning o'zi bilan **bir xil sandbox muhitida** ishga tushirish uchun `systemd-run --pty` dan foydalaning:

```bash
cd /var/www/customsync-capture

# 1. Telegram sessiyasiga kirish (telefon, kod, 2FA parol so'raladi):
# 2026-10-15: birinchi login deploy skripti orqali bajarildi — shu xususiyatlar va unit'ning
# qolgan [Service] cheklovlari bilan; self-contained'da oxirgi qator:
# /var/www/customsync-capture/CustomSync.Capture --login
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
    --property=Environment=LD_LIBRARY_PATH=/var/www/customsync-capture \
    /usr/bin/dotnet /var/www/customsync-capture/CustomSync.Capture.dll --login

# 2. Sinxronizatsiya qurilmasi sifatida ro'yxatdan o'tish (kod so'raladi):
# 2026-10-15: deploy skripti orqali bajarildi (unit'ning [Service] cheklovlari bilan, self-contained)
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
# 2026-10-15: deploy skripti orqali bajarildi; FP tdesktop bilan mos keldi
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
   sudo rm -rf /var/lib/customsync-capture/tdlib /var/lib/customsync-capture/files
   ```
   `message-cache.db` o'chirilmaydi: unda hali serverga yetmagan yozuvlar bo'lishi mumkin, xuddi shu akkauntga qayta kirilganda u yaroqli qoladi. Uni faqat 7-bo'limdagi buzilish holatida (butun tizim bilan birga) yo'q qiling.
5. **Qayta kirish:**
   Yuqoridagi 4-banddagi `systemd-run ... --login` buyrug'i orqali yangi sessiya yaratiladi.

---

### 7. VPS buzilishi (Compromise) shubhasi tug'ilganda harakatlar rejasi

Agar VPS buzilgan deb taxmin qilinsa (masalan oldingi crypto miner hodisasi kabi):
1. **Darhol telefondan uzing:** Telefoningizdagi Telegram orqali "CustomSync Capture" sessiyasini uzing (Terminate Session).
2. **Sinxronizatsiya qurilmasini bekor qiling:** Serverdagi boshqa admin qurilmadan yoki admin API orqali capture qurilmasining tokeni va ruxsatini bekor qiling (Device Revocation).
3. **Bazaviy shifrlash kalitini almashtiring:** `/etc/customsync-capture/tdlib-db-key` fayliga yangi `openssl rand -base64 32` kaliti yozing.
4. **Qayta kirishni faqat toza tizimda bajaring:** Buzilgan VPS dagi dasturlarni tekshirmasdan qayta ishga tushirmang. Tizim to'liq tozalangach yoki yangi VPS ga o'rnatilgach, qayta `--login` qiling.


