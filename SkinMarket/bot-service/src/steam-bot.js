const fs = require("fs");
const path = require("path");
const SteamUser = require("steam-user");
const SteamCommunity = require("steamcommunity");
const TradeOfferManager = require("steam-tradeoffer-manager");
const SteamTotp = require("steam-totp");

function createDeferred() {
  let resolve;
  let reject;
  const promise = new Promise((res, rej) => {
    resolve = res;
    reject = rej;
  });

  return { promise, resolve, reject };
}

class HttpError extends Error {
  constructor(statusCode, message) {
    super(message);
    this.statusCode = statusCode;
  }
}

class SteamBot {
  constructor(config, logger) {
    this.config = config;
    this.logger = logger;
    this.readyDeferred = createDeferred();
    this.ready = false;
    this.loggedOn = false;
    this.lastError = null;
    this.lastReadyAt = null;
    this.refreshTokenPath = path.join(this.config.dataDirectory, "refresh-token.txt");

    this.client = new SteamUser({
      autoRelogin: true
    });
    this.community = new SteamCommunity();
    this.manager = new TradeOfferManager({
      steam: this.client,
      community: this.community,
      language: "en",
      useAccessToken: true,
      pollInterval: this.config.polling.pollInterval,
      minimumPollInterval: this.config.polling.minimumPollInterval,
      pollFullUpdateInterval: this.config.polling.pollFullUpdateInterval,
      dataDirectory: this.config.dataDirectory,
      savePollData: true
    });

    this.registerEventHandlers();
  }

  async getUserInventory(payload) {
    await this.ensureReady();
    this.validatePayload(payload, [
      "steamId",
      "appId",
      "contextId"
    ]);

    const steamId = String(payload.steamId).trim();
    const appId = Number(payload.appId);
    const contextId = String(payload.contextId).trim();
    if (!steamId) {
      throw new HttpError(400, "steamId is required.");
    }

    if (!Number.isFinite(appId) || appId <= 0) {
      throw new HttpError(400, "appId is invalid.");
    }

    if (!contextId) {
      throw new HttpError(400, "contextId is required.");
    }

    this.logger.info("Loading user inventory through Steam bot session.", {
      steamId,
      appId,
      contextId
    });

    try {
      const { items, totalInventoryCount } = await new Promise((resolve, reject) => {
        this.community.getUserInventoryContents(
          steamId,
          appId,
          contextId,
          false,
          "english",
          (error, inventory, _currency, totalCount) => {
            if (error) {
              reject(error);
              return;
            }

            resolve({
              items: Array.isArray(inventory) ? inventory : [],
              totalInventoryCount: Number.isFinite(totalCount) ? totalCount : null
            });
          }
        );
      });

      return {
        success: true,
        itemCount: items.length,
        totalInventoryCount: totalInventoryCount ?? items.length,
        items: items
          .map((item) => ({
            assetId: String(item.assetid || item.id || ""),
            classId: String(item.classid || ""),
            instanceId: String(item.instanceid || "0"),
            name: item.name || "Unknown Item",
            marketHashName: item.market_hash_name || null,
            marketName: item.market_name || null,
            iconUrl: this.getInventoryItemIconUrl(item),
            tradable: item.tradable === undefined ? null : Boolean(item.tradable),
            marketable: item.marketable === undefined ? null : Boolean(item.marketable)
          }))
          .filter((item) => item.assetId)
      };
    } catch (error) {
      throw this.mapInventoryError(error);
    }
  }

  async start() {
    fs.mkdirSync(this.config.dataDirectory, { recursive: true });

    this.logger.info("Steam bot service startup.", {
      enabled: this.config.bot.enabled,
      steamIdConfigured: Boolean(this.config.bot.steamId),
      usernameConfigured: Boolean(this.config.bot.username),
      identitySecretConfigured: Boolean(this.config.bot.identitySecret),
      sharedSecretConfigured: Boolean(this.config.bot.sharedSecret),
      steamApiKeyConfigured: Boolean(this.config.steamApiKey),
      pollInterval: this.config.polling.pollInterval,
      minimumPollInterval: this.config.polling.minimumPollInterval,
      pollFullUpdateInterval: this.config.polling.pollFullUpdateInterval
    });

    if (!this.config.bot.enabled) {
      this.lastError = "Steam bot is disabled by configuration.";
      this.logger.warn(this.lastError);
      return;
    }

    if (!this.config.bot.username || !this.config.bot.password || !this.config.bot.steamId) {
      this.lastError = "Steam bot username, password, or Steam ID is missing.";
      this.logger.error(this.lastError);
      return;
    }

    this.logOn();
  }

