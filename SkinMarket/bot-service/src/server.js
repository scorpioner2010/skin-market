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

app.use((error, _req, res, _next) => {
  const statusCode = error instanceof HttpError ? error.statusCode : 500;
  logger.error("Bot service request failed.", {
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
