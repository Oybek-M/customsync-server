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

### 1. Alohida tizim foydalanuvchisi va StateDirectory

Backend API ochiq internetga qaragan, Capture xizmati esa maxfiy sessiya va kalitlarni saqlaydi. Shuning uchun ular bir-biridan ajratilgan:

```bash
sudo useradd -r -s /bin/false customsync-capture
```

`customsync-capture.service` da `StateDirectory=customsync-capture` va `StateDirectoryMode=0700` sozlangan. Bu systemd tomonidan `/var/lib/customsync-capture` katalogini faqat `customsync-capture` foydalanuvchisi uchun to'liq huquq (0700) bilan xavfsiz yaratilishini ta'minlaydi.

### 2. CLI buyruqlari (--login, --enroll, --set-key)

Barcha sozlash buyruqlarini FAQAT `customsync-capture` foydalanuvchisi nomidan
va `/var/www/customsync-capture` papkasidan turib bajaring:

```bash
cd /var/www/customsync-capture

# Telegram sessiyasiga kirish (telefon, kod va parol so'raladi):
sudo -u customsync-capture DOTNET_ENVIRONMENT=Production \
    dotnet CustomSync.Capture.dll --login

# Sinxronizatsiya qurilmasi sifatida ro'yxatdan o'tish (kod so'raladi):
sudo -u customsync-capture DOTNET_ENVIRONMENT=Production \
    dotnet CustomSync.Capture.dll --enroll

# Master kalitni o'rnatish (parol so'raladi):
sudo -u customsync-capture DOTNET_ENVIRONMENT=Production \
    dotnet CustomSync.Capture.dll --set-key
```

- **Kod va parol buyruq qatoriga YOZILMAYDI.** Dastur ularni o'zi so'raydi
  (kiritilgani ekranda ko'rinmaydi). Buyruq qatoridagi qiymat
  `~/.bash_history` ga yoziladi va `ps` orqali boshqa jarayonlarga
  ko'rinadi — dastur esa uni baribir o'qimaydi.
- **`cd` majburiy.** `appsettings.json` va `appsettings.Production.json`
  joriy papkadan o'qiladi (generic host'ning content root'i — joriy
  papka). Boshqa papkadan ishga tushirilsa sozlamalar topilmaydi.
  systemd unit'da buni `WorkingDirectory` hal qiladi.
- Root sifatida bajarilsa fayllar root egaligida yaratilib qoladi va
  keyinchalik xizmat ularni o'qiy olmaydi.

