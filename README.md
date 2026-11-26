# Sala de reuniones HCP · reservas con Microsoft 365

Aplicación web (ASP.NET Core 8 MVC) para la tablet de la puerta de una sala. Muestra si la sala está **Libre** u **Ocupada**, la reunión actual y la agenda, permite **elegir cualquier día** del calendario y **reservar** desde la propia pantalla. Lee y crea los eventos directamente en el calendario de Exchange Online de `sala.reuniones` mediante Microsoft Graph (Client Credentials), de modo que todo se ve en Outlook y Teams.

- **Detalle de reunión:** al pulsar la reunión actual, la siguiente o cualquiera de la agenda se abre una ficha con estado, día, horario, duración, organizador, sala y si tiene enlace de Teams.
- Se publica en IIS como aplicación **`/salareservas`** (HTTPS obligatorio).
- La tablet (Linux) la abre en **pantalla completa**, modo kiosco.
- Diseño según el *Manual de marca HCP v1.0* (HCP Lime `#D8FF00`, HCP Pink `#FF006E`, HCP Violet `#6B35FF`, HCP Gray `#888888`; logotipo vectorial del manual).

## Estructura

```
SalaReservas/
├─ SalaReservas.csproj
├─ Program.cs                    arranque, subcarpeta, HTTPS, clave de la tablet
├─ appsettings.json              configuración (sin secretos)
├─ web.config                    hosting in-process en IIS + cabeceras de seguridad
├─ Controllers/Controllers.cs    Home + API (/api/status, /api/token, /api/book)
├─ Models/Models.cs
├─ Services/
│  ├─ RoomServiceBase.cs         zona horaria, validaciones, bloqueo anti-duplicados
│  ├─ GraphRoomService.cs        Microsoft Graph (calendario real)
│  └─ DemoRoomService.cs         datos de ejemplo para probar sin Microsoft 365
├─ Views/ (Layout, Index, iconos SVG)
├─ wwwroot/ (css, js, fuentes, logo, Bootstrap en local, manifest)
├─ deploy/install-iis.ps1        instalador para IIS
└─ kiosk-linux/                  arranque de la tablet Linux
```

## 1. Probar en tu equipo (modo demo, sin Microsoft 365)

Requiere el SDK de .NET 8.

```powershell
cd SalaReservas
$env:Room__DemoMode = "true"; $env:RequireHttps = "false"; $env:PathBase = "/salareservas"
dotnet run --urls http://localhost:5000
```
Abre <http://localhost:5000/salareservas/>. (En bash: `Room__DemoMode=true RequireHttps=false PathBase=/salareservas dotnet run --urls http://localhost:5000`.)

## 2. Microsoft 365 y Entra ID

### 2.1 El buzón
Comprueba que `sala.reuniones@tudominio.com` existe en Exchange Online (buzón de sala o de usuario). Las reservas se crean **en el calendario de ese buzón**: no envían invitaciones. Para que alguien las vea en su Outlook debe añadir ese calendario (o consultar la sala desde el Buscador de salas).

> `CreateTeamsMeeting: true` añade un enlace de Teams a cada reserva, pero solo funciona si el buzón admite reuniones online (normalmente un usuario con licencia). En un buzón de sala déjalo en `false`.

### 2.2 Registrar la aplicación
1. <https://entra.microsoft.com> → **Identidad › Aplicaciones › Registros de aplicaciones › Nuevo registro**.
2. Nombre `SalaReservas`, *Solo este directorio*, sin URI de redirección → **Registrar**.
3. Anota **Id. de aplicación (cliente)** e **Id. de directorio (inquilino)**.
4. **Certificados y secretos › Nuevo secreto de cliente**. Copia el **Valor** (solo se muestra una vez) y apunta la fecha de caducidad para renovarlo.

### 2.3 Permisos mínimos: la app solo puede tocar UN buzón
Se usa **RBAC for Applications** de Exchange Online (sustituye a las antiguas *Application Access Policies*). Con este método **no concedas** `Calendars.ReadWrite` en *Permisos de API*: el permiso se da en Exchange y solo sobre el buzón de la sala.

```powershell
Install-Module ExchangeOnlineManagement          # una vez
Connect-ExchangeOnline

# 1) Ámbito: solo ese buzón
New-ManagementScope -Name "SalaReservas-Sala" `
   -RecipientRestrictionFilter "PrimarySmtpAddress -eq 'sala.reuniones@tudominio.com'"

