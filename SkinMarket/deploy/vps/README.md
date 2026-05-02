# SkinMarket Vultr VPS Deployment

SkinMarket is moving from Render to a Vultr Ubuntu VPS because Render returned HTTP 429/null for Steam inventory requests while the Vultr VPS returned HTTP 200 OK for the same Steam inventory URL.

Target paths:

- App root: `/var/www/xmania`
- Repo: `/var/www/xmania/SkinMarket`
- Project: `/var/www/xmania/SkinMarket/SkinMarket.csproj`
- Publish path: `/var/www/xmania/publish`
- Environment file: `/etc/xmania/xmania.env`

## Prerequisites

- Ubuntu 24.04 LTS x64
- Root or sudo access
- DNS can be added later; nginx currently accepts `server_name _;`
- Disable Render Steam workers before enabling VPS workers so two bots/workers do not process the same Steam work.

## Clone The Repo

```bash
sudo mkdir -p /var/www/xmania
sudo chown -R "$USER":www-data /var/www/xmania
git clone <your-repo-url> /var/www/xmania/SkinMarket
```

## Run VPS Setup

```bash
cd /var/www/xmania/SkinMarket
sudo bash deploy/vps/setup-vps.sh
```

The setup script installs required packages, installs .NET SDK 8 if missing, fixes `/root/.nuget/NuGet/NuGet.Config` if it is missing or invalid, creates `/var/www/xmania`, `/var/www/xmania/publish`, `/var/log/xmania`, and prepares the `xmania` system user.

## Configure Environment

```bash
sudo cp /var/www/xmania/SkinMarket/deploy/vps/xmania.env.example /etc/xmania/xmania.env
sudo nano /etc/xmania/xmania.env
sudo chown root:xmania /etc/xmania/xmania.env
sudo chmod 640 /etc/xmania/xmania.env
```

Copy the Render environment variables into `/etc/xmania/xmania.env`, especially:

- `DATABASE_URL` or `ConnectionStrings__DefaultConnection`
- `STEAM_API_KEY`
- `STEAM_BOT_ENABLED`
- `STEAM_BOT_USERNAME`
- `STEAM_BOT_PASSWORD`
- `STEAM_BOT_STEAM_ID`
- `STEAM_BOT_TRADE_URL`
- `STEAM_BOT_SHARED_SECRET`
- `STEAM_BOT_IDENTITY_SECRET`
- `STEAM_BOT_SERVICE_URL=http://127.0.0.1:5174`
- `STEAM_BOT_SERVICE_PORT=5174`

The ASP.NET app also supports the `SteamBot__...` and `SteamApi__ApiKey` keys included in the template.

## Install Systemd Services

```bash
sudo cp /var/www/xmania/SkinMarket/deploy/vps/xmania-web.service /etc/systemd/system/
sudo cp /var/www/xmania/SkinMarket/deploy/vps/xmania-steam-bot.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable xmania-web
sudo systemctl enable xmania-steam-bot
```

Bot details found in the repo:

- Package path: `/var/www/xmania/SkinMarket/bot-service/package.json`
- Start command: `npm start`
- Node entrypoint: `node src/server.js`
- Default port: `5174`
- Health endpoint: `http://127.0.0.1:5174/healthz`
- Required bot env: `STEAM_BOT_ENABLED`, `STEAM_BOT_USERNAME`, `STEAM_BOT_PASSWORD`, `STEAM_BOT_STEAM_ID`, `STEAM_BOT_TRADE_URL`, `STEAM_BOT_SHARED_SECRET`, `STEAM_BOT_IDENTITY_SECRET`, `STEAM_API_KEY`

## Configure Nginx

```bash
sudo cp /var/www/xmania/SkinMarket/deploy/vps/nginx-xmania.conf /etc/nginx/sites-available/xmania
sudo ln -s /etc/nginx/sites-available/xmania /etc/nginx/sites-enabled/xmania
sudo nginx -t
sudo systemctl reload nginx
```

Later, replace `server_name _;` with your domain and add TLS.

## Deploy The App

```bash
cd /var/www/xmania/SkinMarket
sudo bash deploy/vps/deploy-app.sh
```

The deploy script runs `git pull --ff-only`, restores, builds, publishes to `/var/www/xmania/releases/<timestamp>`, points `/var/www/xmania/publish` to that release, installs bot dependencies, applies EF Core migrations when a database connection string is configured, and restarts configured services.

## Test

```bash
dotnet --version
node -v
npm -v
systemctl status nginx
systemctl status xmania-web
systemctl status xmania-steam-bot
curl -i http://127.0.0.1:5000
curl -i http://70.34.255.46
curl -i http://127.0.0.1:5174/healthz
curl -i "https://steamcommunity.com/inventory/76561198741807571/730/2?l=english&count=2000"
```

Expected Steam diagnostic from the Vultr VPS is HTTP 200 OK with `success: 1`.

## Logs

```bash
journalctl -u xmania-web -f
journalctl -u xmania-steam-bot -f
journalctl -u nginx -f
```

## Rollback

The deploy script keeps timestamped releases in `/var/www/xmania/releases`.

```bash
ls -1 /var/www/xmania/releases
sudo ln -sfnT /var/www/xmania/releases/<previous-timestamp> /var/www/xmania/publish
sudo systemctl restart xmania-web
```

If the bot code changed and you need to roll back the repo too:

```bash
cd /var/www/xmania/SkinMarket
git log --oneline -5
sudo git checkout <previous-commit>
sudo npm ci --prefix /var/www/xmania/SkinMarket/bot-service
sudo systemctl restart xmania-steam-bot
```

## Checklist

- Copy Render environment variables to `/etc/xmania/xmania.env`.
- Disable Render Steam workers before enabling VPS workers.
- Deploy on the VPS with `sudo bash deploy/vps/deploy-app.sh`.
- Test Steam inventory from the VPS.
- Update DNS later after the IP test works.