  getHealth() {
    return {
      enabled: this.config.bot.enabled,
      ready: this.ready,
      loggedOn: this.loggedOn,
      lastReadyAt: this.lastReadyAt,
      botSteamId: this.config.bot.steamId || null,
      usernameConfigured: Boolean(this.config.bot.username),
      identitySecretConfigured: Boolean(this.config.bot.identitySecret),
      sharedSecretConfigured: Boolean(this.config.bot.sharedSecret),
      steamApiKeyConfigured: Boolean(this.config.steamApiKey),
      lastError: this.lastError
    };
  }

  async createIntakeTrade(payload) {
    await this.ensureReady();
    this.validatePayload(payload, [
      "tradeOperationId",
      "sellerSteamId",
      "sellerTradeUrl",
      "appId",
      "contextId",
      "assetId",
      "itemName"
    ]);

    if (!this.tradeUrlBelongsToSteamId(payload.sellerTradeUrl, payload.sellerSteamId)) {
      throw new HttpError(400, "Seller Trade URL does not match the seller SteamID.");
    }

    const offer = this.manager.createOffer(payload.sellerTradeUrl);
    offer.setMessage(`SkinMarket intake: ${payload.itemName}`);
    offer.addTheirItem({
      appid: Number(payload.appId),
      contextid: String(payload.contextId),
      assetid: String(payload.assetId)
    });

    this.logger.info("Creating intake trade offer.", {
      tradeOperationId: payload.tradeOperationId,
      sellerSteamId: payload.sellerSteamId,
      assetId: payload.assetId,
      appId: payload.appId,
      contextId: payload.contextId
    });

    return await this.sendOffer(offer, {
      flow: "intake",
      entityId: payload.tradeOperationId,
      activeStatus: "TradeCreated",
      pendingStatus: "AwaitingBotConfirmation",
      itemName: payload.itemName
    });
  }

  async createDeliveryTrade(payload) {
    await this.ensureReady();
    this.validatePayload(payload, [
      "marketItemId",
      "buyerSteamId",
      "buyerTradeUrl",
      "appId",
      "contextId",
      "assetId",
      "itemName"
    ]);

    if (!this.tradeUrlBelongsToSteamId(payload.buyerTradeUrl, payload.buyerSteamId)) {
      throw new HttpError(400, "Buyer Trade URL does not match the buyer SteamID.");
    }

    const offer = this.manager.createOffer(payload.buyerTradeUrl);
    offer.setMessage(`SkinMarket delivery: ${payload.itemName}`);
    offer.addMyItem({
      appid: Number(payload.appId),
      contextid: String(payload.contextId),
      assetid: String(payload.assetId)
    });

    this.logger.info("Creating delivery trade offer.", {
      marketItemId: payload.marketItemId,
      buyerSteamId: payload.buyerSteamId,
      assetId: payload.assetId,
      appId: payload.appId,
      contextId: payload.contextId
    });

    return await this.sendOffer(offer, {
      flow: "delivery",
      entityId: payload.marketItemId,
      activeStatus: "DeliveryTradeCreated",
      pendingStatus: "AwaitingBotConfirmation",
      itemName: payload.itemName
    });
  }

  async getOfferStatuses(offers) {
    await this.ensureReady();
    if (!Array.isArray(offers)) {
      throw new HttpError(400, "Offer list is required.");
    }

    const results = [];
    for (const request of offers) {
      if (!request || !request.offerId || !request.flow) {
        results.push({
          offerId: request?.offerId || "",
          flow: request?.flow || "",
          exists: false,
          state: "Failed",
          rawState: "InvalidRequest",
          isTerminal: true,
          isSuccess: false,
          message: "Offer status request is invalid."
        });
        continue;
      }

      try {
        const offer = await this.getOfferById(request.offerId);
        results.push(await this.mapOfferStatus(request.flow, offer));
      } catch (error) {
        this.logger.warn("Offer status lookup failed.", {
          offerId: request.offerId,
          flow: request.flow,
          error: error.message
        });

        results.push({
          offerId: String(request.offerId),
          flow: String(request.flow),
          exists: false,
          state: "OfferNotFound",
          rawState: "OfferNotFound",
          isTerminal: true,
          isSuccess: false,
          message: error.message
        });
      }
    }

    return results;
  }