# 2) Puntero a la aplicación. Los IDs salen de Entra › Aplicaciones empresariales › SalaReservas
#    (NO los de «Registros de aplicaciones»: son distintos)
New-ServicePrincipal -AppId <ID_APLICACION> -ObjectId <ID_OBJETO_ENTERPRISE_APP> -DisplayName "SalaReservas"

# 3) Rol de calendario (lectura/escritura) limitado al ámbito
New-ManagementRoleAssignment -App "SalaReservas" -Role "Application Calendars.ReadWrite" `
   -CustomResourceScope "SalaReservas-Sala"

# 4) Comprobación (InScope debe ser True)
Test-ServicePrincipalAuthorization -Identity "SalaReservas" -Resource sala.reuniones@tudominio.com | Format-Table
```
Los cambios pueden tardar entre 30 minutos y 2 horas en aplicarse. Referencia: [Role Based Access Control for Applications in Exchange Online](https://learn.microsoft.com/Exchange/permissions-exo/application-rbac).

*Alternativa clásica:* conceder `Calendars.ReadWrite` (aplicación) con consentimiento de administrador y limitarlo con `New-ApplicationAccessPolicy` a un grupo de seguridad que contenga solo el buzón de la sala.

## 3. Publicar en IIS (subcarpeta `/salareservas`)

**Requisitos del servidor:** Windows Server con el rol *Servidor web (IIS)*, el [**.NET 8 Hosting Bundle**](https://dotnet.microsoft.com/download/dotnet/8.0) (después `iisreset`) y un sitio con **enlace HTTPS** y certificado válido.

1. En una máquina con el SDK: `dotnet publish -c Release -o .\publish`
2. Copia la carpeta `publish` y `deploy\install-iis.ps1` al servidor.
3. PowerShell **como administrador**:
   ```powershell
   .\install-iis.ps1 -SourcePath .\publish `
       -TenantId "<TENANT_ID>" -ClientId "<CLIENT_ID>" `
       -Mailbox "sala.reuniones@tudominio.com" -KioskKey "<clave-larga-aleatoria>"
   ```
   Pedirá el secreto de cliente de forma oculta. El script:
   - crea el Application Pool `SalaReservas` (sin código administrado, siempre activo, **un solo proceso**),
   - crea la aplicación `/salareservas` bajo *Default Web Site* (`-SiteName` para otro sitio),
   - guarda las credenciales en `appsettings.Production.json` con permisos NTFS restringidos (no van en el código ni en `appsettings.json`).
4. Abre `https://<servidor>/salareservas/?key=<clave>` desde un navegador para comprobarlo.

**Instalación manual (sin script):** crea un pool *Sin código administrado* con *Maximum Worker Processes = 1*, crea una aplicación `salareservas` apuntando a la carpeta publicada, y define las credenciales como variables de entorno del sistema (`Graph__TenantId`, `Graph__ClientId`, `Graph__ClientSecret`, `Room__Mailbox`, `Kiosk__AccessKey`) o en `appsettings.Production.json`.

**Actualizar la app:** vuelve a publicar y ejecuta el script con `-SourcePath` (conserva credenciales y logs). La tablet detecta la nueva versión y se recarga sola.

## 4. Tablet con Linux (pantalla completa)

La interfaz está pensada para tablets de 10–13" en horizontal o vertical, con botones grandes y **teclado táctil propio** (no depende del teclado del sistema). Ya está en modo pantalla completa por defecto; el navegador la activa al primer toque y el botón ⛶ de la agenda la alterna. Evita el zoom, el menú contextual y el reposo de pantalla.

Instala Chromium (`sudo apt install chromium` o `chromium-browser`) y copia la carpeta `kiosk-linux` a la tablet.

```bash
chmod +x start-kiosk.sh
SALA_URL="https://servidor.tudominio.com/salareservas/" SALA_KEY="<clave>" ./start-kiosk.sh
```
`start-kiosk.sh` espera a que haya red, apaga el salvapantallas (X11), oculta el cursor si existe `unclutter`, abre Chromium con `--kiosk` y **lo reabre si se cierra o falla**. La primera vez usa `?key=` y guarda una cookie segura de un año en el perfil propio (`~/.config/sala-kiosk`), después basta la URL normal.

**Arranque automático:** edita las rutas, URL y clave de `sala-kiosk.desktop` y cópialo a `~/.config/autostart/` (inicio de sesión automático en la tablet).

**Pantalla siempre encendida**
- X11: ya lo hace el script (`xset s off -dpms`).
- GNOME/Wayland: `gsettings set org.gnome.desktop.session idle-delay 0` y `gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-ac-type 'nothing'`.
- La app además pide un *wake lock* al navegador cuando está disponible.

**Certificado de una CA interna:** si el servidor usa un certificado de tu propia CA, impórtalo para Chromium:
```bash
sudo apt install libnss3-tools
certutil -d sql:$HOME/.pki/nssdb -A -t "C,," -n "CA-interna" -i ca.crt
```
**Modo sin pantalla completa automática:** abre una vez `…/salareservas/?fs=0` (y `?fs=1` para volver).

## 5. Configuración (`appsettings.json`)

| Clave | Por defecto | Descripción |
|---|---|---|
| `Room:Mailbox` | | Buzón de la sala en Microsoft 365 |
| `Room:DisplayName` | Sala de Reuniones | Nombre en pantalla |
| `Room:TimeZone` | Romance Standard Time | Zona horaria de la sala (Madrid) |
| `Room:MinMinutes` / `MaxMinutes` | 15 / 240 | Duración mínima y máxima de una reserva |
| `Room:SlotMinutes` | 15 | Separación entre horas de inicio |
| `Room:WorkdayStart` / `WorkdayEnd` | 07:00 / 21:00 | Horario en que se puede reservar |
| `Room:MaxDaysAhead` | 90 | Hasta cuántos días por delante se consulta y reserva |
| `Room:CreateTeamsMeeting` | false | Añade enlace de Teams (si el buzón lo admite) |
| `Room:DemoMode` | false | Datos de ejemplo, sin Microsoft 365 |
| `Kiosk:AccessKey` | vacío | Clave que debe presentar la tablet (cookie de 1 año) |
| `PathBase` | vacío | Solo para ejecutar fuera de IIS bajo `/salareservas` |
| `RequireHttps` | true | Redirige HTTP a HTTPS |
| `Graph:TenantId` / `ClientId` / `ClientSecret` | | Credenciales de Entra ID (ponlas en `appsettings.Production.json`, nunca en el repositorio) |

## 6. Marca

- **Colores:** Libre = HCP Lime, Ocupada = HCP Pink, acentos en negro, HCP Violet para el foco del teclado y HCP Gray para textos secundarios.
- **Logotipo:** SVG vectorial extraído del manual (`wwwroot/img/hcp-logo.svg`), con la altura mínima de 50 px del apartado 2.5, en negro sobre Lime y en blanco sobre Pink (apartado 2.8).
- **Tipografías:** el manual usa **Px Grotesk** (titulares) y **APK/HCP Narrative** (texto), que son comerciales y **no se incluyen**. Se usan como sustitutas *Inter Tight*, *Inter* e *IBM Plex Mono* (licencia libre, incluidas en `wwwroot/fonts`). Si dispones de las licencias, copia los `.woff2` a `wwwroot/fonts` y añade en `site.css` sus `@font-face` con los nombres `"Px Grotesk"`, `"HCP Narrative"` y `"HCP Narrative Mono"`; la hoja de estilos ya los prioriza.

## 7. Seguridad y comportamiento

- Credenciales solo en el servidor (Client Credentials); la tablet nunca las ve. Permisos mínimos: un único buzón.
- **Sin reservas duplicadas:** las reservas se serializan con un bloqueo y la disponibilidad se vuelve a comprobar contra Exchange justo antes de crear el evento (por eso el pool usa **1 proceso**). Si algún día escalas a varios servidores, sustituye el bloqueo por uno distribuido.
- El servidor valida horario, duración, fecha (no pasadas, máximo de antelación) y título; token antiforgery en cada reserva; HTTPS + HSTS; cabeceras de seguridad.
- Las horas se calculan siempre en la **hora de la sala** del servidor: no dependen del reloj ni de la zona horaria de la tablet.
- La pantalla se actualiza cada 30 s y el reloj cada segundo; si nadie toca la tablet durante 90 s vuelve a «Hoy». Recarga de mantenimiento diaria a las 03:00.

## 8. Problemas habituales

| Síntoma | Causa probable |
|---|---|
| «Sin permisos sobre el calendario de la sala (403)» | Falta el rol RBAC / consentimiento, o aún no se ha propagado (hasta 2 h) |
| «No se encuentra el buzón de la sala (404)» | `Room:Mailbox` mal escrito |
| «Falta configurar Microsoft Graph» | `Graph:*` sin rellenar o aún con `PON-AQUI…` |
| Error HTTP 500.19 / 502.5 en IIS | Falta el Hosting Bundle o el pool no es *Sin código administrado* |
| Horas desplazadas | Revisa `Room:TimeZone` |
| La tablet muestra «No autorizado» | Abre una vez la URL con `?key=<clave>` |
