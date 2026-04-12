const path = require("path");
const fs = require("fs");

function loadLocalEnv(rootDir) {
  const envFilePath = path.join(rootDir, "..", ".env.local");
  if (!fs.existsSync(envFilePath)) {
    return;
  }

  const lines = fs.readFileSync(envFilePath, "utf8").split(/\r?\n/);
  for (const rawLine of lines) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#")) {
      continue;
    }

    const separatorIndex = line.indexOf("=");
    if (separatorIndex <= 0) {
      continue;
    }

    const name = line.slice(0, separatorIndex).trim();
    if (!name || process.env[name]) {
      continue;
    }

    let value = line.slice(separatorIndex + 1).trim();
    if ((value.startsWith("\"") && value.endsWith("\"")) ||
        (value.startsWith("'") && value.endsWith("'"))) {
      value = value.slice(1, -1);
    }

    process.env[name] = value;
  }
}

function parseBoolean(value, fallback = false) {
  if (value === undefined || value === null || value === "") {
    return fallback;
  }

  return String(value).trim().toLowerCase() === "true";
}

function parseInteger(value, fallback) {
  const parsed = Number.parseInt(String(value ?? ""), 10);
  return Number.isFinite(parsed) ? parsed : fallback;
}

const rootDir = path.resolve(__dirname, "..");
loadLocalEnv(rootDir);

module.exports = {
  port: parseInteger(process.env.STEAM_BOT_SERVICE_PORT, 5174),
  steamApiKey: process.env.STEAM_API_KEY || "",
  dataDirectory: path.join(rootDir, "data"),
  polling: {
    pollInterval: parseInteger(process.env.STEAM_BOT_POLL_INTERVAL_MS, 60000),
    minimumPollInterval: parseInteger(process.env.STEAM_BOT_MINIMUM_POLL_INTERVAL_MS, 15000),
    pollFullUpdateInterval: parseInteger(process.env.STEAM_BOT_POLL_FULL_UPDATE_INTERVAL_MS, 300000)
  },
  bot: {
    enabled: parseBoolean(process.env.STEAM_BOT_ENABLED, false),
    username: process.env.STEAM_BOT_USERNAME || "",
    password: process.env.STEAM_BOT_PASSWORD || "",
    steamId: process.env.STEAM_BOT_STEAM_ID || "",
    tradeUrl: process.env.STEAM_BOT_TRADE_URL || "",
    sharedSecret: process.env.STEAM_BOT_SHARED_SECRET || "",
    identitySecret: process.env.STEAM_BOT_IDENTITY_SECRET || ""
  }
};
