#!/usr/bin/env bash
# =============================================================================
#  Sala de reuniones HCP · arranque en modo kiosco (tablet Linux)
#  Probado con la lógica de Chromium/Chrome en Debian, Ubuntu y Raspberry Pi OS.
#
#  Uso:   SALA_URL="https://servidor.tudominio.com/salareservas/" SALA_KEY="clave" ./start-kiosk.sh
#  (o edita los valores por defecto de abajo)
# =============================================================================
set -u

URL="${SALA_URL:-https://servidor.tudominio.com/salareservas/}"
KEY="${SALA_KEY:-}"                                   # = Kiosk:AccessKey del servidor (si la has definido)
PROFILE="${SALA_PROFILE:-$HOME/.config/sala-kiosk}"   # perfil propio: guarda la cookie de acceso 1 año

FULL_URL="$URL"
[ -n "$KEY" ] && FULL_URL="${URL}?key=${KEY}"

# --- Navegador ---------------------------------------------------------------
BROWSER=""
for b in chromium chromium-browser google-chrome-stable google-chrome; do
  if command -v "$b" >/dev/null 2>&1; then BROWSER="$b"; break; fi
done
if [ -z "$BROWSER" ]; then echo "No se encuentra Chromium ni Chrome. Instala 'chromium'." >&2; exit 1; fi

# --- Pantalla siempre encendida y sin cursor (X11) ---------------------------
if [ -n "${DISPLAY:-}" ] && command -v xset >/dev/null 2>&1; then
  xset s off; xset s noblank; xset -dpms
fi
if command -v unclutter >/dev/null 2>&1; then (unclutter -idle 0.5 -root >/dev/null 2>&1 &); fi

# --- Espera a que haya red y el servidor responda (cualquier código HTTP vale) ---
until [ "$(curl -ks -o /dev/null -w '%{http_code}' --max-time 5 "$URL")" != "000" ]; do
  sleep 5
done

# --- Bucle de vigilancia: si el navegador se cierra o se cuelga, vuelve a abrirlo ---
while true; do
  # Evita el aviso "Chromium no se cerró correctamente" tras un apagón
  if [ -f "$PROFILE/Default/Preferences" ]; then
    sed -i 's/"exited_cleanly":false/"exited_cleanly":true/; s/"exit_type":"Crashed"/"exit_type":"Normal"/' "$PROFILE/Default/Preferences"
  fi

  "$BROWSER" \
    --kiosk \
    --user-data-dir="$PROFILE" \
    --noerrdialogs --disable-infobars --disable-session-crashed-bubble --no-first-run \
    --disable-translate --disable-features=Translate,TranslateUI \
    --overscroll-history-navigation=0 --disable-pinch \
    --check-for-update-interval=31536000 \
    --password-store=basic \
    --ozone-platform-hint=auto \
    "$FULL_URL"

  sleep 3
done
