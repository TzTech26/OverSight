# Oversight store

The marketing site + licensed download store for **Oversight** (the Windows 11
screen magnifier / auto-clicker in this repo). It sells a one-time license via
Stripe, gates downloads behind purchase, and serves installers pulled straight
from your NAS `releases/` folder.

```
Buyer → landing page → sign in (Google or email) → Stripe checkout
      → /download (version dropdown, latest verified default, betas optional)
Owner → /admin (re-sync NAS, set price, Stripe keys, channels, users)
NAS releases/ ──(mount + 24h sync)──► data/releases/ (served files)
```

- **Runtime:** Node.js + Express, EJS templates, SQLite (`better-sqlite3`).
- **Deploy:** a Linux box on your LAN that mounts the NAS, published via a
  **Cloudflare Tunnel** (no port-forwarding, free TLS — no cert files to manage).
- **Reads your existing pipeline:** it consumes [../releases/manifest.json](../releases/manifest.json)
  as-is — `verified` builds are the public default, `beta` builds are shown as
  “Work in progress”. Building/promoting releases stays exactly as it is today.

## Install (on the Linux box)

Copy this `website/` folder to the Linux machine (or run it from the NAS mount),
then:

```bash
sudo bash install.sh
```

It installs Node + deps, mounts the NAS share (CIFS, read-only), writes `.env`,
does the first sync, creates a `oversight` systemd service (starts on boot), and
offers to install `cloudflared`. See the script header for non-interactive use.

Local check afterwards: `http://localhost:3000`. Logs: `journalctl -u oversight -f`.

## First-run setup checklist

1. **Sign in** with the admin email you set (`ADMIN_EMAILS`). Magic-link URLs are
   printed to the server log until SMTP is configured.
2. Open **`/admin`** and:
   - **Pull latest from NAS now** — confirms the mount works and lists builds.
   - Set **price**, product copy, and which **channels** buyers can download.
   - Paste **Stripe keys** (or set them in `.env`).
3. **Publish** with Cloudflare (see the script's step 6 output). Put the resulting
   `https://…` address in `PUBLIC_URL` and `sudo systemctl restart oversight`.

## Configuring Stripe (one-time purchase)

1. Create a Stripe account → **Developers → API keys**. Copy the **Secret** and
   **Publishable** keys (test keys first: `sk_test_…` / `pk_test_…`).
2. Put them in `/admin` (or `.env`). No product/price needs to be pre-created —
   the app charges the price you set in Admin at checkout time.
3. **Webhook:** Developers → Webhooks → *Add endpoint*
   - URL: `https://<your-public-url>/stripe/webhook`
   - Event: `checkout.session.completed`
   - Copy the **Signing secret** (`whsec_…`) into `/admin` or `.env`.
   (Even without the webhook, a purchase is reconciled on the Stripe return page —
   the webhook just makes it bulletproof.)
4. Test with card `4242 4242 4242 4242`, any future expiry/CVC. Go live by
   swapping in `sk_live_…` / `pk_live_…` keys and a live-mode webhook.

## Configuring Google Sign-In (optional — email login works without it)

1. [console.cloud.google.com](https://console.cloud.google.com) → **APIs & Services
   → Credentials → Create OAuth client ID → Web application**.
2. **Authorized redirect URI:** `https://<your-public-url>/auth/google/callback`
3. Put the **Client ID/Secret** in `.env` (`GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET`)
   and restart. (Google requires HTTPS + a real domain to publish the consent
   screen — use email magic-link until then.)

## How downloads stay current

`install.sh` mounts the NAS read-only and the app mirrors `manifest.json` + the
per-channel installer `.exe` files into `data/releases/` **on boot and every
`SYNC_INTERVAL_HOURS`** (default 24). You can also hit **Pull latest from NAS now**
in Admin any time you promote a build. Serving from the local mirror means the
store keeps working even if the NAS is briefly offline.

## Windows path → Linux path

Your NAS folder is `z:\Projects\Oversight\releases` on Windows. On the Linux box
it becomes a CIFS mount, e.g. share `//NAS/Projects` mounted at
`/mnt/oversight-nas`, so `NAS_RELEASES_DIR=/mnt/oversight-nas/Oversight/releases`.
The installer asks for these and wires them into `/etc/fstab`.

## Project layout

```
website/
├── install.sh              one-shot Linux installer (Node, NAS mount, systemd, cloudflared)
├── .env.example            copy to .env (install.sh generates it for you)
├── server/
│   ├── index.js            Express app + wiring
│   ├── config.js           env + data/settings.json (admin-editable)
│   ├── db.js               SQLite (users, purchases, login tokens)
│   ├── auth.js             Google OAuth + email magic-link
│   ├── stripe.js           checkout + webhook + entitlement fulfillment
│   ├── sync.js             NAS mirror, manifest reader, scheduler
│   ├── downloads.js        entitlement-gated download routes
│   ├── admin.js            owner console routes
│   ├── middleware.js       auth/entitlement guards
│   ├── mailer.js           SMTP (falls back to logging)
│   └── scripts/            sync-now.js, make-admin.js
├── views/                  EJS templates (landing, login, download, account, admin)
├── public/                 CSS + favicon
└── data/                   SQLite DB, sessions, settings.json, releases/ mirror  (gitignored)
```

## Handy commands

```bash
sudo systemctl restart oversight     # apply .env changes
journalctl -u oversight -f           # live logs
npm run sync                         # pull from NAS now (CLI)
npm run make-admin you@example.com   # grant admin to an existing user
```
