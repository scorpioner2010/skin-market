const express = require("express");
const config = require("./config");
const logger = require("./logger");
const { SteamBot, HttpError } = require("./steam-bot");
const { BotEventLog } = require("./event-log");

const app = express();
app.use(express.json());

const eventLog = new BotEventLog(config.dataDirectory);
const bot = new SteamBot(config, logger, eventLog);

app.get("/healthz", (_req, res) => {
  bot.recordHealthCheck();
  res.status(200).json({
    status: "ok",
    bot: bot.getHealth()
  });
});

app.get("/api/bot/logs", (req, res) => {
  res.status(200).json({
    entries: bot.getLogs({
      limit: req.query.limit,
      level: req.query.level,
      source: req.query.source,
      eventType: req.query.eventType,
      tradeOperationId: req.query.tradeOperationId,
      offerId: req.query.offerId
    })
  });
});

app.get("/api/logs", (req, res) => {
  res.status(200).json({
    entries: bot.getLogs({
      limit: req.query.limit,
      level: req.query.level,
      source: req.query.source,
      eventType: req.query.eventType,
      tradeOperationId: req.query.tradeOperationId,
      offerId: req.query.offerId
    })
  });
});

app.post("/api/trades/intake", async (req, res, next) => {
  try {
    const result = await bot.createIntakeTrade(req.body);
    res.status(result.success ? 200 : 400).json(result);
  } catch (error) {
    next(error);
  }
});

app.post("/api/trades/delivery", async (req, res, next) => {
  try {
    const result = await bot.createDeliveryTrade(req.body);
    res.status(result.success ? 200 : 400).json(result);
  } catch (error) {
    next(error);
  }
});

app.post("/api/trades/statuses", async (req, res, next) => {
  try {
    const offers = await bot.getOfferStatuses(req.body?.offers);
    res.status(200).json({ offers });
  } catch (error) {
    next(error);
  }
});

app.post("/api/trades/confirm", async (req, res, next) => {
  try {
    const result = await bot.confirmOffer(req.body);
    res.status(result.success ? 200 : 409).json(result);
  } catch (error) {
    next(error);
  }
});

app.post("/api/trades/cancel", async (req, res, next) => {
  try {
    const result = await bot.cancelOffer(req.body);
    res.status(result.success ? 200 : 409).json(result);
  } catch (error) {
    next(error);
  }
});

app.post("/api/inventory/user", async (req, res, next) => {
  try {
    const result = await bot.getUserInventory(req.body);
    res.status(200).json(result);
  } catch (error) {
    next(error);
  }
});

app.use((error, req, res, _next) => {
  const statusCode = error instanceof HttpError ? error.statusCode : 500;
  if (statusCode >= 500 || statusCode === 429) {
    bot.recordRequestFailure(req.path, statusCode, error.message, error.details);
  }
  logger.error("Bot service request failed.", {
    route: req.path,
    statusCode,
    error: error.message
  });

  const payload = {
    success: false,
    message: error.message || "Bot service request failed."
  };

  if (error instanceof HttpError && error.details) {
    payload.error = error.message;
    Object.assign(payload, error.details);
  }

  res.status(statusCode).json(payload);
});

app.listen(config.port, async () => {
  logger.info("Steam bot HTTP service listening.", { port: config.port });
  await bot.start();
});
