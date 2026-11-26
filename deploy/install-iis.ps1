<#
.SYNOPSIS
  Instala la app "Sala de reuniones HCP" en IIS como aplicacion /salareservas.

.DESCRIPTION
  - Crea (o actualiza) un Application Pool propio "SalaReservas" (sin codigo administrado,
    siempre activo, UN solo proceso: necesario para el bloqueo anti-reservas duplicadas).
  - Crea la aplicacion /salareservas dentro del sitio indicado (por defecto "Default Web Site").
  - Copia los archivos publicados (dotnet publish) y conserva appsettings.Production.json y logs.
  - Guarda las credenciales de Microsoft 365 en appsettings.Production.json, FUERA del codigo,
    con permisos NTFS restringidos (solo SYSTEM, Administradores y la identidad del pool).

  Ejecutar en PowerShell COMO ADMINISTRADOR en el servidor Windows.

.EXAMPLE
  dotnet publish -c Release -o .\publish
  .\deploy\install-iis.ps1 -SourcePath .\publish `
      -TenantId "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" `
      -ClientId "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy" `
      -Mailbox  "sala.reuniones@tudominio.com" `
      -KioskKey "una-clave-larga-y-aleatoria"
  (el secreto de cliente se pide de forma oculta si no se pasa con -ClientSecret)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [string]$SiteName     = 'Default Web Site',
    [string]$AppName      = 'salareservas',
    [string]$PoolName     = 'SalaReservas',
    [string]$PhysicalPath = 'C:\inetpub\salareservas',

    [string]$TenantId,
    [string]$ClientId,
    [securestring]$ClientSecret,
    [string]$Mailbox,
    [string]$KioskKey,
    [string]$DisplayName
)

$ErrorActionPreference = 'Stop'

# --- Comprobaciones previas ---------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Ejecuta este script en una consola de PowerShell como Administrador.' }

if (-not (Test-Path (Join-Path $SourcePath 'SalaReservas.dll'))) {
    throw "No encuentro SalaReservas.dll en '$SourcePath'. Genera antes la publicacion: dotnet publish -c Release -o .\publish"
}

Import-Module WebAdministration

if (-not (Get-WebGlobalModule | Where-Object { $_.Name -eq 'AspNetCoreModuleV2' })) {
    throw 'Falta el modulo ASP.NET Core de IIS. Instala el ".NET 8 Hosting Bundle" (https://dotnet.microsoft.com/download/dotnet/8.0) y ejecuta iisreset.'
}
if (-not (Get-Website -Name $SiteName)) { throw "No existe el sitio IIS '$SiteName'." }

$httpsBinding = Get-WebBinding -Name $SiteName -Protocol 'https'
if (-not $httpsBinding) {
    Write-Warning "El sitio '$SiteName' no tiene un enlace HTTPS. La app exige HTTPS: anade un binding https con un certificado valido."
}

# --- Archivos -----------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $PhysicalPath | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $PhysicalPath 'logs') | Out-Null

# Se detiene el pool (si ya existe) para poder sustituir la DLL sin bloqueos.
if (Test-Path "IIS:\AppPools\$PoolName") {
    if ((Get-WebAppPoolState -Name $PoolName).Value -ne 'Stopped') { Stop-WebAppPool -Name $PoolName }
    $t = 0; while ((Get-WebAppPoolState -Name $PoolName).Value -ne 'Stopped' -and $t -lt 30) { Start-Sleep -Seconds 1; $t++ }
}

# /MIR deja la carpeta igual que el origen, salvo los archivos y carpetas excluidos (secretos y logs).
robocopy $SourcePath $PhysicalPath /MIR /XF appsettings.Production.json /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy fallo con el codigo $LASTEXITCODE" }

# --- Application Pool ---------------------------------------------------------
if (-not (Test-Path "IIS:\AppPools\$PoolName")) { New-WebAppPool -Name $PoolName | Out-Null }
$pool = "IIS:\AppPools\$PoolName"
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ''                 # Sin codigo administrado
Set-ItemProperty $pool -Name startMode -Value 'AlwaysRunning'               # arranca sin esperar la 1a peticion
Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name processModel.maxProcesses -Value 1             # un solo proceso (bloqueo anti-duplicados)
Set-ItemProperty $pool -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)

# --- Aplicacion /salareservas -------------------------------------------------
$app = "IIS:\Sites\$SiteName\$AppName"
if (-not (Test-Path $app)) {
    New-WebApplication -Name $AppName -Site $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $PoolName | Out-Null
} else {
    Set-ItemProperty $app -Name physicalPath -Value $PhysicalPath
    Set-ItemProperty $app -Name applicationPool -Value $PoolName
}
Set-ItemProperty $app -Name preloadEnabled -Value $true

# --- Credenciales (appsettings.Production.json) -------------------------------
$prodFile = Join-Path $PhysicalPath 'appsettings.Production.json'
$wantsConfig = $TenantId -or $ClientId -or $ClientSecret -or $Mailbox -or $KioskKey -or $DisplayName

if ($wantsConfig -or -not (Test-Path $prodFile)) {
    if (-not $TenantId)  { $TenantId  = Read-Host 'Id. de directorio (inquilino / Tenant ID)' }
    if (-not $ClientId)  { $ClientId  = Read-Host 'Id. de aplicacion (cliente / Client ID)' }
    if (-not $ClientSecret) { $ClientSecret = Read-Host 'Secreto de cliente (valor)' -AsSecureString }
    if (-not $Mailbox)   { $Mailbox   = Read-Host 'Buzon de la sala (p. ej. sala.reuniones@tudominio.com)' }
    if (-not $KioskKey)  { $KioskKey  = Read-Host 'Clave de acceso de la tablet (deja vacio para no exigirla)' }

    $plainSecret = [System.Net.NetworkCredential]::new('', $ClientSecret).Password
    $room = @{ Mailbox = $Mailbox }
    if ($DisplayName) { $room.DisplayName = $DisplayName }
    $cfg = @{
        Graph = @{ TenantId = $TenantId; ClientId = $ClientId; ClientSecret = $plainSecret }
        Room  = $room
        Kiosk = @{ AccessKey = $KioskKey }
    }
    $json = $cfg | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($prodFile, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Credenciales guardadas en $prodFile"
}

# --- Permisos NTFS ------------------------------------------------------------
$identity = "IIS AppPool\$PoolName"
icacls $PhysicalPath /grant "${identity}:(OI)(CI)RX" /T /C | Out-Null
icacls (Join-Path $PhysicalPath 'logs') /grant "${identity}:(OI)(CI)M" /C | Out-Null
if (Test-Path $prodFile) {
    icacls $prodFile /inheritance:r | Out-Null
    icacls $prodFile /grant 'SYSTEM:(R)' 'BUILTIN\Administrators:(F)' "${identity}:(R)" | Out-Null
}

Start-WebAppPool -Name $PoolName

Write-Host ''
Write-Host 'Instalacion terminada.' -ForegroundColor Green
Write-Host "  Aplicacion : $SiteName/$AppName   ->  $PhysicalPath"
Write-Host "  Pool       : $PoolName (sin codigo administrado, 1 proceso)"
Write-Host "  Abrir      : https://<nombre-del-servidor>/$AppName/" + $(if ($KioskKey) { "?key=<clave>" } else { '' })
Write-Host '  Si falla   : revisa .\logs y el Visor de eventos > Aplicacion (origen IIS AspNetCore Module V2).'
