#!/usr/bin/env bash
# ============================================================================
#  Oversight store — one-shot Linux installer
#
#  Run it on a Debian/Ubuntu box (Raspberry Pi / mini-PC / VM) on the same LAN
#  as your NAS:
#
#      sudo bash install.sh
#
#  It will:
#    1. Install system deps (Node.js 20, build tools, cifs-utils, cloudflared)
#    2. Mount your NAS releases share (CIFS) and add it to /etc/fstab
#    3. Write .env (interactive, with sane defaults; re-runs keep your answers)
#    4. npm install + first NAS sync
#    5. Create + start a systemd service (oversight) that runs on boot
#    6. Optionally install a Cloudflare Tunnel to publish it (no port-forwarding)
#
#  Non-interactive: pre-set any of the prompted vars in the environment and pass
#  --yes to accept defaults, e.g.  ADMIN_EMAILS=me@x.com NAS_UNC=//nas/Projects sudo -E bash install.sh --yes
# ============================================================================
set -euo pipefail

BLUE='\033[1;34m'; GREEN='\033[1;32m'; YELLOW='\033[1;33m'; RED='\033[1;31m'; NC='\033[0m'
say()  { echo -e "${BLUE}==>${NC} $*"; }
ok()   { echo -e "${GREEN}  ✓${NC} $*"; }
warn() { echo -e "${YELLOW}  ! ${NC}$*"; }
die()  { echo -e "${RED}  ✗ $*${NC}"; exit 1; }

[ "$(id -u)" -eq 0 ] || die "Please run with sudo:  sudo bash install.sh"

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUN_USER="${SUDO_USER:-root}"
ASSUME_YES=0; [ "${1:-}" = "--yes" ] && ASSUME_YES=1

ask() { # ask VAR "Prompt" "default"
  local __var="$1" __prompt="$2" __def="${3:-}" __cur="${!1:-}" __val
  __def="${__cur:-$__def}"
  if [ "$ASSUME_YES" = "1" ] || [ ! -t 0 ]; then __val="$__def";
  else read -r -p "$(echo -e "${__prompt} [${__def}]: ")" __val; __val="${__val:-$__def}"; fi
  printf -v "$__var" '%s' "$__val"
}
ask_secret() { # ask_secret VAR "Prompt"
  local __var="$1" __prompt="$2" __val
  if [ "$ASSUME_YES" = "1" ] || [ ! -t 0 ]; then __val="${!1:-}";
  else read -r -s -p "$(echo -e "${__prompt}: ")" __val; echo; fi
  printf -v "$__var" '%s' "$__val"
}

# ---------------------------------------------------------------------------
say "1/6  Installing system packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq ca-certificates curl gnupg build-essential python3 cifs-utils >/dev/null
if ! command -v node >/dev/null || [ "$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0)" -lt 18 ]; then
  say "    Installing Node.js 20.x"
  curl -fsSL https://deb.nodesource.com/setup_20.x | bash - >/dev/null 2>&1
  apt-get install -y -qq nodejs >/dev/null
fi
ok "Node $(node -v), npm $(npm -v)"

# ---------------------------------------------------------------------------
say "2/6  NAS releases share"
echo "    Your Oversight releases/ folder lives on the NAS. Point this box at it."
echo "    Example UNC on Windows:  \\\\NAS\\Projects   ->  here:  //NAS/Projects"
ask NAS_UNC        "    NAS share (//host/share)"        "${NAS_UNC:-//NAS/Projects}"
ask NAS_SUBPATH    "    Sub-path to releases inside share" "${NAS_SUBPATH:-Oversight/releases}"
ask NAS_MOUNT      "    Local mount point"                "${NAS_MOUNT:-/mnt/oversight-nas}"
ask NAS_USER       "    NAS username (blank = guest)"     "${NAS_USER:-}"
NAS_PASS="${NAS_PASS:-}"; [ -n "$NAS_USER" ] && ask_secret NAS_PASS "    NAS password"

