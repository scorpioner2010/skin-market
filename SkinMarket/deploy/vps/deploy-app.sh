#!/usr/bin/env bash
set -euo pipefail

APP_ROOT="/var/www/xmania"
REPO_DIR="${APP_ROOT}/SkinMarket"
PROJECT_FILE="${REPO_DIR}/SkinMarket.csproj"
PUBLISH_DIR="${APP_ROOT}/publish"
RELEASES_DIR="${APP_ROOT}/releases"
ENV_FILE="/etc/xmania/xmania.env"
NUGET_CONFIG="/root/.nuget/NuGet/NuGet.Config"
APP_USER="xmania"
BOT_DIR="${REPO_DIR}/bot-service"
TIMESTAMP="$(date +%Y%m%d%H%M%S)"
RELEASE_DIR="${RELEASES_DIR}/${TIMESTAMP}"

require_root() {
  if [[ "${EUID}" -ne 0 ]]; then
    echo "Run this script as root: sudo bash deploy/vps/deploy-app.sh"
    exit 1
  fi
}

ensure_nuget_config() {
  mkdir -p "$(dirname "${NUGET_CONFIG}")"
  if [[ -s "${NUGET_CONFIG}" ]] && grep -q "<configuration" "${NUGET_CONFIG}" && grep -q "</configuration>" "${NUGET_CONFIG}"; then
    return
  fi

  if [[ -e "${NUGET_CONFIG}" ]]; then
    cp "${NUGET_CONFIG}" "${NUGET_CONFIG}.bak.$(date +%Y%m%d%H%M%S)"
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
}

load_environment() {
  if [[ -f "${ENV_FILE}" ]]; then
    while IFS= read -r line || [[ -n "${line}" ]]; do
      line="${line%$'\r'}"
      [[ "${line}" =~ ^[[:space:]]*$ ]] && continue
      [[ "${line}" =~ ^[[:space:]]*# ]] && continue
      line="${line#export }"
      [[ "${line}" == *"="* ]] || continue

      local name="${line%%=*}"
      local value="${line#*=}"
      if [[ "${name}" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
        export "${name}=${value}"
      else
        echo "Skipping invalid environment variable name in ${ENV_FILE}: ${name}"
      fi
    done < "${ENV_FILE}"
  else
    echo "Warning: ${ENV_FILE} does not exist. Build will continue, migrations and runtime may fail until it is created."
  fi
}

has_database_config() {
  [[ -n "${DATABASE_URL:-}" || -n "${ConnectionStrings__DefaultConnection:-}" ]]
}

run_migrations_if_configured() {
  if [[ ! -d "${REPO_DIR}/Migrations" ]]; then
    echo "No EF Core Migrations directory found; skipping database update."
    return
  fi

  if ! has_database_config; then
    echo "No DATABASE_URL or ConnectionStrings__DefaultConnection configured; skipping database update."
    return
  fi

  echo "Applying EF Core migrations..."
  if ! command -v dotnet-ef >/dev/null 2>&1 && [[ ! -x "${HOME}/.dotnet/tools/dotnet-ef" ]]; then
    dotnet tool install --global dotnet-ef --version "8.*"
  fi

  local dotnet_ef="dotnet-ef"
  if ! command -v dotnet-ef >/dev/null 2>&1; then
    dotnet_ef="${HOME}/.dotnet/tools/dotnet-ef"
  fi

  "${dotnet_ef}" database update --project "${PROJECT_FILE}" --startup-project "${PROJECT_FILE}" --configuration Release
}

restart_if_present() {
  local service_name="$1"
  if systemctl list-unit-files --no-legend "${service_name}" 2>/dev/null | grep -q "${service_name}"; then
    systemctl restart "${service_name}"
    systemctl --no-pager --full status "${service_name}" || true
  else
    echo "Systemd service not installed, skipping restart: ${service_name}"
  fi
}

require_root

if [[ ! -d "${REPO_DIR}/.git" ]]; then
  echo "Repo is not cloned at ${REPO_DIR}"
  exit 1
fi

if [[ ! -f "${PROJECT_FILE}" ]]; then
  echo "Project file not found: ${PROJECT_FILE}"
  exit 1
fi

ensure_nuget_config
load_environment
git config --global --add safe.directory "${REPO_DIR}" || true

echo "Pulling latest code..."
git -C "${REPO_DIR}" pull --ff-only

echo "Restoring and building ASP.NET Core app..."
dotnet restore "${PROJECT_FILE}"
dotnet build "${PROJECT_FILE}" --configuration Release --no-restore

echo "Publishing to ${RELEASE_DIR}..."
mkdir -p "${RELEASE_DIR}" "${RELEASES_DIR}"
dotnet publish "${PROJECT_FILE}" --configuration Release --no-build --output "${RELEASE_DIR}"

if [[ -d "${BOT_DIR}" && -f "${BOT_DIR}/package.json" ]]; then
  echo "Installing bot-service dependencies..."
  if [[ -f "${BOT_DIR}/package-lock.json" ]]; then
    npm ci --prefix "${BOT_DIR}"
  else
    npm install --prefix "${BOT_DIR}"
  fi
else
  echo "No bot-service package.json found; skipping bot dependency install."
fi

run_migrations_if_configured

echo "Activating published release..."
if [[ -e "${PUBLISH_DIR}" && ! -L "${PUBLISH_DIR}" ]]; then
  BACKUP_DIR="${APP_ROOT}/publish.backup.${TIMESTAMP}"
  mv "${PUBLISH_DIR}" "${BACKUP_DIR}"
  echo "Moved previous publish directory to ${BACKUP_DIR}"
fi
ln -sfnT "${RELEASE_DIR}" "${PUBLISH_DIR}"
chown -R "${APP_USER}:www-data" "${APP_ROOT}"

echo "Restarting services..."
restart_if_present "xmania-steam-bot.service"
restart_if_present "xmania-web.service"

cat <<EOF

Deploy complete.

Useful checks:
  systemctl status xmania-web
  systemctl status xmania-steam-bot
  journalctl -u xmania-web -f
  journalctl -u xmania-steam-bot -f
  curl -i http://127.0.0.1:5000
  curl -i "https://steamcommunity.com/inventory/76561198741807571/730/2?l=english&count=2000"

Rollback to the previous release:
  ls -1 ${RELEASES_DIR}
  sudo ln -sfnT ${RELEASES_DIR}/<previous-timestamp> ${PUBLISH_DIR}
  sudo systemctl restart xmania-web
EOF
