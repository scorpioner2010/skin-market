#!/usr/bin/env bash
set -euo pipefail

APP_ROOT="/var/www/xmania"
REPO_DIR="${APP_ROOT}"
PROJECT_DIR="${APP_ROOT}/SkinMarket"
PUBLISH_DIR="${APP_ROOT}/publish"
RELEASES_DIR="${APP_ROOT}/releases"
LOG_DIR="/var/log/xmania"
ENV_DIR="/etc/xmania"
NUGET_CONFIG="/root/.nuget/NuGet/NuGet.Config"
APP_USER="xmania"

require_root() {
  if [[ "${EUID}" -ne 0 ]]; then
    echo "Run this script as root: sudo bash SkinMarket/deploy/vps/setup-vps.sh"
    exit 1
  fi
}

install_dotnet_repo_if_needed() {
  if apt-cache show dotnet-sdk-8.0 >/dev/null 2>&1; then
    return
  fi

  local package_path="/tmp/packages-microsoft-prod.deb"
  wget -q "https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb" -O "${package_path}"
  dpkg -i "${package_path}"
  rm -f "${package_path}"
  apt-get update
}

ensure_nuget_config() {
  mkdir -p "$(dirname "${NUGET_CONFIG}")"
  if [[ -s "${NUGET_CONFIG}" ]] && grep -q "<configuration" "${NUGET_CONFIG}" && grep -q "</configuration>" "${NUGET_CONFIG}"; then
    echo "NuGet.Config already looks valid: ${NUGET_CONFIG}"
    return
  fi

  if [[ -e "${NUGET_CONFIG}" ]]; then
    local backup="${NUGET_CONFIG}.bak.$(date +%Y%m%d%H%M%S)"
    cp "${NUGET_CONFIG}" "${backup}"
    echo "Backed up invalid NuGet.Config to ${backup}"
  fi

  cat > "${NUGET_CONFIG}" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
XML
  echo "Wrote valid NuGet.Config with nuget.org source: ${NUGET_CONFIG}"
}

require_root

echo "Updating apt package indexes..."
apt-get update

echo "Installing base packages..."
apt-get install -y curl wget git unzip ca-certificates gnupg nginx nodejs npm

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^8\.'; then
  echo "Installing .NET SDK 8..."
  install_dotnet_repo_if_needed
  apt-get install -y dotnet-sdk-8.0
else
  echo ".NET SDK 8 is already installed."
fi

ensure_nuget_config

echo "Creating system user and directories..."
if ! id "${APP_USER}" >/dev/null 2>&1; then
  useradd --system --create-home --home-dir "${APP_ROOT}" --shell /usr/sbin/nologin "${APP_USER}"
else
  echo "User ${APP_USER} already exists."
fi

mkdir -p "${APP_ROOT}" "${PUBLISH_DIR}" "${RELEASES_DIR}" "${LOG_DIR}" "${ENV_DIR}"
chown -R "${APP_USER}:www-data" "${APP_ROOT}" "${LOG_DIR}"
chmod 750 "${APP_ROOT}" "${LOG_DIR}"
chmod 755 "${PUBLISH_DIR}"
chmod 755 "${RELEASES_DIR}"
chmod 750 "${ENV_DIR}"
git config --global --add safe.directory "${REPO_DIR}" || true

if [[ ! -f "${ENV_DIR}/xmania.env" ]]; then
  echo "Environment file is not present yet: ${ENV_DIR}/xmania.env"
  echo "Create it from ${PROJECT_DIR}/deploy/vps/xmania.env.example after cloning the repo."
else
  chmod 640 "${ENV_DIR}/xmania.env"
  chown root:"${APP_USER}" "${ENV_DIR}/xmania.env"
fi

systemctl enable nginx >/dev/null
systemctl restart nginx

cat <<EOF

VPS setup complete.

Next steps:
1. Clone the repo directly into ${REPO_DIR} if it is not already there:
   git clone https://github.com/scorpioner2010/skin-market.git ${REPO_DIR}
2. Copy ${PROJECT_DIR}/deploy/vps/xmania.env.example to ${ENV_DIR}/xmania.env and fill secrets from Render.
3. Install service files:
   sudo cp ${PROJECT_DIR}/deploy/vps/xmania-web.service /etc/systemd/system/
   sudo cp ${PROJECT_DIR}/deploy/vps/xmania-steam-bot.service /etc/systemd/system/
   sudo systemctl daemon-reload
4. Install nginx config:
   Only do this for the first nginx install. Do not overwrite a live Certbot HTTPS config unless you plan to rerun Certbot.
   sudo cp ${PROJECT_DIR}/deploy/vps/nginx-xmania.conf /etc/nginx/sites-available/xmania
   sudo ln -s /etc/nginx/sites-available/xmania /etc/nginx/sites-enabled/xmania
   sudo nginx -t && sudo systemctl reload nginx
5. Deploy:
   cd ${REPO_DIR}
   sudo bash SkinMarket/deploy/vps/deploy-app.sh
EOF
