<#
.SYNOPSIS
  `customsync` roli va bazasini yaratadi, migratsiyani qo'llaydi.

.DESCRIPTION
  Plan 01a, Task 3, Step 7. Skript sizdan postgres superuser parolini
  O'Z terminalingizda so'raydi va uni hech qayerga yozmaydi.

  `customsync` roli uchun parol tasodifiy generatsiya qilinadi va faqat
  `appsettings.Development.json` ga yoziladi -- u `.gitignore` da.
  Shu sababli parol na gitga, na chatga, na log'ga tushadi.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\db-bootstrap.ps1
#>

[CmdletBinding()]
param(
    [string] $SuperUser = 'postgres',
    [string] $DbHost    = 'localhost',
    [int]    $Port      = 5432,
    [string] $Role      = 'customsync',
    [string] $Database  = 'customsync'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command psql -ErrorAction SilentlyContinue)) {
    throw "psql PATH da topilmadi. PostgreSQL bin katalogini PATH ga qo'shing."
}

# --- 1. Superuser paroli (faqat shu jarayon xotirasida) --------------------
$secure = Read-Host "postgres ($SuperUser) paroli" -AsSecureString
$bstr   = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try   { $superPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }

# --- 2. `customsync` uchun tasodifiy parol ---------------------------------
# Alnum-only: connection string'da qochirish (escaping) muammosi bo'lmasin.
#
# RandomNumberGenerator::Create() ATAYLAB ishlatiladi: ::Fill() faqat
# .NET Core'da bor, Windows PowerShell 5.1 esa .NET Framework 4.x da
# ishlaydi. ::Create() ikkalasida ham mavjud.
$alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
$length   = 32

# 256 alphabet uzunligiga bo'linmaydi, shuning uchun oxirgi to'liq
# blokdan katta baytlar rad etiladi -- aks holda birinchi harflar
# boshqalaridan ko'proq chiqardi.
$limit = [byte](256 - (256 % $alphabet.Length))

$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $chars  = New-Object System.Collections.Generic.List[char]
    $buffer = New-Object byte[] 64
    while ($chars.Count -lt $length) {
        $rng.GetBytes($buffer)
        foreach ($b in $buffer) {
            if ($chars.Count -ge $length) { break }
            if ($b -lt $limit) { $chars.Add($alphabet[$b % $alphabet.Length]) }
        }
    }
}
finally { $rng.Dispose() }

$rolePassword = -join $chars

# --- 3. Rol va bazani yaratish (idempotent) --------------------------------
# Parol SQL'ga literal sifatida kiradi -- alnum bo'lgani uchun xavfsiz.
$sql = @"
DO `$`$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$Role') THEN
    CREATE ROLE $Role LOGIN PASSWORD '$rolePassword';
  ELSE
    ALTER ROLE $Role WITH LOGIN PASSWORD '$rolePassword';
  END IF;
END
`$`$;
"@

$env:PGPASSWORD = $superPassword
try {
    Write-Host "-> rol '$Role' yaratilmoqda/yangilanmoqda..."
    $sql | psql -U $SuperUser -h $DbHost -p $Port -d postgres -v ON_ERROR_STOP=1 -q
    if ($LASTEXITCODE -ne 0) { throw "Rol yaratishda xato (exit $LASTEXITCODE)." }

    $exists = psql -U $SuperUser -h $DbHost -p $Port -d postgres -tAc `
        "SELECT 1 FROM pg_database WHERE datname = '$Database'"
    if ($exists -ne '1') {
        Write-Host "-> baza '$Database' yaratilmoqda..."
        psql -U $SuperUser -h $DbHost -p $Port -d postgres -v ON_ERROR_STOP=1 -q `
             -c "CREATE DATABASE $Database OWNER $Role"
        if ($LASTEXITCODE -ne 0) { throw "Baza yaratishda xato (exit $LASTEXITCODE)." }
    } else {
        # Baza boshqa egada bo'lsa, PG15+ da `public` sxemaga yozib
        # bo'lmaydi va migratsiya "permission denied for schema public"
        # bilan yiqilardi. Egalikni to'g'irlab qo'yamiz.
        Write-Host "-> baza '$Database' mavjud, egaligi tekshirilmoqda..."
        psql -U $SuperUser -h $DbHost -p $Port -d postgres -v ON_ERROR_STOP=1 -q `
             -c "ALTER DATABASE $Database OWNER TO $Role"
        if ($LASTEXITCODE -ne 0) { throw "Baza egaligini o'zgartirishda xato (exit $LASTEXITCODE)." }
    }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    $superPassword = $null
}

# --- 4. appsettings.Development.json -------------------------------------
$connection = "Host=$DbHost;Port=$Port;Database=$Database;Username=$Role;Password=$rolePassword"
$devSettingsPath = Join-Path $repo 'src\CustomSync.Api\appsettings.Development.json'

$devSettings = [ordered]@{
    ConnectionStrings = [ordered]@{ Postgres = $connection }
}
$devSettings | ConvertTo-Json -Depth 5 | Set-Content -Path $devSettingsPath -Encoding utf8
Write-Host "-> yozildi: $devSettingsPath (gitignore'da)"

# --- 5. Migratsiyani qo'llash ---------------------------------------------
Write-Host '-> migratsiya qollanmoqda...'
Push-Location $repo
try {
    dotnet ef database update --project src/CustomSync.Data --startup-project src/CustomSync.Api
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update yiqildi (exit $LASTEXITCODE)." }
}
finally { Pop-Location }

# --- 6. Tekshirish ---------------------------------------------------------
$env:PGPASSWORD = $rolePassword
try {
    Write-Host ''
    Write-Host 'Jadvallar:'
    psql -U $Role -h $DbHost -p $Port -d $Database -c '\dt'
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    $rolePassword = $null
}

Write-Host ''
Write-Host 'Tayyor. Kutilgan: 10 ta jadval (8 entity + sync_counter + __EFMigrationsHistory).'
