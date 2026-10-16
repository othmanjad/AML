<#
  Creates .env for docker compose with random secrets (run from the repository root):
    powershell -ExecutionPolicy Bypass -File deploy\docker\generate-env.ps1 -PublicUrl http://SERVER:8080
  The file is written next to docker-compose.yml and is never committed. Keep a secure copy of the keys.
#>
param(
  [string]$PublicUrl = "http://localhost:8080",
  [int]$WebPort = 8080,
  [switch]$NoDemoData,
  [switch]$Force
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$target = Join-Path $root ".env"
if ((Test-Path $target) -and -not $Force) { throw ".env already exists. Use -Force to overwrite (existing encrypted data needs the old keys)." }

$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
function New-Key([int]$bytes = 32) { $b = New-Object byte[] $bytes; $rng.GetBytes($b); [Convert]::ToBase64String($b) }
function New-Password([int]$length = 20) {
  $sets = @('ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '!#%+-=@_')
  $all = ($sets -join '').ToCharArray()
  $chars = @()
  foreach ($s in $sets) { $b = New-Object byte[] 1; $rng.GetBytes($b); $chars += $s[$b[0] % $s.Length] }
  while ($chars.Count -lt $length) { $b = New-Object byte[] 1; $rng.GetBytes($b); $chars += $all[$b[0] % $all.Length] }
  -join ($chars | Sort-Object { Get-Random })
}

$demo = if ($NoDemoData) { "false" } else { "true" }
$content = @"
PUBLIC_URL=$PublicUrl
WEB_PORT=$WebPort
ASPNETCORE_ENVIRONMENT=Staging
SQL_SA_PASSWORD=$(New-Password 24)
SQL_DATABASE=AmlDb
MSSQL_PID=Developer
SECURITY_ENCRYPTION_KEY=$(New-Key)
SECURITY_SEARCH_TOKEN_KEY=$(New-Key)
SECURITY_INTEGRITY_KEY=$(New-Key)
SECURITY_ENCRYPTION_KEY_ID=k1
AUTH_SIGNING_KEY=$(New-Key 48)
AUTH_AUTHORITY=
DEVAUTH_ENABLED=true
DEVAUTH_SEED_PASSWORD=$(New-Password 16)
INTEGRATION_CLIENT_SECRET=$(New-Password 32)
SEED_DEMO_DATA=$demo
HTTPS_REDIRECT=false
SWAGGER_ENABLED=true
"@
# UTF-8 without BOM so docker compose reads the first variable correctly.
[System.IO.File]::WriteAllText($target, $content, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Created $target"
Write-Host "Test users' password (DEVAUTH_SEED_PASSWORD) and the other secrets are in that file — store it securely."
