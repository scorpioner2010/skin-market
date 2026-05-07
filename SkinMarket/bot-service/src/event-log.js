const fs = require("fs");
const path = require("path");

const SecretKeyPattern = /(secret|password|token|cookie|session|auth)/i;
const MaxEntries = 1000;
const MaxPersistedLines = 2000;

class BotEventLog {
  constructor(dataDirectory) {
    this.entries = [];
    this.sequence = 0;
    this.filePath = path.join(dataDirectory, "bot-events.jsonl");
    this.loadFromDisk();
  }

  add(level, source, eventType, message, metadata = null) {
    const entry = {
      id: `${Date.now()}-${++this.sequence}`,
      timestampUtc: new Date().toISOString(),
      level: normalizeLevel(level),
      source: normalizeText(source, "steam-bot"),
      eventType: normalizeText(eventType, "event"),
      message: String(message || "Bot event."),
      metadata: sanitizeMetadata(metadata),
      correlationId: firstValue(metadata, "correlationId"),
      tradeOperationId: firstValue(metadata, "tradeOperationId", "entityId", "marketItemId"),
      offerId: firstValue(metadata, "offerId", "tradeOfferId"),
      serviceState: firstValue(metadata, "serviceState")
    };

    this.entries.unshift(entry);
    while (this.entries.length > MaxEntries) {
      this.entries.pop();
    }

    this.appendToDisk(entry);
    return entry;
  }

  query(filters) {
    const limit = Math.min(Math.max(Number(filters.limit || 100), 1), 500);
    return this.entries
      .filter((entry) => matches(entry.level, filters.level))
      .filter((entry) => matches(entry.source, filters.source))
      .filter((entry) => matches(entry.eventType, filters.eventType))
      .filter((entry) => matches(entry.tradeOperationId, filters.tradeOperationId))
      .filter((entry) => matches(entry.offerId, filters.offerId))
      .slice(0, limit);
  }

  loadFromDisk() {
    try {
      if (!fs.existsSync(this.filePath)) {
        return;
      }

      const lines = fs.readFileSync(this.filePath, "utf8")
        .split(/\r?\n/)
        .filter(Boolean)
        .slice(-MaxEntries);

      this.entries = lines
        .map((line) => {
          try {
            return JSON.parse(line);
          } catch {
            return null;
          }
        })
        .filter(Boolean)
        .reverse();
    } catch {
      this.entries = [];
    }
  }

  appendToDisk(entry) {
    try {
      fs.mkdirSync(path.dirname(this.filePath), { recursive: true });
      fs.appendFileSync(this.filePath, `${JSON.stringify(entry)}\n`, "utf8");
      this.compactIfNeeded();
    } catch {
    }
  }

  compactIfNeeded() {
    try {
      const content = fs.readFileSync(this.filePath, "utf8");
      const lines = content.split(/\r?\n/).filter(Boolean);
      if (lines.length <= MaxPersistedLines) {
        return;
      }

      fs.writeFileSync(this.filePath, `${lines.slice(-MaxPersistedLines).join("\n")}\n`, "utf8");
    } catch {
    }
  }
}

function normalizeLevel(level) {
  const value = String(level || "Info").trim();
  if (/^warn/i.test(value)) {
    return "Warning";
  }

  if (/^err/i.test(value)) {
    return "Error";
  }

  if (/^debug/i.test(value)) {
    return "Debug";
  }

  return "Info";
}

function normalizeText(value, fallback) {
  const text = String(value || "").trim();
  return text || fallback;
}

function matches(value, filter) {
  if (!filter) {
    return true;
  }

  return String(value || "").toLowerCase() === String(filter).trim().toLowerCase();
}

function firstValue(metadata, ...keys) {
  if (!metadata || typeof metadata !== "object") {
    return null;
  }

  for (const key of keys) {
    const value = metadata[key];
    if (value !== undefined && value !== null && value !== "") {
      return String(value);
    }
  }

  return null;
}

function sanitizeMetadata(metadata) {
  if (!metadata || typeof metadata !== "object") {
    return {};
  }

  const result = {};
  for (const [key, value] of Object.entries(metadata)) {
    if (SecretKeyPattern.test(key)) {
      result[key] = "[redacted]";
      continue;
    }

    if (value === undefined || value === null || value === "") {
      continue;
    }

    if (typeof value === "object") {
      result[key] = "[object]";
      continue;
    }

    result[key] = truncate(String(value), 500);
  }

  return result;
}

function truncate(value, maxLength) {
  return value.length <= maxLength ? value : `${value.slice(0, maxLength)}...`;
}

module.exports = {
  BotEventLog,
  sanitizeMetadata
};
