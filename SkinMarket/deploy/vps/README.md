# SkinMarket Vultr VPS Deployment

SkinMarket was migrated from Render to a Vultr Ubuntu VPS because Render returned HTTP 429/null for Steam inventory requests while the Vultr VPS returned HTTP 200 OK for the same Steam inventory URL.

Current test domain:

- `https://70-34-255-46.sslip.io`

HTTPS is required for the Unity WebGL browser integration. The game may fail in browsers when served over plain HTTP.

## VPS Structure

- App root: `/var/www/xmania`
- Git repository root: `/var/www/xmania/SkinMarket`
- Main ASP.NET project file: `/var/www/xmania/SkinMarket/SkinMarket.csproj`
- Bot service folder: `/var/www/xmania/SkinMarket/bot-service`
- Publish symlink/output root: `/var/www/xmania/publish`
- Releases folder: `/var/www/xmania/releases`
- Env file: `/etc/xmania/xmania.env`
- Web systemd service: `xmania-web.service`
- Bot systemd service: `xmania-steam-bot.service`
- Nginx site config: `/etc/nginx/sites-available/xmania`

## Prerequisites

- Ubuntu 24.04 LTS x64
- Root or sudo access
- Disable Render Steam workers before enabling VPS workers so two bots/workers do not process the same Steam work.

## Clone The Repo

The repository must be cloned into `/var/www/xmania/SkinMarket`, not directly into `/var/www/xmania`.

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

The setup script installs required packages, installs .NET SDK 8 if missing, fixes `/root/.nuget/NuGet/NuGet.Config` if it is missing or invalid, creates `/var/www/xmania`, `/var/www/xmania/publish`, `/var/www/xmania/releases`, `/var/log/xmania`, and prepares the `xmania` system user.

## Configure Environment

Do not overwrite an existing `/etc/xmania/xmania.env` on a running VPS.

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

Bot service details:

- Package path: `/var/www/xmania/SkinMarket/bot-service/package.json`
- Start command: `npm start`
- Node entrypoint: `node src/server.js`
- Default port: `5174`
- Health endpoint: `http://127.0.0.1:5174/healthz`

## Configure Nginx

```bash
sudo cp /var/www/xmania/SkinMarket/deploy/vps/nginx-xmania.conf /etc/nginx/sites-available/xmania
sudo ln -s /etc/nginx/sites-available/xmania /etc/nginx/sites-enabled/xmania
sudo nginx -t
sudo systemctl reload nginx
```

The included config uses `server_name 70-34-255-46.sslip.io;` and proxies to `http://127.0.0.1:5000` with websocket upgrade headers. When switching to a real domain, update `server_name`, reload nginx, then issue a new certificate for that domain.

If the default Nginx site conflicts:

```bash
sudo rm -f /etc/nginx/sites-enabled/default
sudo nginx -t
sudo systemctl reload nginx
```

## Configure HTTPS

Certbot was used for the current test domain:

```bash
sudo certbot --nginx -d 70-34-255-46.sslip.io --email <email> --agree-tos --redirect
```

Certbot modifies the Nginx site to add the TLS server block and HTTP-to-HTTPS redirect. Keep those generated certificate lines on the VPS.

## Deploy After Each New Commit

```bash
cd /var/www/xmania/SkinMarket
git pull
bash deploy/vps/deploy-app.sh
```

Run the deploy script as root. If you are not already root, use `sudo bash deploy/vps/deploy-app.sh`.

The deploy script runs `git pull --ff-only` inside `/var/www/xmania/SkinMarket`, restores, builds, publishes `/var/www/xmania/SkinMarket/SkinMarket.csproj` to `/var/www/xmania/releases/<timestamp>`, points `/var/www/xmania/publish` to that release, installs bot dependencies from `/var/www/xmania/SkinMarket/bot-service`, applies EF Core migrations when a database connection string is configured, and restarts both services.

## Useful Checks

```bash
systemctl status xmania-web --no-pager -l
systemctl status xmania-steam-bot --no-pager -l
journalctl -u xmania-web -f
journalctl -u xmania-steam-bot -f
curl -i http://127.0.0.1:5000
curl -i https://70-34-255-46.sslip.io
```

If the browser cannot open the site, check the firewall:

```bash
sudo ufw status
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
```

Additional diagnostics:

```bash
dotnet --version
node -v
npm -v
systemctl status nginx --no-pager -l
curl -i http://127.0.0.1:5174/healthz
curl -i "https://steamcommunity.com/inventory/76561198741807571/730/2?l=english&count=2000"
```

Expected Steam diagnostic from the Vultr VPS is HTTP 200 OK with `success: 1`.

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