NAS_RELEASES_DIR="${NAS_MOUNT%/}/${NAS_SUBPATH#/}"
mkdir -p "$NAS_MOUNT"
CRED_FILE="/etc/oversight-nas.cred"
if [ -n "$NAS_USER" ]; then
  printf 'username=%s\npassword=%s\n' "$NAS_USER" "$NAS_PASS" > "$CRED_FILE"
  chmod 600 "$CRED_FILE"
  FSTAB_OPTS="credentials=${CRED_FILE},iocharset=utf8,ro,uid=$(id -u "$RUN_USER"),gid=$(id -g "$RUN_USER"),vers=3.0,_netdev,nofail,x-systemd.automount"
else
  FSTAB_OPTS="guest,iocharset=utf8,ro,uid=$(id -u "$RUN_USER"),gid=$(id -g "$RUN_USER"),vers=3.0,_netdev,nofail,x-systemd.automount"
fi
FSTAB_LINE="${NAS_UNC}  ${NAS_MOUNT}  cifs  ${FSTAB_OPTS}  0  0"
if ! grep -qsF "${NAS_UNC}  ${NAS_MOUNT}" /etc/fstab; then
  echo "$FSTAB_LINE" >> /etc/fstab; ok "Added CIFS mount to /etc/fstab"
else
  sed -i "s|^.*${NAS_MOUNT}.*cifs.*$|${FSTAB_LINE}|" /etc/fstab; ok "Updated CIFS mount in /etc/fstab"
fi
systemctl daemon-reload
umount "$NAS_MOUNT" 2>/dev/null || true
if mount "$NAS_MOUNT" 2>/dev/null; then ok "Mounted $NAS_UNC -> $NAS_MOUNT (read-only)";
else warn "Could not mount the NAS yet — check host/credentials. It will retry on boot (nofail)."; fi
[ -f "${NAS_RELEASES_DIR}/manifest.json" ] && ok "Found manifest.json on the NAS" || warn "manifest.json not visible at ${NAS_RELEASES_DIR} yet."

# ---------------------------------------------------------------------------
say "3/6  Configuration (.env)"
ask PORT          "    App port"                       "${PORT:-3000}"
ask PUBLIC_URL    "    Public URL (tunnel/domain, or leave localhost for now)" "${PUBLIC_URL:-http://localhost:${PORT}}"
ask ADMIN_EMAILS  "    Your admin email(s), comma-separated" "${ADMIN_EMAILS:-trevorzimmerman26@gmail.com}"
ask SYNC_INTERVAL_HOURS "    Re-sync from NAS every N hours" "${SYNC_INTERVAL_HOURS:-24}"
SESSION_SECRET="${SESSION_SECRET:-$(openssl rand -hex 32 2>/dev/null || head -c32 /dev/urandom | od -An -tx1 | tr -d ' \n')}"

ENV_FILE="${APP_DIR}/.env"
if [ -f "$ENV_FILE" ] && [ "$ASSUME_YES" != "1" ]; then
  ask KEEP_ENV "    .env already exists. Overwrite? (y/N)" "N"
  [ "${KEEP_ENV,,}" = "y" ] || { warn "Keeping existing .env"; SKIP_ENV=1; }
fi
if [ "${SKIP_ENV:-0}" != "1" ]; then
  cat > "$ENV_FILE" <<EOF
PORT=${PORT}
HOST=127.0.0.1
PUBLIC_URL=${PUBLIC_URL%/}
SESSION_SECRET=${SESSION_SECRET}
ADMIN_EMAILS=${ADMIN_EMAILS}

NAS_RELEASES_DIR=${NAS_RELEASES_DIR}
SYNC_INTERVAL_HOURS=${SYNC_INTERVAL_HOURS}

GOOGLE_CLIENT_ID=${GOOGLE_CLIENT_ID:-}
GOOGLE_CLIENT_SECRET=${GOOGLE_CLIENT_SECRET:-}

