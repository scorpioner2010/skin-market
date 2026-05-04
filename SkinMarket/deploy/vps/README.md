# SkinMarket Vultr VPS Deployment

SkinMarket runs on a Vultr Ubuntu VPS at the current HTTPS test domain:

- `https://70-34-255-46.sslip.io`

HTTPS is required for Unity WebGL browser features. Use the HTTPS domain when testing the Unity WebGL game in a browser.

## VPS Structure

- App root: `/var/www/xmania`
- Git repository root: `/var/www/xmania`
- ASP.NET project folder: `/var/www/xmania/SkinMarket`
- ASP.NET project file: `/var/www/xmania/SkinMarket/SkinMarket.csproj`
- Bot service folder: `/var/www/xmania/SkinMarket/bot-service`
- Deploy files: `/var/www/xmania/SkinMarket/deploy/vps`
- Publish symlink/output root: `/var/www/xmania/publish`
- Releases folder: `/var/www/xmania/releases`
- Env file: `/etc/xmania/xmania.env`
- Web systemd service: `xmania-web.service`
- Bot systemd service: `xmania-steam-bot.service`
- Nginx site config: `/etc/nginx/sites-available/xmania`

## Clone The Repo

Clone the repository directly into `/var/www/xmania`.

```bash
sudo mkdir -p /var/www
sudo git clone https://github.com/scorpioner2010/skin-market.git /var/www/xmania
```

Do not clone into `/var/www/xmania/SkinMarket`; `SkinMarket` is the ASP.NET project folder inside the repository.

## Run VPS Setup

```bash
cd /var/www/xmania
sudo bash SkinMarket/deploy/vps/setup-vps.sh
```

The setup script installs required packages, installs .NET SDK 8 if missing, fixes `/root/.nuget/NuGet/NuGet.Config` if it is missing or invalid, creates `/var/www/xmania`, `/var/www/xmania/publish`, `/var/www/xmania/releases`, `/var/log/xmania`, `/etc/xmania`, and prepares the `xmania` system user.

The setup script does not overwrite an existing `/etc/xmania/xmania.env`.

## Configure Environment

Only create `/etc/xmania/xmania.env` when it does not already exist.

```bash
sudo cp /var/www/xmania/SkinMarket/deploy/vps/xmania.env.example /etc/xmania/xmania.env
sudo nano /etc/xmania/xmania.env
sudo chown root:xmania /etc/xmania/xmania.env
sudo chmod 640 /etc/xmania/xmania.env
```

Copy the production values into `/etc/xmania/xmania.env`. Do not commit real env values or secrets.

## Install Systemd Services

```bash
sudo cp /var/www/xmania/SkinMarket/deploy/vps/xmania-web.service /etc/systemd/system/
sudo cp /var/www/xmania/SkinMarket/deploy/vps/xmania-steam-bot.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable xmania-web
sudo systemctl enable xmania-steam-bot
```

The web service runs `/var/www/xmania/publish/SkinMarket.dll`.

The bot service uses:

- Working directory: `/var/www/xmania/SkinMarket/bot-service`
- Start command: `npm start`
- Env file: `/etc/xmania/xmania.env`

## Nginx And HTTPS

The repository includes an initial Nginx config at:

```bash
/var/www/xmania/SkinMarket/deploy/vps/nginx-xmania.conf
```

It proxies to `http://127.0.0.1:5000` and includes websocket upgrade headers.

Do not overwrite the live Nginx config after Certbot has modified it unless you plan to rerun Certbot. Overwriting `/etc/nginx/sites-available/xmania` with the repo copy can remove the live HTTPS server block and redirect rules.

Certbot was used for the current test domain:

```bash
sudo certbot --nginx -d 70-34-255-46.sslip.io --email <email> --agree-tos --redirect
```

If the default Nginx site conflicts:

```bash
sudo rm -f /etc/nginx/sites-enabled/default
sudo nginx -t
sudo systemctl reload nginx
```

## Deploy After Each Commit

Normal deployment after pulling a committed change:

```bash
ssh root@70.34.255.46
cd /var/www/xmania
git pull
bash SkinMarket/deploy/vps/deploy-app.sh
```

The deploy script runs `git pull --ff-only` inside `/var/www/xmania`, restores, builds, publishes `/var/www/xmania/SkinMarket/SkinMarket.csproj` to `/var/www/xmania/releases/<timestamp>`, updates `/var/www/xmania/publish`, installs bot dependencies from `/var/www/xmania/SkinMarket/bot-service`, applies EF Core migrations when a database connection string is configured, and restarts both services.

## Useful Checks

```bash
systemctl status xmania-web --no-pager -l
systemctl status xmania-steam-bot --no-pager -l
curl -i https://70-34-255-46.sslip.io
journalctl -u xmania-web -f
journalctl -u xmania-steam-bot -f
```

Additional checks:

```bash
curl -i http://127.0.0.1:5000
curl -i http://127.0.0.1:5174/healthz
sudo ufw status
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
```

## Rollback

The deploy script keeps timestamped releases in `/var/www/xmania/releases`.

```bash
ls -1 /var/www/xmania/releases
sudo ln -sfnT /var/www/xmania/releases/<previous-timestamp> /var/www/xmania/publish
sudo systemctl restart xmania-web
```

If the bot code changed and the repository must be rolled back too:

```bash
cd /var/www/xmania
git log --oneline -5
sudo git checkout <previous-commit>
sudo npm ci --prefix /var/www/xmania/SkinMarket/bot-service
sudo systemctl restart xmania-steam-bot
```