  logOn() {
    const refreshToken = this.tryReadRefreshToken();
    const loginDetails = refreshToken
      ? { refreshToken }
      : {
          accountName: this.config.bot.username,
          password: this.config.bot.password,
          twoFactorCode: this.config.bot.sharedSecret
            ? SteamTotp.generateAuthCode(this.config.bot.sharedSecret)
            : undefined
        };

    this.logger.info("Steam bot login starting.", {
      mode: refreshToken ? "refresh_token" : "password"
    });
    this.client.logOn(loginDetails);
  }

  registerEventHandlers() {
    this.client.on("loggedOn", () => {
      this.loggedOn = true;
      this.lastError = null;
      this.client.setPersona(SteamUser.EPersonaState.Online);
      this.logger.info("Steam bot logged on successfully.", {
        steamId: this.client.steamID?.getSteamID64?.() || null
      });
    });

    this.client.on("steamGuard", (_domain, callback, lastCodeWrong) => {
      if (!this.config.bot.sharedSecret) {
        this.lastError = "Steam Guard challenge received but STEAM_BOT_SHARED_SECRET is missing.";
        this.logger.error(this.lastError, { lastCodeWrong });
        return;
      }

      const code = SteamTotp.generateAuthCode(this.config.bot.sharedSecret);
      this.logger.info("Generated Steam Guard code from shared secret.", { lastCodeWrong });
      callback(code);
    });

    this.client.on("refreshToken", (token) => {
      fs.writeFileSync(this.refreshTokenPath, token, "utf8");
      this.logger.info("Steam refresh token updated on disk.");
    });

    this.client.on("webSession", (_sessionId, cookies) => {
      this.community.setCookies(cookies);
      this.manager.setCookies(cookies, (error) => {
        if (error) {
          this.ready = false;
          this.lastError = `Trade manager session initialization failed: ${error.message}`;
          this.logger.error(this.lastError);
          this.resetReadyDeferred();
          return;
        }

        this.ready = true;
        this.lastError = null;
        this.lastReadyAt = new Date().toISOString();
        this.logger.info("Steam bot web session initialized.");
        this.readyDeferred.resolve();
      });
    });

    this.client.on("error", (error) => {
      this.ready = false;
      this.loggedOn = false;
      this.lastError = error.message;
      this.logger.error("Steam bot client error.", {
        error: error.message,
        eresult: error.eresult || null
      });
      this.resetReadyDeferred();
    });

    this.client.on("disconnected", (eresult, message) => {
      this.ready = false;
      this.loggedOn = false;
      this.lastError = message || "Steam client disconnected.";
      this.logger.warn("Steam bot disconnected.", {
        eresult,
        message
      });
      this.resetReadyDeferred();
    });

    this.manager.on("sessionExpired", () => {
      this.ready = false;
      this.lastError = "Trade manager session expired. Refreshing web session.";
      this.logger.warn(this.lastError);
      this.resetReadyDeferred();
      this.client.webLogOn();
    });

    this.manager.on("pollFailure", (error) => {
      this.logger.warn("Trade manager poll failure.", {
        error: error.message
      });
    });

    this.manager.on("sentOfferChanged", (offer, oldState) => {
      this.logger.info("Sent offer state changed.", {
        offerId: offer.id,
        oldState: this.getOfferStateName(oldState),
        newState: this.getOfferStateName(offer.state)
      });
    });

    this.manager.on("realTimeTradeConfirmationRequired", (offer) => {
      this.logger.warn("Real-time trade confirmation required.", {
        offerId: offer.id
      });
    });
  }

