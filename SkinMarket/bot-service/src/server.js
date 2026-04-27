const express = require("express");
const config = require("./config");
const logger = require("./logger");
const { SteamBot, HttpError } = require("./steam-bot");

const app = express();
app.use(express.json());

const bot = new SteamBot(config, logger);

app.get("/healthz", (_req, res) => {
  res.status(200).json({
    status: "ok",
    bot: bot.getHealth()
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
    bot.recordRequestFailure(req.path, statusCode, error.message);
  }
  logger.error("Bot service request failed.", {
    route: req.path,
    statusCode,
    error: error.message
  });

  res.status(statusCode).json({
    success: false,
    message: error.message || "Bot service request failed."
  });
});

app.listen(config.port, async () => {
  logger.info("Steam bot HTTP service listening.", { port: config.port });
  await bot.start();
});