SMTP_HOST=${SMTP_HOST:-}
SMTP_PORT=${SMTP_PORT:-587}
SMTP_USER=${SMTP_USER:-}
SMTP_PASS=${SMTP_PASS:-}
MAIL_FROM=${MAIL_FROM:-Oversight <no-reply@localhost>}

STRIPE_SECRET_KEY=${STRIPE_SECRET_KEY:-}
STRIPE_PUBLISHABLE_KEY=${STRIPE_PUBLISHABLE_KEY:-}
STRIPE_WEBHOOK_SECRET=${STRIPE_WEBHOOK_SECRET:-}
EOF
  chmod 600 "$ENV_FILE"; chown "$RUN_USER":"$RUN_USER" "$ENV_FILE"
  ok "Wrote $ENV_FILE"
fi

# ---------------------------------------------------------------------------
say "4/6  Installing app dependencies + first sync"
chown -R "$RUN_USER":"$RUN_USER" "$APP_DIR"
sudo -u "$RUN_USER" bash -lc "cd '$APP_DIR' && npm install --omit=dev" \
  || die "npm install failed (build tools for better-sqlite3 present? see errors above)"
sudo -u "$RUN_USER" bash -lc "cd '$APP_DIR' && npm run sync" || warn "First sync didn't complete — check the NAS mount, then use Admin → 'Pull latest from NAS now'."

# ---------------------------------------------------------------------------
say "5/6  systemd service"
SERVICE=/etc/systemd/system/oversight.service
# systemd automount unit name for the mount point (e.g. /mnt/x -> mnt-x.automount)
AUTOMOUNT_UNIT="$(systemd-escape -p --suffix=automount "$NAS_MOUNT")"
cat > "$SERVICE" <<EOF
[Unit]
Description=Oversight store (Node.js)
After=network-online.target ${AUTOMOUNT_UNIT}
Wants=network-online.target

[Service]
Type=simple
User=${RUN_USER}
WorkingDirectory=${APP_DIR}
ExecStart=$(command -v node) ${APP_DIR}/server/index.js
Restart=on-failure
RestartSec=5
Environment=NODE_ENV=production

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable oversight >/dev/null 2>&1 || true
systemctl restart oversight
sleep 2
if systemctl is-active --quiet oversight; then ok "Service 'oversight' is running on port ${PORT}"; else warn "Service failed to start — see: journalctl -u oversight -e"; fi

# ---------------------------------------------------------------------------
say "6/6  Cloudflare Tunnel (publish to the internet)"
if ! command -v cloudflared >/dev/null; then
  ask INSTALL_CF "    Install cloudflared now? (Y/n)" "Y"
  if [ "${INSTALL_CF,,}" != "n" ]; then
    ARCH=$(dpkg --print-architecture)
    curl -fsSL "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-${ARCH}.deb" -o /tmp/cloudflared.deb \
      && apt-get install -y -qq /tmp/cloudflared.deb >/dev/null && ok "cloudflared installed" || warn "cloudflared install failed; do it manually later."
  fi
fi
cat <<EOF

  To publish the site with Cloudflare (no port-forwarding, free TLS):

   • Quick test URL (ephemeral, changes each run):
       cloudflared tunnel --url http://localhost:${PORT}
     Copy the https://<random>.trycloudflare.com URL it prints into PUBLIC_URL
     in ${ENV_FILE}, then:  sudo systemctl restart oversight

   • Permanent (recommended once you buy a domain):
       cloudflared tunnel login
       cloudflared tunnel create oversight
       cloudflared tunnel route dns oversight yourdomain.com
       # point the tunnel's ingress at http://localhost:${PORT}, then run it as a service:
       sudo cloudflared service install
     Set PUBLIC_URL=https://yourdomain.com and restart the service.

EOF

echo -e "${GREEN}Done.${NC} Local:  http://localhost:${PORT}"
echo "  Next: open the site, sign in with your admin email, then visit /admin to add Stripe keys + Google Sign-In."
echo "  Logs:  journalctl -u oversight -f    |    Re-sync: (Admin page) or  npm run sync"