  async sendOffer(offer, context) {
    const sendResult = await new Promise((resolve, reject) => {
      offer.send((error, status) => {
        if (error) {
          reject(error);
          return;
        }

        resolve({
          offerId: String(offer.id),
          status: String(status)
        });
      });
    });

    this.logger.info("Trade offer send result.", {
      flow: context.flow,
      entityId: context.entityId,
      offerId: sendResult.offerId,
      sendStatus: sendResult.status
    });

    if (sendResult.status === "pending") {
      if (!this.config.bot.identitySecret) {
        this.logger.warn("Trade offer requires manual confirmation because identity secret is missing.", {
          offerId: sendResult.offerId,
          flow: context.flow
        });

        return {
          success: true,
          tradeOfferId: sendResult.offerId,
          newStatus: context.pendingStatus,
          message: "Trade offer was created and is awaiting manual mobile confirmation on the bot account."
        };
      }

      try {
        await this.acceptConfirmation(sendResult.offerId);
        return {
          success: true,
          tradeOfferId: sendResult.offerId,
          newStatus: context.pendingStatus,
          message: "Trade offer was created and mobile confirmation was accepted. Waiting for Steam to activate the offer."
        };
      } catch (error) {
        this.logger.error("Trade offer confirmation failed.", {
          offerId: sendResult.offerId,
          flow: context.flow,
          error: error.message
        });

        return {
          success: true,
          tradeOfferId: sendResult.offerId,
          newStatus: context.pendingStatus,
          message: `Trade offer was created but automatic mobile confirmation failed: ${error.message}`
        };
      }
    }

    return {
      success: true,
      tradeOfferId: sendResult.offerId,
      newStatus: context.activeStatus,
      message: "Trade offer was created successfully."
    };
  }

  async acceptConfirmation(offerId) {
    this.logger.info("Attempting mobile confirmation for trade offer.", { offerId });
    await new Promise((resolve, reject) => {
      this.community.acceptConfirmationForObject(this.config.bot.identitySecret, offerId, (error) => {
        if (error) {
          reject(error);
          return;
        }

        resolve();
      });
    });
    this.logger.info("Mobile confirmation accepted.", { offerId });
  }

  async mapOfferStatus(flow, offer) {
    const rawState = this.getOfferStateName(offer.state);
    const base = {
      offerId: String(offer.id),
      flow,
      exists: true,
      rawState,
      isTerminal: false,
      isSuccess: false,
      message: null
    };

    switch (offer.state) {
      case TradeOfferManager.ETradeOfferState.Active:
        return {
          ...base,
          state: "Active",
          message: flow === "intake"
            ? "Trade offer is active and awaiting seller acceptance."
            : "Trade offer is active and awaiting buyer acceptance."
        };
      case TradeOfferManager.ETradeOfferState.CreatedNeedsConfirmation:
        return {
          ...base,
          state: "CreatedNeedsConfirmation",
          message: "Trade offer is waiting for bot mobile confirmation."
        };
      case TradeOfferManager.ETradeOfferState.Accepted:
        if (flow === "intake") {
          const exchange = await this.tryGetExchangeDetails(offer);
          if (!exchange || !Array.isArray(exchange.receivedItems) || exchange.receivedItems.length === 0) {
            return {
              ...base,
              state: "AcceptedPendingReceipt",
              message: "Trade offer was accepted but exchange details are not available yet."
            };
          }

          const receivedItem = exchange.receivedItems[0];
          const botAssetId = receivedItem.new_assetid || receivedItem.assetid;
          const botContextId = receivedItem.new_contextid || receivedItem.contextid;
          if (!botAssetId) {
            return {
              ...base,
              state: "AcceptedPendingReceipt",
              message: "Trade offer was accepted but the new bot inventory asset id is still missing."
            };
          }

          return {
            ...base,
            state: "Accepted",
            isTerminal: true,
            isSuccess: true,
            message: "Trade offer completed successfully and the item is now in the bot inventory.",
            receivedItem: {
              assetId: String(botAssetId),
              classId: String(receivedItem.classid || ""),
              instanceId: String(receivedItem.instanceid || ""),
              appId: Number(receivedItem.appid || 0),
              contextId: String(botContextId || "")
            }
          };
        }

        return {
          ...base,
          state: "Accepted",
          isTerminal: true,
          isSuccess: true,
          message: "Delivery trade offer completed successfully."
        };
      case TradeOfferManager.ETradeOfferState.InEscrow:
        return {
          ...base,
          state: "InEscrow",
          message: "Trade offer is in escrow and not completed yet."
        };
      case TradeOfferManager.ETradeOfferState.Declined:
        return {
          ...base,
          state: "Declined",
          isTerminal: true,
          message: "Trade offer was declined."
        };
      case TradeOfferManager.ETradeOfferState.Canceled:
        return {
          ...base,
          state: "Canceled",
          isTerminal: true,
          message: "Trade offer was canceled."
        };
      case TradeOfferManager.ETradeOfferState.Expired:
        return {
          ...base,
          state: "Expired",
          isTerminal: true,
          message: "Trade offer expired."
        };
      case TradeOfferManager.ETradeOfferState.Countered:
        return {
          ...base,
          state: "Countered",
          isTerminal: true,
          message: "Trade offer was countered."
        };
      case TradeOfferManager.ETradeOfferState.InvalidItems:
        return {
          ...base,
          state: "InvalidItems",
          isTerminal: true,
          message: "Trade offer contains invalid items."
        };
      case TradeOfferManager.ETradeOfferState.CanceledBySecondFactor:
        return {
          ...base,
          state: "CanceledBySecondFactor",
          isTerminal: true,
          message: "Trade offer was canceled by Steam Guard or mobile confirmation rules."
        };
      case TradeOfferManager.ETradeOfferState.Invalid:
      default:
        return {
          ...base,
          state: rawState === "Unknown" ? "Failed" : rawState,
          isTerminal: true,
          message: `Trade offer is in terminal state ${rawState}.`
        };
    }
  }

  async tryGetExchangeDetails(offer) {
    if (!offer.tradeID) {
      return null;
    }

    return await new Promise((resolve) => {
      offer.getExchangeDetails(true, (error, status, tradeInitTime, receivedItems, sentItems) => {
        if (error) {
          this.logger.warn("Exchange details lookup failed.", {
            offerId: offer.id,
            error: error.message
          });
          resolve(null);
          return;
        }

        resolve({
          status,
          tradeInitTime,
          receivedItems,
          sentItems
        });
      });
    });
  }

  async ensureReady(timeoutMs = 60000) {
    if (!this.config.bot.enabled) {
      throw new HttpError(503, "Steam bot is disabled.");
    }

    if (this.ready) {
      return;
    }

    const timeout = new Promise((_, reject) => {
      setTimeout(() => reject(new HttpError(503, "Steam bot is not ready yet.")), timeoutMs);
    });

    await Promise.race([this.readyDeferred.promise, timeout]);
  }

  getOfferById(offerId) {
    return new Promise((resolve, reject) => {
      this.manager.getOffer(String(offerId), (error, offer) => {
        if (error || !offer) {
          reject(error || new Error("Offer not found."));
          return;
        }

        resolve(offer);
      });
    });
  }

  getInventoryItemIconUrl(item) {
    const iconPath = item?.icon_url_large || item?.icon_url;
    if (!iconPath) {
      return null;
    }

    return `https://community.akamai.steamstatic.com/economy/image/${iconPath}`;
  }

  getOfferStateName(state) {
    const entries = Object.entries(TradeOfferManager.ETradeOfferState);
    const matched = entries.find(([, value]) => value === state);
    return matched ? matched[0] : "Unknown";
  }

  validatePayload(payload, requiredFields) {
    if (!payload || typeof payload !== "object") {
      throw new HttpError(400, "Request payload is required.");
    }

    for (const field of requiredFields) {
      if (payload[field] === undefined || payload[field] === null || payload[field] === "") {
        throw new HttpError(400, `${field} is required.`);
      }
    }
  }

  tradeUrlBelongsToSteamId(tradeUrl, steamId64) {
    try {
      const url = new URL(tradeUrl);
      const partner = url.searchParams.get("partner");
      if (!partner) {
        return false;
      }

      const accountId = BigInt(String(steamId64)) - 76561197960265728n;
      return accountId === BigInt(partner);
    } catch {
      return false;
    }
  }

  tryReadRefreshToken() {
    try {
      if (!fs.existsSync(this.refreshTokenPath)) {
        return null;
      }

      const value = fs.readFileSync(this.refreshTokenPath, "utf8").trim();
      return value || null;
    } catch (error) {
      this.logger.warn("Failed to read refresh token from disk.", {
        error: error.message
      });
      return null;
    }
  }

  resetReadyDeferred() {
    this.readyDeferred = createDeferred();
  }

  mapInventoryError(error) {
    if (error instanceof HttpError) {
      return error;
    }

    const message = error?.message || "Steam inventory request via bot failed.";
    if (message.includes("HTTP error 429")) {
      return new HttpError(429, "Steam inventory request via bot was rate limited.");
    }

    if (message.includes("private")) {
      return new HttpError(403, "Steam inventory is private or unavailable.");
    }

    if (message.includes("Malformed response")) {
      return new HttpError(502, "Steam inventory returned an invalid payload through the bot session.");
    }

    return new HttpError(502, `Steam inventory request via bot failed: ${message}`);
  }
}

module.exports = {
  SteamBot,
  HttpError
};
