using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Bybit.Net;
using Bybit.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TradingBotEngine.Data;
using TradingBotEngine.Data.Models;

namespace TradingBotEngine.Services
{
    public class BrokerService
    {
        private readonly ApplicationDbContext _context;
        private readonly ICredentialProtector _protector;
        private readonly ExchangeMarketDataService _marketDataService;

        public BrokerService(
            ApplicationDbContext context,
            ICredentialProtector protector,
            ExchangeMarketDataService? marketDataService = null)
        {
            _context = context;
            _protector = protector;
            _marketDataService = marketDataService
                ?? new ExchangeMarketDataService(
                    new MemoryCache(new MemoryCacheOptions()));
        }

        public async Task<bool> ConnectAsync(
            int userId,
            BrokerConnection connection)
        {
            var brokerName = connection.BrokerName?.Trim();
            var apiKey = connection.ApiKey?.Trim();
            var apiSecret = connection.ApiSecret?.Trim();

            if (string.IsNullOrWhiteSpace(brokerName) ||
                (
                    !brokerName.Equals("Binance", StringComparison.OrdinalIgnoreCase) &&
                    !brokerName.Equals("Bybit", StringComparison.OrdinalIgnoreCase)
                ) ||
                string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(apiSecret))
            {
                return false;
            }

            brokerName = brokerName.Equals("Binance", StringComparison.OrdinalIgnoreCase)
                ? "Binance"
                : "Bybit";

            var persisted = await _context.BrokerConnections
                .FirstOrDefaultAsync(b =>
                    b.UserId == userId &&
                    b.BrokerName == brokerName);

            if (persisted == null)
            {
                persisted = new BrokerConnection
                {
                    UserId = userId,
                    BrokerName = brokerName,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                _context.BrokerConnections.Add(persisted);
            }

            persisted.ApiKey = _protector.Protect(apiKey);
            persisted.ApiSecret = _protector.Protect(apiSecret);
            persisted.IsActive = true;
            persisted.IsConnected = false;
            persisted.IsTestnet = true;
            persisted.IsLiveTradingEnabled = false;
            persisted.LiveTradingOptInAt = null;
            persisted.LastDisconnectedAt = DateTime.UtcNow;
            persisted.UpdatedAt = DateTime.UtcNow;

            try
            {
                var client = CreateClient(persisted);

                var connected =
                    await VerifyConnectionWithRetryAsync(client, persisted);

                persisted.IsConnected = connected;
                persisted.IsActive = connected || persisted.IsActive;
                persisted.LastConnectedAt =
                    connected
                        ? DateTime.UtcNow
                        : persisted.LastConnectedAt;

                persisted.UpdatedAt = DateTime.UtcNow;

                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = connected
                        ? "BrokerConnected"
                        : "BrokerConnectionFailed",

                    Details =
                        $"Broker {persisted.BrokerName} connection attempt " +
                        $"completed in {(persisted.IsTestnet ? "testnet" : "live")} mode.",

                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return connected;
            }
            catch (Exception connectException)
            {
                Console.Error.WriteLine(
                    $"[BrokerVerify] {persisted.BrokerName} connect threw: {connectException}");

                persisted.IsConnected = false;
                persisted.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                return false;
            }
        }

        public async Task<bool> DisconnectAsync(
            int userId,
            int? connectionId = null)
        {
            var connections = await _context.BrokerConnections
                .Where(b =>
                    b.UserId == userId &&
                    b.IsActive &&
                    (!connectionId.HasValue ||
                     b.Id == connectionId.Value))
                .ToListAsync();

            if (connections.Count == 0)
                return false;

            foreach (var connection in connections)
            {
                connection.IsConnected = false;
                connection.LastDisconnectedAt = DateTime.UtcNow;
                connection.UpdatedAt = DateTime.UtcNow;

                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "BrokerDisconnected",
                    Details = $"Broker {connection.BrokerName} connection {connection.Id} disconnected.",
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<bool> IsConnectedAsync(
            int userId,
            string? brokerName = null)
        {
            return await _context.BrokerConnections.AnyAsync(b =>
                b.UserId == userId &&
                b.IsActive &&
                b.IsConnected &&
                (brokerName == null ||
                 b.BrokerName == brokerName));
        }

        public async Task<BrokerConnectionStatus?>
            GetConnectionStatusAsync(int userId, int connectionId)
        {
            return await _context.BrokerConnections
                .AsNoTracking()
                .Where(b => b.UserId == userId && b.IsActive && b.Id == connectionId)
                .Select(b => new BrokerConnectionStatus
                {
                    Id = b.Id,
                    BrokerName = b.BrokerName,
                    IsConnected = b.IsConnected,
                    IsTestnet = b.IsTestnet,
                    IsLiveTradingEnabled = b.IsLiveTradingEnabled,
                    LastConnectedAt = b.LastConnectedAt,
                    LastDisconnectedAt = b.LastDisconnectedAt
                })
                .FirstOrDefaultAsync();
        }

        public async Task<IReadOnlyList<BrokerConnectionStatus>>
            GetConnectionsAsync(int userId)
        {
            return await _context.BrokerConnections
                .AsNoTracking()
                .Where(b =>
                    b.UserId == userId &&
                    b.IsActive)
                .Select(b => new BrokerConnectionStatus
                {
                    Id = b.Id,
                    BrokerName = b.BrokerName,
                    IsConnected = b.IsConnected,
                    IsTestnet = b.IsTestnet,
                    IsLiveTradingEnabled =
                        b.IsLiveTradingEnabled,
                    LastConnectedAt =
                        b.LastConnectedAt,
                    LastDisconnectedAt =
                        b.LastDisconnectedAt
                })
                .ToListAsync();
        }

        public async Task<bool> SetLiveTradingOptInAsync(
            int userId,
            int connectionId,
            bool enabled)
        {
            var connection =
                await _context.BrokerConnections
                    .FirstOrDefaultAsync(
                        b =>
                            b.Id == connectionId &&
                            b.UserId == userId &&
                            b.IsActive);

            if (connection == null)
                return false;

            if (enabled)
            {
                if (!connection.IsConnected)
                {
                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = userId,
                        Action = "LiveTradingEnableBlocked",

                        Details =
                            $"Live trading enable attempt blocked for " +
                            $"broker connection {connection.Id}: " +
                            $"connection is not currently connected/verified.",

                        CreatedAt = DateTime.UtcNow
                    });

                    await _context.SaveChangesAsync();

                    return false;
                }

                var originalIsTestnet = connection.IsTestnet;
                connection.IsTestnet = false;

                try
                {
                    var liveClient = CreateClient(connection);
                    var verifiedLive = await VerifyConnectionWithRetryAsync(liveClient, connection);

                    if (!verifiedLive)
                    {
                        connection.IsTestnet = originalIsTestnet;
                        _context.AuditLogs.Add(new AuditLog
                        {
                            UserId = userId,
                            Action = "LiveTradingEnableBlocked",
                            Details = $"Live trading enable attempt blocked for broker connection {connection.Id}: live credentials could not be verified.",
                            CreatedAt = DateTime.UtcNow
                        });
                        await _context.SaveChangesAsync();
                        return false;
                    }
                }
                catch
                {
                    connection.IsTestnet = originalIsTestnet;
                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = userId,
                        Action = "LiveTradingEnableBlocked",
                        Details = $"Live trading enable attempt blocked for broker connection {connection.Id}: live credential verification failed.",
                        CreatedAt = DateTime.UtcNow
                    });
                    await _context.SaveChangesAsync();
                    return false;
                }

                connection.IsLiveTradingEnabled = true;
                connection.LiveTradingOptInAt = DateTime.UtcNow;
            }
            else
            {
                connection.IsTestnet = true;
                connection.IsLiveTradingEnabled = false;
                connection.LiveTradingOptInAt = null;
            }

            connection.UpdatedAt = DateTime.UtcNow;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId = userId,

                Action = enabled
                    ? "LiveTradingEnabled"
                    : "LiveTradingDisabled",

                Details =
                    $"Broker connection {connection.Id} live-trading " +
                    $"state changed to {enabled}.",

                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            return true;
        }

        public sealed record SpotMarketPriceSnapshot(
            string BrokerName,
            bool IsTestnet,
            string Symbol,
            decimal LastPrice,
            decimal SuggestedBuyPrice);

        public async Task<SpotMarketPriceSnapshot?> GetSpotMarketPriceAsync(
            int userId,
            string symbol,
            int? connectionId = null)
        {
            symbol = symbol?.Trim().ToUpperInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(symbol))
                return null;

            var connection = await GetConnectionAsync(userId, connectionId);
            if (connection == null || !connection.IsActive || !connection.IsConnected)
                return null;

            try
            {
                var client = CreateClient(connection);
                decimal lastPrice;

                if (client is BinanceRestClient binance)
                {
                    var ticker = await binance.SpotApi.ExchangeData
                        .GetPriceAsync(symbol, CancellationToken.None);

                    if (!ticker.Success || ticker.Data == null || ticker.Data.Price <= 0m)
                        return null;

                    lastPrice = ticker.Data.Price;
                }
                else if (client is BybitRestClient bybit)
                {
                    var ticker = await bybit.V5Api.ExchangeData
                        .GetSpotTickersAsync(symbol);

                    var tickerData = ticker.Success
                        ? ticker.Data.List.FirstOrDefault()
                        : null;

                    if (tickerData == null || tickerData.LastPrice <= 0m)
                        return null;

                    lastPrice = tickerData.LastPrice;
                }
                else
                {
                    return null;
                }

                var suggestedBuyPrice = Math.Round(
                    lastPrice * 1.01m,
                    2,
                    MidpointRounding.AwayFromZero);

                return new SpotMarketPriceSnapshot(
                    connection.BrokerName,
                    connection.IsTestnet,
                    symbol,
                    lastPrice,
                    suggestedBuyPrice);
            }
            catch
            {
                return null;
            }
        }

        public virtual async Task<decimal> GetBalanceAsync(
            int userId,
            string currency = "USDT",
            bool useFutures = false,
            int? connectionId = null)
        {
            var connection = await GetConnectionAsync(userId, connectionId);

            if (connection == null)
                return 0;

            try
            {
                var client = CreateClient(connection);

                if (client is BinanceRestClient binance)
                {
                    if (useFutures)
                    {
                        var result =
                            await binance.UsdFuturesApi
                                .Account
                                .GetAccountInfoAsync();

                        if (result.Success)
                        {
                            return result.Data.Assets
                                .FirstOrDefault(a =>
                                    a.Asset == currency)
                                ?.WalletBalance ?? 0;
                        }
                    }
                    else
                    {
                        var result =
                            await binance.SpotApi
                                .Account
                                .GetAccountInfoAsync();

                        if (result.Success)
                        {
                            return result.Data.Balances
                                .FirstOrDefault(a =>
                                    a.Asset == currency)
                                ?.Available ?? 0;
                        }
                    }
                }

                if (client is BybitRestClient bybit)
                {
                    var result =
                        await bybit.V5Api.Account
                            .GetBalancesAsync(
                                Bybit.Net.Enums.AccountType.Unified,
                                currency,
                                CancellationToken.None);

                    if (result.Success)
                    {
                        var coin = result.Data.List?
                            .FirstOrDefault()?
                            .Assets?
                            .FirstOrDefault(a =>
                                a.Asset == currency);

                        if (coin != null)
                            return coin.WalletBalance; 
                    }
                }
            }
            catch
            {
            }

            return 0;
        }

        public async Task<BrokerOrderResult> PlaceOrderAsync(
            int userId,
            string symbol,
            string direction,
            string orderType = "Market",
            decimal? stopLoss = null,
            decimal? takeProfit = null,
            decimal? entryPrice = null,
            bool useFutures = false,
            string? idempotencyKey = null,
            int? connectionId = null,
            int? botId = null)
        {
            symbol = symbol?.Trim() ?? string.Empty;
            direction = direction?.Trim() ?? string.Empty;
            orderType = orderType?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(symbol))
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Invalid order parameters.",
                    $"Order validation failed for symbol '{symbol}'.");
            }

            if (!string.Equals(
                    direction,
                    "BUY",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    direction,
                    "SELL",
                    StringComparison.OrdinalIgnoreCase))
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Invalid order side.",
                    $"Order validation failed because side '{direction}' is invalid.");
            }

            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "An idempotency key is required for every order.",
                    $"Order request was rejected because no idempotency key was supplied.");
            }

            idempotencyKey = idempotencyKey.Trim();

            if (idempotencyKey.Length > 100)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Invalid idempotency key.",
                    $"Order request was rejected because the idempotency key exceeds the allowed length.");
            }

            if (!string.Equals(
                    orderType,
                    "Market",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    orderType,
                    "Limit",
                    StringComparison.OrdinalIgnoreCase))
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Invalid order type.",
                    $"Order type '{orderType}' is not supported.");
            }

            if (string.Equals(
                    orderType,
                    "Limit",
                    StringComparison.OrdinalIgnoreCase) &&
                (!entryPrice.HasValue ||
                 entryPrice.Value <= 0))
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "A valid entry price is required for limit orders.",
                    $"Limit order for '{symbol}' was rejected because the entry price is missing or invalid.");
            }

            var connection =
                await GetConnectionAsync(userId, connectionId);

            if (connection == null ||
                !connection.IsConnected)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderBlocked_NoBrokerConnection",
                    "No active broker connection.",
                    $"Order for '{symbol}' was blocked because no active broker connection exists.");
            }

            if (!connection.IsTestnet &&
                !connection.IsLiveTradingEnabled)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderBlocked_LiveTradingNotEnabled",
                    "Live trading is not explicitly enabled for this connection.",
                    $"Order for '{symbol}' was blocked because explicit live-trading opt-in is not enabled.");
            }

            if (!useFutures &&
                direction.Equals("SELL", StringComparison.OrdinalIgnoreCase) &&
                (stopLoss.HasValue || takeProfit.HasValue))
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Spot SELL orders cannot open short positions with protective SL/TP.",
                    $"Spot SELL for '{symbol}' was rejected because spot SELL closes/reduces an existing asset position. Protection is created for spot BUY holdings instead.");
            }

            if (connection.BrokerName.Equals("Bybit", StringComparison.OrdinalIgnoreCase) &&
                !useFutures &&
                (stopLoss.HasValue || takeProfit.HasValue) &&
                orderType.Equals("Market", StringComparison.OrdinalIgnoreCase))
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Protected Bybit Spot orders must use a limit entry.",
                    $"Bybit Spot order for '{symbol}' was rejected because the protected Spot path uses a limit entry so TP/SL can be attached safely.");
            }

            var existingTrade =
                await _context.Trades
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t =>
                        t.UserId == userId &&
                        t.IdempotencyKey == idempotencyKey);

            if (existingTrade != null)
            {
                if (!string.IsNullOrWhiteSpace(
                        existingTrade.OrderId))
                {
                    await AuditOrderEventAsync(
                        userId,
                        "OrderIdempotencyReplay",
                        $"Existing order {existingTrade.OrderId} was returned for idempotency key '{idempotencyKey}'.");

                    return BrokerOrderResult.SuccessResult(
                        existingTrade.OrderId,
                        existingTrade.EntryPrice);
                }

                return await AuditOrderFailureAsync(
                    userId,
                    "OrderBlocked_IdempotencyConflict",
                    "An order with this idempotency key is already being processed. Reconciliation is required before retrying.",
                    $"Duplicate order request blocked for idempotency key '{idempotencyKey}'.");
            }

            var riskSetting = await _context.RiskSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == userId);

            if (riskSetting == null)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderBlocked_RiskSettingsMissing",
                    "Risk settings are not configured for this user.",
                    $"Order for '{symbol}' was blocked because risk settings are not configured for user {userId}.");
            }

            if (riskSetting.IsEmergencyKillSwitch)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "RiskBlocked_EmergencyKillSwitch",
                    Details =
                        "Order blocked because the emergency kill switch is active." +
                        (riskSetting.EmergencyKillSwitchActivatedAt.HasValue
                            ? $" Activated at {riskSetting.EmergencyKillSwitchActivatedAt.Value:O}."
                            : string.Empty),
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return BrokerOrderResult.Fail(
                    "Trading is blocked because the emergency kill switch is active.");
            }

            if (riskSetting.RiskPerTrade <= 0m ||
                riskSetting.RiskPerTrade > 100m)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderBlocked_InvalidRiskConfiguration",
                    "Configured risk per trade is invalid.",
                    $"Order for '{symbol}' was blocked because RiskPerTrade ({riskSetting.RiskPerTrade:F2}%) is invalid.");
            }

            if (riskSetting.MaxOpenTrades <= 0)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "RiskBlocked_MaxOpenTrades",
                    Details =
                        "Order blocked because MaxOpenTrades is not configured " +
                        "to a positive value.",
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return BrokerOrderResult.Fail(
                    "Maximum open trades is not configured correctly.");
            }

            var openTradeCount = await _context.Trades
                .CountAsync(t =>
                    t.UserId == userId &&
                    (t.Status == "Pending" ||
                     t.Status == "Open" ||
                     t.Status == "New" ||
                     t.Status == "PartiallyFilled" ||
                     t.Status == "Filled" ||
                     t.Status == "PendingReconciliation"));

            if (openTradeCount >= riskSetting.MaxOpenTrades)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "RiskBlocked_MaxOpenTrades",
                    Details =
                        $"Order blocked because the maximum number of open " +
                        $"trades ({riskSetting.MaxOpenTrades}) has been reached. " +
                        $"Current active/unresolved trades: {openTradeCount}.",
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return BrokerOrderResult.Fail(
                    $"Maximum open trades limit reached ({riskSetting.MaxOpenTrades}).");
            }

            if (riskSetting.DailyLossLimit <= 0m ||
                riskSetting.DailyLossLimit > 100m)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "RiskBlocked_DailyLossLimit",
                    Details =
                        "Order blocked because DailyLossLimit is not configured " +
                        "as a valid percentage between 0 and 100.",
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return BrokerOrderResult.Fail(
                    "Daily loss limit is not configured correctly.");
            }

            var balance = await GetBalanceAsync(
                userId,
                "USDT",
                useFutures,
                connection.Id);

            if (balance <= 0m)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderBlocked_InsufficientBalance",
                    "Insufficient available balance.",
                    $"Order for '{symbol}' was blocked because available USDT balance was insufficient.");
            }

            var utcToday = DateTime.UtcNow.Date;
            var tomorrowUtc = utcToday.AddDays(1);

            var dailyRealizedLoss = await _context.Trades
                .Where(t =>
                    t.UserId == userId &&
                    t.ExitTime.HasValue &&
                    t.ExitTime.Value >= utcToday &&
                    t.ExitTime.Value < tomorrowUtc &&
                    t.ProfitLoss < 0m)
                .SumAsync(t => (decimal?)t.ProfitLoss) ?? 0m;

            dailyRealizedLoss = Math.Abs(dailyRealizedLoss);

            var dailyLossLimitAmount =
                balance * (riskSetting.DailyLossLimit / 100m);

            if (dailyRealizedLoss >= dailyLossLimitAmount)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "RiskBlocked_DailyLossLimit",
                    Details =
                        $"Order blocked because today's realized loss " +
                        $"({dailyRealizedLoss:F2} USDT) has reached or exceeded " +
                        $"the daily loss limit ({dailyLossLimitAmount:F2} USDT, " +
                        $"{riskSetting.DailyLossLimit:F2}%).",
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return BrokerOrderResult.Fail(
                    $"Daily loss limit reached ({dailyLossLimitAmount:F2} USDT).");
            }

            if (riskSetting.MaxDrawdown <= 0m ||
                riskSetting.MaxDrawdown > 100m)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "RiskBlocked_MaxDrawdown",
                    Details =
                        "Order blocked because MaxDrawdown is not configured " +
                        "as a valid percentage between 0 and 100.",
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return BrokerOrderResult.Fail(
                    "Maximum drawdown is not configured correctly.");
            }

            var currentEquity = balance;

            if (useFutures)
            {
                var unrealizedPnL = await _context.Positions
                    .Where(p =>
                        p.UserId == userId &&
                        p.Status == "Open")
                    .SumAsync(p => (decimal?)p.UnrealizedPnL) ?? 0m;

                currentEquity += unrealizedPnL;
            }

            if (currentEquity <= 0m)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderBlocked_EquityUnavailable",
                    "Current account equity is not available.",
                    $"Order for '{symbol}' was blocked because current account equity could not be established.");
            }

            var trackedRiskSetting = await _context.RiskSettings
                .FirstOrDefaultAsync(r => r.UserId == userId);

            if (trackedRiskSetting == null)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderBlocked_RiskSettingsMissing",
                    "Risk settings are not configured for this user.",
                    $"Order for '{symbol}' was blocked because risk settings are not configured for user {userId}.");
            }

            if (trackedRiskSetting.EquityHighWaterMark <= 0m)
            {
                trackedRiskSetting.EquityHighWaterMark = currentEquity;
                trackedRiskSetting.EquityHighWaterMarkUpdatedAt =
                    DateTime.UtcNow;

                await _context.SaveChangesAsync();
            }
            else if (currentEquity > trackedRiskSetting.EquityHighWaterMark)
            {
                trackedRiskSetting.EquityHighWaterMark = currentEquity;
                trackedRiskSetting.EquityHighWaterMarkUpdatedAt =
                    DateTime.UtcNow;

                await _context.SaveChangesAsync();
            }

            var equityHighWaterMark =
                trackedRiskSetting.EquityHighWaterMark;

            var drawdownPercentage =
                ((equityHighWaterMark - currentEquity) /
                 equityHighWaterMark) * 100m;

            if (drawdownPercentage >= trackedRiskSetting.MaxDrawdown)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "RiskBlocked_MaxDrawdown",
                    Details =
                        $"Order blocked because current equity " +
                        $"({currentEquity:F2} USDT) is down " +
                        $"{drawdownPercentage:F2}% from the equity high-water " +
                        $"mark ({equityHighWaterMark:F2} USDT), reaching the " +
                        $"configured maximum drawdown of " +
                        $"{trackedRiskSetting.MaxDrawdown:F2}%.",
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return BrokerOrderResult.Fail(
                    $"Maximum drawdown limit reached " +
                    $"({trackedRiskSetting.MaxDrawdown:F2}%).");
            }

            ExchangeOrderRules rules;

            if (string.Equals(
                    connection.BrokerName,
                    "Binance",
                    StringComparison.OrdinalIgnoreCase))
            {
                rules = await _marketDataService.GetBinanceRulesAsync(
                    symbol,
                    useFutures,
                    connection.IsTestnet);
            }
            else if (string.Equals(
                         connection.BrokerName,
                         "Bybit",
                         StringComparison.OrdinalIgnoreCase))
            {
                rules = await _marketDataService.GetBybitRulesAsync(
                    symbol,
                    useFutures,
                    connection.IsTestnet);
            }
            else
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Unsupported broker.",
                    $"Order for '{symbol}' was rejected because broker '{connection.BrokerName}' is unsupported.");
            }

            decimal sizingPrice;

            if (entryPrice.HasValue && entryPrice.Value > 0m)
            {
                sizingPrice = entryPrice.Value;
            }
            else
            {
                var pricingClient = CreateClient(connection);

                if (pricingClient is BinanceRestClient pricingBinance)
                {
                    if (useFutures)
                    {
                        var ticker = await pricingBinance.UsdFuturesApi
                            .ExchangeData
                            .GetTickerAsync(symbol, CancellationToken.None);

                        sizingPrice = ticker.Success && ticker.Data != null
                            ? ticker.Data.LastPrice
                            : 0m;
                    }
                    else
                    {
                        var ticker = await pricingBinance.SpotApi
                            .ExchangeData
                            .GetPriceAsync(symbol, CancellationToken.None);

                        sizingPrice = ticker.Success && ticker.Data != null
                            ? ticker.Data.Price
                            : 0m;
                    }
                }
                else
                {
                    sizingPrice = rules.ReferencePrice;
                }
            }

            if (sizingPrice <= 0m)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Unable to determine a valid pricing reference.",
                    $"Order for '{symbol}' was rejected because no valid pricing reference was available.");
            }

            var leverage = useFutures
                ? riskSetting.Leverage
                : 1m;

            if (leverage <= 0m)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Configured leverage is invalid.",
                    $"Order for '{symbol}' was rejected because configured leverage ({leverage:F2}) is invalid.");
            }

            if (useFutures && rules.MaxLeverage > 0m)
            {
                leverage = Math.Min(
                    leverage,
                    rules.MaxLeverage);
            }
            else if (!useFutures)
            {
                leverage = 1m;
            }

            decimal calculatedQuantity;

            if (stopLoss.HasValue && stopLoss.Value > 0m)
            {
                var isBuyOrder = direction.Equals("BUY", StringComparison.OrdinalIgnoreCase);

                if ((isBuyOrder && stopLoss.Value >= sizingPrice) ||
                    (!isBuyOrder && stopLoss.Value <= sizingPrice))
                {
                    return await AuditOrderFailureAsync(
                        userId,
                        "OrderValidationFailed",
                        "Stop-loss is on the wrong side of the entry price.",
                        $"Order for '{symbol}' was rejected: {direction.ToUpperInvariant()} stop-loss {stopLoss.Value} " +
                        $"is not on the loss side of reference price {sizingPrice}.");
                }

                try
                {
                    calculatedQuantity = OrderRiskSizer.CalculateQuantity(
                        balance: balance,
                        riskPerTradePercent: riskSetting.RiskPerTrade,
                        entryPrice: sizingPrice,
                        stopLoss: stopLoss.Value,
                        leverage: leverage,
                        minQuantity: Math.Max(rules.MinQuantity, rules.QuantityStep),
                        maxQuantity: rules.MaxQuantity > 0m ? rules.MaxQuantity : decimal.MaxValue,
                        quantityStep: rules.QuantityStep);
                }
                catch (InvalidOperationException sizingException)
                {
                    return await AuditOrderFailureAsync(
                        userId,
                        "OrderValidationFailed",
                        "Unable to size the order from the stop-loss risk.",
                        $"Order for '{symbol}' was rejected by the stop-loss risk sizer: {sizingException.Message}");
                }
            }
            else
            {
                var riskCapital =
                    balance * (riskSetting.RiskPerTrade / 100m);

                var targetNotional =
                    riskCapital * leverage;

                calculatedQuantity =
                    targetNotional / sizingPrice;
            }

            if (calculatedQuantity <= 0m)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Calculated order quantity is invalid.",
                    $"Order for '{symbol}' was rejected because the server-calculated quantity is invalid.");
            }

            if (rules.QuantityStep <= 0m)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Exchange quantity step is invalid.",
                    $"Order for '{symbol}' was rejected because the exchange quantity step is invalid.");
            }

            calculatedQuantity =
                Math.Floor(
                    calculatedQuantity / rules.QuantityStep)
                * rules.QuantityStep;

            if (calculatedQuantity <= 0m)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Calculated order quantity became zero after exchange rounding.",
                    $"Order for '{symbol}' was rejected because quantity became zero after exchange rounding.");
            }

            if (calculatedQuantity < rules.MinQuantity)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Calculated quantity is below the exchange minimum.",
                    $"Order for '{symbol}' was rejected because calculated quantity is below the exchange minimum.");
            }

            if (rules.MaxQuantity > 0m &&
                calculatedQuantity > rules.MaxQuantity)
            {
                calculatedQuantity =
                    Math.Floor(
                        rules.MaxQuantity / rules.QuantityStep)
                    * rules.QuantityStep;
            }

            if (calculatedQuantity < rules.MinQuantity)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Exchange quantity limits cannot satisfy the calculated order size.",
                    $"Order for '{symbol}' was rejected because exchange quantity limits cannot satisfy the calculated order size.");
            }

            var finalNotional =
                calculatedQuantity * sizingPrice;

            if (finalNotional <= 0m)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Final order value is invalid.",
                    $"Order for '{symbol}' was rejected because final order value is invalid.");
            }

            if (rules.MinNotional > 0m &&
                finalNotional < rules.MinNotional)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderValidationFailed",
                    "Final order value is below the exchange minimum.",
                    $"Order for '{symbol}' was rejected because final order value is below the exchange minimum.");
            }

            if (!useFutures &&
                finalNotional > balance)
            {
                return await AuditOrderFailureAsync(
                    userId,
                    "OrderBlocked_InsufficientBalance",
                    "Final order value exceeds available balance.",
                    $"Order for '{symbol}' was blocked because final order value exceeds available balance.");
            }

            var quantity = calculatedQuantity;

            if (!useFutures &&
                direction.Equals("SELL", StringComparison.OrdinalIgnoreCase))
            {
                var baseAsset = ExtractBaseAsset(symbol);
                var availableBase = await GetSpotAssetBalanceAsync(
                    baseAsset,
                    connection);

                if (availableBase <= 0m)
                {
                    return await AuditOrderFailureAsync(
                        userId,
                        "OrderBlocked_InsufficientAssetBalance",
                        "Insufficient available base-asset balance for spot SELL.",
                        $"Spot SELL for '{symbol}' was blocked because no sellable {baseAsset} balance was available.");
                }

                quantity = Math.Min(quantity, availableBase);
                quantity = Math.Floor(quantity / rules.QuantityStep) * rules.QuantityStep;

                if (quantity < rules.MinQuantity)
                {
                    return await AuditOrderFailureAsync(
                        userId,
                        "OrderValidationFailed",
                        "Available base-asset balance is below the exchange minimum sell quantity.",
                        $"Spot SELL for '{symbol}' was rejected because available {baseAsset} balance is below the exchange minimum quantity.");
                }
            }

            var pendingTrade = new Trade
            {
                UserId = userId,
                BotId = botId,
                Symbol = symbol,
                Direction = direction.ToUpperInvariant(),

                Quantity = quantity,

                EntryPrice =
                    entryPrice ?? 0m,

                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                ProtectionStatus = (stopLoss.HasValue || takeProfit.HasValue) ? "Pending" : "NotRequired",

                EntryTime = DateTime.UtcNow,

                Status = "Pending",

                BrokerName = connection.BrokerName,

                IdempotencyKey = idempotencyKey,

                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Trades.Add(pendingTrade);

            try
            {
                await _context.SaveChangesAsync();

                await AuditOrderEventAsync(
                    userId,
                    "OrderReserved",
                    $"Order reserved before exchange submission. Symbol: {symbol}, Side: {direction.ToUpperInvariant()}, Quantity: {quantity}, Broker: {connection.BrokerName}, IdempotencyKey: {idempotencyKey}.");
            }
            catch (DbUpdateException)
            {
                var concurrentTrade =
                    await _context.Trades
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t =>
                            t.UserId == userId &&
                            t.IdempotencyKey == idempotencyKey);

                if (concurrentTrade != null)
                {
                    if (!string.IsNullOrWhiteSpace(
                            concurrentTrade.OrderId))
                    {
                        await AuditOrderEventAsync(
                            userId,
                            "OrderIdempotencyReplay",
                            $"Existing order {concurrentTrade.OrderId} was returned after a concurrent reservation for idempotency key '{idempotencyKey}'.");

                        return BrokerOrderResult.SuccessResult(
                            concurrentTrade.OrderId,
                            concurrentTrade.EntryPrice);
                    }

                    _context.Entry(pendingTrade).State = EntityState.Detached;

                    return await AuditOrderFailureAsync(
                        userId,
                        "OrderBlocked_IdempotencyConflict",
                        "An order with this idempotency key is already being processed. Reconciliation is required before retrying.",
                        $"Concurrent duplicate order request blocked for idempotency key '{idempotencyKey}'.");
                }

                return await AuditOrderFailureAsync(
                    userId,
                    "OrderReservationFailed",
                    "The order could not be reserved safely. No exchange order was submitted.",
                    $"Order reservation failed for symbol '{symbol}'. No exchange order was submitted.");
            }

            var finalKillSwitchState = await _context.RiskSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == userId);

            if (finalKillSwitchState?.IsEmergencyKillSwitch == true)
            {
                pendingTrade.Status = "Blocked";
                pendingTrade.Reason =
                    "Emergency kill switch was active before exchange submission.";
                pendingTrade.UpdatedAt = DateTime.UtcNow;

                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "RiskBlocked_EmergencyKillSwitch",
                    Details =
                        "Pending order blocked immediately before exchange submission " +
                        "because the emergency kill switch is active.",
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return BrokerOrderResult.Fail(
                    "Trading is blocked because the emergency kill switch is active.");
            }

            var clientOrderId = BuildClientOrderId(userId, idempotencyKey);

            try
            {
                var client = CreateClient(connection);

                var normalizedSide =
                    direction.ToUpperInvariant();

                if (client is BinanceRestClient binance)
                {
                    var side =
                        normalizedSide == "BUY"
                            ? OrderSide.Buy
                            : OrderSide.Sell;

                    if (useFutures)
                    {
                        await EnsureFuturesAccountSettingsAsync(binance, symbol, riskSetting.Leverage);

                        var result =
                            await binance
                                .UsdFuturesApi
                                .Trading
                                .PlaceOrderAsync(
                                    symbol: symbol,
                                    side: side,

                                    type:
                                        string.Equals(
                                            orderType,
                                            "Market",
                                            StringComparison.OrdinalIgnoreCase)
                                            ? FuturesOrderType.Market
                                            : FuturesOrderType.Limit,

                                    quantity: quantity,
                                    price: entryPrice,
                                    newClientOrderId: clientOrderId,
                                    stopPrice: stopLoss);

                        if (!result.Success)
                        {
                            var exchangeError = result.Error?.ToString() ??
                                "Unknown exchange rejection.";

                            pendingTrade.Status = "Failed";
                            pendingTrade.UpdatedAt = DateTime.UtcNow;
                            pendingTrade.Reason =
                                $"Exchange rejected the order: {exchangeError}";

                            _context.AuditLogs.Add(new AuditLog
                            {
                                UserId = userId,
                                Action = "OrderRejectedByExchange",
                                Details =
                                    $"Exchange rejected order for {symbol} on {connection.BrokerName}. " +
                                    $"Exchange error: {exchangeError}",
                                CreatedAt = DateTime.UtcNow
                            });

                            await _context.SaveChangesAsync();

                            return BrokerOrderResult.Fail(
                                $"Exchange rejected the order: {exchangeError}");
                        }

                        var orderId =
                            result.Data?.Id.ToString();

                        if (string.IsNullOrWhiteSpace(orderId))
                        {
                            pendingTrade.Status =
                                "PendingReconciliation";

                            pendingTrade.UpdatedAt =
                                DateTime.UtcNow;

                            pendingTrade.Reason =
                                "Exchange accepted the order but " +
                                "did not return an order ID.";

                            _context.AuditLogs.Add(new AuditLog
                            {
                                UserId = userId,
                                Action = "OrderPendingReconciliation",
                                Details = $"Exchange accepted the order for {symbol} on {connection.BrokerName}, but no exchange order ID was returned. Reconciliation required.",
                                CreatedAt = DateTime.UtcNow
                            });

                            await _context.SaveChangesAsync();

                            return BrokerOrderResult.Fail(
                                "The exchange accepted the order but " +
                                "did not return an order ID. " +
                                "Reconciliation is required.");
                        }

                        pendingTrade.OrderId = orderId;

                        pendingTrade.EntryPrice =
                            result.Data?.Price ??
                            entryPrice ??
                            0m;

                        pendingTrade.Status = "Pending";
                        pendingTrade.UpdatedAt =
                            DateTime.UtcNow;

                        await PersistExchangeOrderAsync(
                            userId,
                            pendingTrade,
                            connection,
                            orderId,
                            normalizedSide,
                            orderType,
                            useFutures,
                            "Entry",
                            null);

                        if (stopLoss.HasValue || takeProfit.HasValue)
                        {
                            await TryEnsureProtectionAsync(connection, pendingTrade, orderId, normalizedSide);
                        }

                        _context.AuditLogs.Add(new AuditLog
                        {
                            UserId = userId,
                            Action = "OrderSubmitted",

                            Details =
                                $"Order {orderId} submitted to " +
                                $"{connection.BrokerName} in " +
                                $"{(connection.IsTestnet ? "testnet" : "live")} mode.",

                            CreatedAt = DateTime.UtcNow
                        });

                        await _context.SaveChangesAsync();

                        return BrokerOrderResult.SuccessResult(
                            orderId,
                            result.Data?.Price ?? entryPrice);
                    }

                    var isSpotMarketOrder =
                        string.Equals(
                            orderType,
                            "Market",
                            StringComparison.OrdinalIgnoreCase);

                    var spot = isSpotMarketOrder
                        ? await binance
                            .SpotApi
                            .Trading
                            .PlaceOrderAsync(
                                symbol: symbol,
                                side: side,
                                type: SpotOrderType.Market,
                                quantity: quantity,
                                newClientOrderId: clientOrderId)
                        : await binance
                            .SpotApi
                            .Trading
                            .PlaceOrderAsync(
                                symbol: symbol,
                                side: side,
                                type: SpotOrderType.Limit,
                                quantity: quantity,
                                price: entryPrice,
                                newClientOrderId: clientOrderId,
                                timeInForce: TimeInForce.GoodTillCanceled);

                    if (!spot.Success)
                    {
                        var exchangeError = spot.Error?.ToString() ??
                            "Unknown exchange rejection.";

                        pendingTrade.Status = "Failed";
                        pendingTrade.UpdatedAt = DateTime.UtcNow;
                        pendingTrade.Reason =
                            $"Exchange rejected the order: {exchangeError}";

                        _context.AuditLogs.Add(new AuditLog
                        {
                            UserId = userId,
                            Action = "OrderRejectedByExchange",
                            Details =
                                $"Exchange rejected order for {symbol} on {connection.BrokerName}. " +
                                $"Exchange error: {exchangeError}",
                            CreatedAt = DateTime.UtcNow
                        });

                        await _context.SaveChangesAsync();

                        return BrokerOrderResult.Fail(
                            $"Exchange rejected the order: {exchangeError}");
                    }

                    var spotOrderId =
                        spot.Data?.Id.ToString();

                    if (string.IsNullOrWhiteSpace(
                            spotOrderId))
                    {
                        pendingTrade.Status =
                            "PendingReconciliation";

                        pendingTrade.UpdatedAt =
                            DateTime.UtcNow;

                        pendingTrade.Reason =
                            "Exchange accepted the order but " +
                            "did not return an order ID.";

                        _context.AuditLogs.Add(new AuditLog
                        {
                            UserId = userId,
                            Action = "OrderPendingReconciliation",
                            Details = $"Exchange accepted the order for {symbol} on {connection.BrokerName}, but no exchange order ID was returned. Reconciliation required.",
                            CreatedAt = DateTime.UtcNow
                        });

                        await _context.SaveChangesAsync();

                        return BrokerOrderResult.Fail(
                            "The exchange accepted the order but " +
                            "did not return an order ID. " +
                            "Reconciliation is required.");
                    }

                    pendingTrade.OrderId =
                        spotOrderId;

                    pendingTrade.EntryPrice =
                        (spot.Data != null && spot.Data.Price > 0m
                            ? spot.Data.Price
                            : (decimal?)null) ??
                        entryPrice ??
                        0m;

                    pendingTrade.Status = "Pending";
                    pendingTrade.UpdatedAt =
                        DateTime.UtcNow;

                    await PersistExchangeOrderAsync(
                        userId,
                        pendingTrade,
                        connection,
                        spotOrderId,
                        normalizedSide,
                        orderType,
                        useFutures,
                        "Entry",
                        null);

                    if (stopLoss.HasValue || takeProfit.HasValue)
                    {
                        await TryEnsureProtectionAsync(connection, pendingTrade, spotOrderId, normalizedSide);
                    }

                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = userId,
                        Action = "OrderSubmitted",

                        Details =
                            $"Order {spotOrderId} submitted to " +
                            $"{connection.BrokerName} in " +
                            $"{(connection.IsTestnet ? "testnet" : "live")} mode.",

                        CreatedAt = DateTime.UtcNow
                    });

                    await _context.SaveChangesAsync();

                    return BrokerOrderResult.SuccessResult(
                        spotOrderId,
                        spot.Data?.Price ?? entryPrice);
                }

                if (client is BybitRestClient bybit)
                {
                    var side =
                        normalizedSide == "BUY"
                            ? Bybit.Net.Enums.OrderSide.Buy
                            : Bybit.Net.Enums.OrderSide.Sell;

                    var category =
                        useFutures
                            ? Bybit.Net.Enums.Category.Linear
                            : Bybit.Net.Enums.Category.Spot;

                    var useAttachedProtection = useFutures;

                    Bybit.Net.Enums.StopLossTakeProfitMode? tpslMode = null;
                    if (useAttachedProtection && (stopLoss.HasValue || takeProfit.HasValue))
                        tpslMode = Bybit.Net.Enums.StopLossTakeProfitMode.Full;

                    var result =
                        await bybit
                            .V5Api
                            .Trading
                            .PlaceOrderAsync(
                                category: category,
                                symbol: symbol,
                                side: side,

                                type:
                                    string.Equals(
                                        orderType,
                                        "Market",
                                        StringComparison.OrdinalIgnoreCase)
                                        ? Bybit.Net.Enums.NewOrderType.Market
                                        : Bybit.Net.Enums.NewOrderType.Limit,

                                quantity: quantity,
                                price: entryPrice,
                                clientOrderId: clientOrderId,
                                stopLoss: useAttachedProtection ? stopLoss : null,
                                takeProfit: useAttachedProtection ? takeProfit : null,
                                stopLossTakeProfitMode: useAttachedProtection ? tpslMode : null);

                    if (!result.Success)
                    {
                        var exchangeError = result.Error?.ToString() ??
                            "Unknown exchange rejection.";

                        pendingTrade.Status = "Failed";
                        pendingTrade.UpdatedAt = DateTime.UtcNow;
                        pendingTrade.Reason =
                            $"Exchange rejected the order: {exchangeError}";

                        _context.AuditLogs.Add(new AuditLog
                        {
                            UserId = userId,
                            Action = "OrderRejectedByExchange",
                            Details =
                                $"Exchange rejected order for {symbol} on {connection.BrokerName}. " +
                                $"Exchange error: {exchangeError}",
                            CreatedAt = DateTime.UtcNow
                        });

                        await _context.SaveChangesAsync();

                        return BrokerOrderResult.Fail(
                            $"Exchange rejected the order: {exchangeError}");
                    }

                    var bybitOrderId =
                        result.Data?.OrderId;

                    if (string.IsNullOrWhiteSpace(
                            bybitOrderId))
                    {
                        pendingTrade.Status =
                            "PendingReconciliation";

                        pendingTrade.UpdatedAt =
                            DateTime.UtcNow;

                        pendingTrade.Reason =
                            "Exchange accepted the order but " +
                            "did not return an order ID.";

                        _context.AuditLogs.Add(new AuditLog
                        {
                            UserId = userId,
                            Action = "OrderPendingReconciliation",
                            Details = $"Exchange accepted the order for {symbol} on {connection.BrokerName}, but no exchange order ID was returned. Reconciliation required.",
                            CreatedAt = DateTime.UtcNow
                        });

                        await _context.SaveChangesAsync();

                        return BrokerOrderResult.Fail(
                            "The exchange accepted the order but " +
                            "did not return an order ID. " +
                            "Reconciliation is required.");
                    }

                    pendingTrade.OrderId =
                        bybitOrderId;

                    pendingTrade.Status = "Pending";
                    pendingTrade.UpdatedAt =
                        DateTime.UtcNow;

                    if (useAttachedProtection && (stopLoss.HasValue || takeProfit.HasValue))
                        pendingTrade.ProtectionStatus = "ExchangeManaged";
                    else if (stopLoss.HasValue || takeProfit.HasValue)
                        pendingTrade.ProtectionStatus = "Pending";

                    await PersistExchangeOrderAsync(
                        userId,
                        pendingTrade,
                        connection,
                        bybitOrderId,
                        normalizedSide,
                        orderType,
                        useFutures,
                        "Entry",
                        null);

                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = userId,
                        Action = "OrderSubmitted",

                        Details =
                            $"Order {bybitOrderId} submitted to " +
                            $"{connection.BrokerName} in " +
                            $"{(connection.IsTestnet ? "testnet" : "live")} mode.",

                        CreatedAt = DateTime.UtcNow
                    });

                    await _context.SaveChangesAsync();

                    return BrokerOrderResult.SuccessResult(
                        bybitOrderId,
                        null);
                }

                pendingTrade.Status = "Failed";
                pendingTrade.UpdatedAt =
                    DateTime.UtcNow;
                pendingTrade.Reason =
                    "Unsupported broker.";

                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "OrderValidationFailed",
                    Details = $"Order for '{symbol}' was rejected because broker '{connection.BrokerName}' is unsupported.",
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return BrokerOrderResult.Fail(
                    "Unsupported broker.");
            }
            catch (Exception ex)
            {
                pendingTrade.Status =
                    "PendingReconciliation";

                pendingTrade.UpdatedAt =
                    DateTime.UtcNow;

                pendingTrade.Reason =
                    "Exchange submission result is unknown; " +
                    "reconciliation is required.";

                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = "OrderSubmissionUnknown",
                    Details =
                        $"Order submission result is unknown for {symbol} on {connection.BrokerName}. " +
                        $"Exception: {ex.Message} The trade was moved to PendingReconciliation.",
                    CreatedAt = DateTime.UtcNow
                });

                try
                {
                    await _context.SaveChangesAsync();
                }
                catch
                {
                }

                return BrokerOrderResult.Fail(
                    "The order submission result is unknown. " +
                    "Reconciliation is required before retrying.");
            }
        }

        public static string BuildClientOrderId(int userId, string idempotencyKey)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{userId}:{idempotencyKey}"));

            return "tb" + Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant();
        }

        private enum ExchangeLookupOutcome
        {
            Found,
            NotFound,
            Unknown
        }

        private sealed record ExchangeLookupResult(
            ExchangeLookupOutcome Outcome,
            string? ExchangeOrderId = null);

        public async Task<int> RecoverUnknownSubmissionsAsync(
            int userId,
            CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var graceCutoff = now.AddSeconds(-45);
            var releaseCutoff = now.AddMinutes(-5);

            var candidates = await _context.Trades
                .Where(t =>
                    t.UserId == userId &&
                    t.Status == "PendingReconciliation" &&
                    t.IdempotencyKey != null &&
                    (t.UpdatedAt ?? t.CreatedAt) < graceCutoff &&
                    !_context.ExchangeOrders.Any(o => o.TradeId == t.Id))
                .ToListAsync(cancellationToken);

            var resolved = 0;

            foreach (var trade in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var clientOrderId = BuildClientOrderId(userId, trade.IdempotencyKey!);

                bool? botUsesFutures = null;
                if (trade.BotId.HasValue)
                {
                    botUsesFutures = await _context.TradingBots
                        .Where(b => b.Id == trade.BotId.Value)
                        .Select(b => (bool?)b.UseFutures)
                        .FirstOrDefaultAsync(cancellationToken);
                }

                var markets = botUsesFutures.HasValue
                    ? new[] { botUsesFutures.Value }
                    : new[] { false, true };

                var connections = await _context.BrokerConnections
                    .Where(b =>
                        b.UserId == userId &&
                        b.IsActive &&
                        b.IsConnected &&
                        b.BrokerName == trade.BrokerName)
                    .OrderByDescending(b => b.LastConnectedAt)
                    .ThenByDescending(b => b.Id)
                    .ToListAsync(cancellationToken);

                var everyLookupSaidNotFound = connections.Count > 0;
                BrokerConnection? foundConnection = null;
                bool foundFutures = false;
                string? foundOrderId = null;

                foreach (var connection in connections)
                {
                    foreach (var futures in markets)
                    {
                        var lookup = await LookupOrderByClientIdAsync(
                            connection,
                            trade.Symbol,
                            futures,
                            clientOrderId,
                            cancellationToken);

                        if (lookup.Outcome == ExchangeLookupOutcome.Found)
                        {
                            foundConnection = connection;
                            foundFutures = futures;
                            foundOrderId = lookup.ExchangeOrderId;
                            break;
                        }

                        if (lookup.Outcome != ExchangeLookupOutcome.NotFound)
                            everyLookupSaidNotFound = false;
                    }

                    if (foundConnection != null)
                        break;
                }

                if (foundConnection != null && !string.IsNullOrWhiteSpace(foundOrderId))
                {
                    await PersistExchangeOrderAsync(
                        userId,
                        trade,
                        foundConnection,
                        foundOrderId,
                        trade.Direction,
                        "Recovered",
                        foundFutures,
                        "Entry",
                        null);

                    trade.OrderId = foundOrderId;
                    trade.Status = "Pending";
                    trade.Reason = "Recovered from the exchange by client order id after an unknown submission result.";
                    trade.UpdatedAt = DateTime.UtcNow;

                    if ((trade.StopLoss.HasValue || trade.TakeProfit.HasValue) &&
                        trade.BrokerName.Equals("Bybit", StringComparison.OrdinalIgnoreCase) &&
                        foundFutures)
                    {
                        trade.ProtectionStatus = "ExchangeManaged";
                    }

                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = userId,
                        Action = "OrderSubmissionRecovered",
                        Details =
                            $"Trade {trade.Id} ({trade.Symbol}) had an unknown submission result. " +
                            $"Found exchange order {foundOrderId} by client order id {clientOrderId}; " +
                            "handed to reconciliation.",
                        CreatedAt = DateTime.UtcNow
                    });

                    await _context.SaveChangesAsync(cancellationToken);
                    resolved++;
                    continue;
                }

                if (everyLookupSaidNotFound && (trade.UpdatedAt ?? trade.CreatedAt) < releaseCutoff)
                {
                    trade.Status = "Failed";
                    trade.Reason =
                        "Submission result was unknown and the exchange has no order for this trade's client order id; released automatically.";
                    trade.UpdatedAt = DateTime.UtcNow;

                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = userId,
                        Action = "OrderSubmissionReleased",
                        Details =
                            $"Trade {trade.Id} ({trade.Symbol}) was released: no exchange order exists " +
                            $"for client order id {clientOrderId}.",
                        CreatedAt = DateTime.UtcNow
                    });

                    await _context.SaveChangesAsync(cancellationToken);
                    resolved++;
                }
            }

            return resolved;
        }

        private async Task<ExchangeLookupResult> LookupOrderByClientIdAsync(
            BrokerConnection connection,
            string symbol,
            bool useFutures,
            string clientOrderId,
            CancellationToken cancellationToken)
        {
            const int binanceOrderDoesNotExist = -2013;

            try
            {
                var client = CreateClient(connection);

                if (client is BinanceRestClient binance)
                {
                    if (useFutures)
                    {
                        var futuresResult = await binance.UsdFuturesApi.Trading.GetOrderAsync(
                            symbol,
                            origClientOrderId: clientOrderId,
                            ct: cancellationToken);

                        if (futuresResult.Success && futuresResult.Data != null)
                        {
                            return new ExchangeLookupResult(
                                ExchangeLookupOutcome.Found,
                                futuresResult.Data.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }

                        return new ExchangeLookupResult(
                            futuresResult.Error?.Code == binanceOrderDoesNotExist
                                ? ExchangeLookupOutcome.NotFound
                                : ExchangeLookupOutcome.Unknown);
                    }

                    var spotResult = await binance.SpotApi.Trading.GetOrderAsync(
                        symbol,
                        origClientOrderId: clientOrderId,
                        ct: cancellationToken);

                    if (spotResult.Success && spotResult.Data != null)
                    {
                        return new ExchangeLookupResult(
                            ExchangeLookupOutcome.Found,
                            spotResult.Data.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    return new ExchangeLookupResult(
                        spotResult.Error?.Code == binanceOrderDoesNotExist
                            ? ExchangeLookupOutcome.NotFound
                            : ExchangeLookupOutcome.Unknown);
                }

                if (client is BybitRestClient bybit)
                {
                    var category = useFutures
                        ? Bybit.Net.Enums.Category.Linear
                        : Bybit.Net.Enums.Category.Spot;

                    var live = await bybit.V5Api.Trading.GetOrdersAsync(
                        category: category,
                        symbol: symbol,
                        clientOrderId: clientOrderId,
                        ct: cancellationToken);

                    var liveHit = live.Success ? live.Data?.List?.FirstOrDefault() : null;
                    if (liveHit != null && !string.IsNullOrWhiteSpace(liveHit.OrderId))
                        return new ExchangeLookupResult(ExchangeLookupOutcome.Found, liveHit.OrderId);

                    var history = await bybit.V5Api.Trading.GetOrderHistoryAsync(
                        category: category,
                        symbol: symbol,
                        clientOrderId: clientOrderId,
                        limit: useFutures ? 1 : (int?)null,
                        ct: cancellationToken);

                    var historyHit = history.Success ? history.Data?.List?.FirstOrDefault() : null;
                    if (historyHit != null && !string.IsNullOrWhiteSpace(historyHit.OrderId))
                        return new ExchangeLookupResult(ExchangeLookupOutcome.Found, historyHit.OrderId);

                    return new ExchangeLookupResult(
                        live.Success && history.Success
                            ? ExchangeLookupOutcome.NotFound
                            : ExchangeLookupOutcome.Unknown);
                }

                return new ExchangeLookupResult(ExchangeLookupOutcome.Unknown);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new ExchangeLookupResult(ExchangeLookupOutcome.Unknown);
            }
        }

        private static decimal RoundToStep(decimal value, decimal step) =>
            step <= 0m
                ? value
                : Math.Round(value / step, 0, MidpointRounding.AwayFromZero) * step;

        private static string LimitLength(string value, int maxLength) =>
            value.Length <= maxLength ? value : value.Substring(0, maxLength);

        private static async Task<decimal?> TryGetSpotTickSizeAsync(
            BinanceRestClient binance,
            string symbol)
        {
            try
            {
                var info = await binance.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol);
                if (!info.Success || info.Data == null)
                    return null;

                var market = info.Data.Symbols.FirstOrDefault(x =>
                    string.Equals(x.Name, symbol, StringComparison.OrdinalIgnoreCase));

                var tick = market?.PriceFilter?.TickSize;
                return tick.HasValue && tick.Value > 0m ? tick : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<decimal?> TryGetFuturesTickSizeAsync(
            BinanceRestClient binance,
            string symbol)
        {
            try
            {
                var info = await binance.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
                if (!info.Success || info.Data == null)
                    return null;

                var market = info.Data.Symbols.FirstOrDefault(x =>
                    string.Equals(x.Name, symbol, StringComparison.OrdinalIgnoreCase));

                var tick = market?.PriceFilter?.TickSize;
                return tick.HasValue && tick.Value > 0m ? tick : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task EnsureFuturesAccountSettingsAsync(
            BinanceRestClient binance,
            string symbol,
            decimal leverage)
        {
            try { await binance.UsdFuturesApi.Account.ModifyPositionModeAsync(false); } catch { }

            try { await binance.UsdFuturesApi.Account.ChangeMarginTypeAsync(symbol, FuturesMarginType.Isolated); } catch { }

            try
            {
                var target = (int)Math.Max(1m, Math.Round(leverage, MidpointRounding.AwayFromZero));
                await binance.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, target);
            }
            catch { }
        }

        public async Task<BrokerOrderResult> ClosePositionAsync(
            int userId,
            int tradeId,
            BrokerConnection connection,
            bool useFutures)
        {
            var trade = await _context.Trades
                .FirstOrDefaultAsync(t => t.Id == tradeId && t.UserId == userId);

            if (trade == null)
                return BrokerOrderResult.Fail("Trade not found.");

            if (!string.Equals(trade.Status, "Open", StringComparison.OrdinalIgnoreCase))
                return BrokerOrderResult.Fail($"Trade {tradeId} is {trade.Status}, not Open.");

            if (connection.BrokerName.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
                return await CloseBybitPositionAsync(userId, trade, connection, useFutures);

            if (!connection.BrokerName.Equals("Binance", StringComparison.OrdinalIgnoreCase))
                return BrokerOrderResult.Fail("Closing positions automatically is not supported for this broker.");

            var client = CreateClient(connection);
            if (client is not BinanceRestClient binance)
                return BrokerOrderResult.Fail("Unable to create the Binance client.");

            async Task<BrokerOrderResult> FailCloseAsync(string message)
            {
                trade.ProtectionStatus = "ProtectionPending";
                trade.Reason = LimitLength(message, 480);
                trade.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return BrokerOrderResult.Fail(message);
            }

            trade.ProtectionStatus = "Closing";
            trade.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            foreach (var protectionOrderId in new[] { trade.StopLossOrderId, trade.TakeProfitOrderId }
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct())
            {
                try
                {
                    await CancelOrderAsync(userId, trade.Symbol, protectionOrderId!, useFutures);
                }
                catch
                {
                }
            }

            trade.StopLossOrderId = null;
            trade.TakeProfitOrderId = null;

            var quantity = trade.Quantity;

            if (!useFutures)
            {
                var freeBase = await GetSpotAssetBalanceAsync(ExtractBaseAsset(trade.Symbol), connection);
                if (freeBase > 0m && freeBase < quantity)
                    quantity = freeBase;

                var stepSize = await TryGetSpotStepSizeAsync(binance, trade.Symbol);
                if (stepSize.HasValue)
                    quantity = RoundDownToStep(quantity, stepSize.Value);
            }

            if (quantity <= 0m)
                return await FailCloseAsync("Close skipped: no sellable quantity is available.");

            string exitOrderId;
            string exitSide;

            if (!useFutures)
            {
                var sell = await binance.SpotApi.Trading.PlaceOrderAsync(
                    symbol: trade.Symbol,
                    side: OrderSide.Sell,
                    type: SpotOrderType.Market,
                    quantity: quantity);

                if (!sell.Success || sell.Data == null)
                    return await FailCloseAsync($"Exit order was rejected: {sell.Error}");

                exitOrderId = sell.Data.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                exitSide = "SELL";
            }
            else
            {
                var isLong = !string.Equals(trade.Direction, "SELL", StringComparison.OrdinalIgnoreCase);

                var exit = await binance.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: trade.Symbol,
                    side: isLong ? OrderSide.Sell : OrderSide.Buy,
                    type: FuturesOrderType.Market,
                    quantity: quantity,
                    positionSide: PositionSide.Both,
                    reduceOnly: true);

                if (!exit.Success || exit.Data == null)
                    return await FailCloseAsync($"Exit order was rejected: {exit.Error}");

                exitOrderId = exit.Data.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                exitSide = isLong ? "SELL" : "BUY";
            }

            var entryOrder = await _context.ExchangeOrders
                .Where(o => o.TradeId == trade.Id && o.OrderRole == "Entry")
                .OrderBy(o => o.Id)
                .FirstOrDefaultAsync();

            await PersistExchangeOrderAsync(
                userId, trade, connection, exitOrderId, exitSide, "Market", useFutures, "Exit", entryOrder?.Id);

            trade.Reason = "Exit order submitted after an opposite signal.";
            trade.UpdatedAt = DateTime.UtcNow;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                Action = "PositionCloseSubmitted",
                Details = $"Exit market order {exitOrderId} submitted for trade {trade.Id} ({trade.Symbol}, qty={quantity}).",
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            return BrokerOrderResult.SuccessResult(exitOrderId, null);
        }

        private async Task<BrokerOrderResult> CloseBybitPositionAsync(
            int userId,
            Trade trade,
            BrokerConnection connection,
            bool useFutures)
        {
            var client = CreateClient(connection);
            if (client is not BybitRestClient bybit)
                return BrokerOrderResult.Fail("Unable to create the Bybit client.");

            var category = useFutures
                ? Bybit.Net.Enums.Category.Linear
                : Bybit.Net.Enums.Category.Spot;

            var isLong = !string.Equals(trade.Direction, "SELL", StringComparison.OrdinalIgnoreCase);

            var quantity = trade.Quantity;
            if (!useFutures)
            {
                var freeBase = await GetSpotAssetBalanceAsync(ExtractBaseAsset(trade.Symbol), connection);
                if (freeBase > 0m && freeBase < quantity)
                    quantity = freeBase;
            }

            if (quantity <= 0m)
                return BrokerOrderResult.Fail("Close skipped: no sellable quantity is available.");

            var previousProtection = trade.ProtectionStatus;
            trade.ProtectionStatus = "Closing";
            trade.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var exit = await bybit.V5Api.Trading.PlaceOrderAsync(
                category: category,
                symbol: trade.Symbol,
                side: isLong ? Bybit.Net.Enums.OrderSide.Sell : Bybit.Net.Enums.OrderSide.Buy,
                type: Bybit.Net.Enums.NewOrderType.Market,
                quantity: quantity,
                reduceOnly: useFutures ? true : (bool?)null);

            var exitOrderId = exit.Data?.OrderId;

            if (!exit.Success || string.IsNullOrWhiteSpace(exitOrderId))
            {
                var message = $"Exit order was rejected: {exit.Error}";
                trade.ProtectionStatus = previousProtection;
                trade.Reason = LimitLength(message, 480);
                trade.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return BrokerOrderResult.Fail(message);
            }

            var entryOrder = await _context.ExchangeOrders
                .Where(o => o.TradeId == trade.Id && o.OrderRole == "Entry")
                .OrderBy(o => o.Id)
                .FirstOrDefaultAsync();

            await PersistExchangeOrderAsync(
                userId, trade, connection, exitOrderId, isLong ? "SELL" : "BUY",
                "Market", useFutures, "Exit", entryOrder?.Id);

            trade.Reason = "Exit order submitted after an opposite signal.";
            trade.UpdatedAt = DateTime.UtcNow;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                Action = "PositionCloseSubmitted",
                Details = $"Bybit exit market order {exitOrderId} submitted for trade {trade.Id} ({trade.Symbol}, qty={quantity}).",
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            return BrokerOrderResult.SuccessResult(exitOrderId, null);
        }

        private static decimal RoundDownToStep(decimal value, decimal step) =>
            step <= 0m ? value : Math.Floor(value / step) * step;

        private static async Task<decimal?> TryGetSpotStepSizeAsync(
            BinanceRestClient binance,
            string symbol)
        {
            try
            {
                var info = await binance.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol);
                if (!info.Success || info.Data == null)
                    return null;

                var market = info.Data.Symbols.FirstOrDefault(x =>
                    string.Equals(x.Name, symbol, StringComparison.OrdinalIgnoreCase));

                var step = market?.LotSizeFilter?.StepSize;
                return step.HasValue && step.Value > 0m ? step : null;
            }
            catch
            {
                return null;
            }
        }

        internal async Task<bool> TryEnsureProtectionAsync(
            BrokerConnection connection,
            Trade trade,
            string entryExchangeOrderId,
            string entrySide)
        {
            if (!trade.StopLoss.HasValue && !trade.TakeProfit.HasValue)
            {
                trade.ProtectionStatus = "NotRequired";
                trade.UpdatedAt = DateTime.UtcNow;
                return true;
            }

            if (connection.BrokerName.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
            {
                var client = CreateClient(connection);
                if (client is not BybitRestClient bybit)
                    return false;

                var parent = await _context.ExchangeOrders.FirstOrDefaultAsync(o =>
                    o.TradeId == trade.Id &&
                    o.ExchangeOrderId == entryExchangeOrderId &&
                    o.OrderRole == "Entry");

                if (parent == null)
                    return false;

                if (parent.IsFutures)
                {
                    trade.ProtectionStatus = "ExchangeManaged";
                    trade.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return true;
                }

                var filledQuantity = parent.FilledQuantity;
                if (filledQuantity <= 0m)
                {
                    trade.ProtectionStatus = "Pending";
                    trade.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return true;
                }

                var protectQuantity = Math.Floor(filledQuantity * 10000m) / 10000m;

                if (protectQuantity <= 0m)
                {
                    trade.ProtectionStatus = "ProtectionPending";
                    trade.Reason = "Bybit Spot protection skipped: quantity too small.";
                    trade.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return false;
                }

                var protectionSide = entrySide.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                    ? Bybit.Net.Enums.OrderSide.Sell
                    : Bybit.Net.Enums.OrderSide.Buy;

                // Stop-loss: RAW HTTP call to Bybit V5 API with orderFilter="StopOrder".
                if (trade.StopLoss.HasValue && string.IsNullOrWhiteSpace(trade.StopLossOrderId))
                {
                    var sl = await PlaceBybitSpotStopOrderAsync(
                        connection,
                        trade.Symbol,
                        protectionSide,
                        protectQuantity,
                        trade.StopLoss.Value);

                    if (!sl.Success || string.IsNullOrWhiteSpace(sl.OrderId))
                    {
                        trade.ProtectionStatus = "ProtectionPending";
                        trade.Reason = LimitLength($"Bybit Spot stop-loss was not accepted: {sl.Error}", 480);
                        trade.UpdatedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        return false;
                    }

                    trade.StopLossOrderId = sl.OrderId;
                    await PersistExchangeOrderAsync(
                        trade.UserId, trade, connection, sl.OrderId,
                        protectionSide == Bybit.Net.Enums.OrderSide.Buy ? "BUY" : "SELL",
                        "StopMarket", false, "StopLoss", parent.Id);
                }

                // Take-profit: Bybit Spot cannot hold both SL and TP on the same quantity.
                // If SL is placed, we skip TP to prevent infinite 170131 retry loops.
                if (trade.TakeProfit.HasValue && string.IsNullOrWhiteSpace(trade.TakeProfitOrderId))
                {
                    if (!string.IsNullOrWhiteSpace(trade.StopLossOrderId))
                    {
                        trade.Reason = "Bybit Spot limitation: Take-profit skipped because Stop-loss is active.";
                    }
                    else
                    {
                        var tp = await bybit.V5Api.Trading.PlaceOrderAsync(
                            category: Bybit.Net.Enums.Category.Spot,
                            symbol: trade.Symbol,
                            side: protectionSide,
                            type: Bybit.Net.Enums.NewOrderType.Limit,
                            quantity: protectQuantity,
                            price: trade.TakeProfit.Value);

                        if (!tp.Success || tp.Data == null)
                        {
                            trade.ProtectionStatus = "ProtectionPending";
                            trade.Reason = LimitLength($"Bybit Spot take-profit was not accepted: {tp.Error}", 480);
                            trade.UpdatedAt = DateTime.UtcNow;
                            await _context.SaveChangesAsync();
                            return false;
                        }

                        trade.TakeProfitOrderId = tp.Data.OrderId;
                        await PersistExchangeOrderAsync(
                            trade.UserId, trade, connection, tp.Data.OrderId,
                            protectionSide == Bybit.Net.Enums.OrderSide.Buy ? "BUY" : "SELL",
                            "TakeProfit", false, "TakeProfit", parent.Id);
                    }
                }

                // Honest Status Tracking (Option A)
                var hasSl = !string.IsNullOrWhiteSpace(trade.StopLossOrderId);
                var hasTp = !string.IsNullOrWhiteSpace(trade.TakeProfitOrderId);

                if (hasSl && hasTp)
                    trade.ProtectionStatus = "Protected";
                else if (hasSl)
                    trade.ProtectionStatus = "SLOnly";
                else if (hasTp)
                    trade.ProtectionStatus = "TPOnly";
                else
                    trade.ProtectionStatus = "ProtectionFailed";

                trade.UpdatedAt = DateTime.UtcNow;
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = trade.UserId,
                    Action = "ProtectionOrdersCreated",
                    Details = $"Bybit Spot protection created for trade {trade.Id}. Status={trade.ProtectionStatus}, SL={trade.StopLossOrderId ?? "none"}, TP={trade.TakeProfitOrderId ?? "none"}.",
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return true;
            }

            if (!connection.BrokerName.Equals("Binance", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!long.TryParse(entryExchangeOrderId, out var numericEntryOrderId))
                return false;

            try
            {
                var client = CreateClient(connection);
                if (client is not BinanceRestClient binance)
                    return false;

                var parent = await _context.ExchangeOrders.FirstOrDefaultAsync(o =>
                    o.TradeId == trade.Id &&
                    o.ExchangeOrderId == entryExchangeOrderId &&
                    o.OrderRole == "Entry");

                if (parent == null)
                    return false;

                if (parent.IsFutures)
                {
                    var entry = await binance.UsdFuturesApi.Trading.GetOrderAsync(
                        trade.Symbol, numericEntryOrderId, null, null, CancellationToken.None);

                    if (!entry.Success || entry.Data == null)
                    {
                        trade.ProtectionStatus = "ProtectionPending";
                        await _context.SaveChangesAsync();
                        return false;
                    }

                    var filledQuantity = Convert.ToDecimal(entry.Data.QuantityFilled, System.Globalization.CultureInfo.InvariantCulture);
                    if (filledQuantity <= 0m)
                    {
                        trade.ProtectionStatus = "Pending";
                        trade.UpdatedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                        return true;
                    }

                    var protectionSide = entrySide.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                        ? OrderSide.Sell
                        : OrderSide.Buy;

                    var futuresTick = await TryGetFuturesTickSizeAsync(binance, trade.Symbol);
                    if (futuresTick.HasValue)
                    {
                        if (trade.StopLoss.HasValue)
                            trade.StopLoss = RoundToStep(trade.StopLoss.Value, futuresTick.Value);
                        if (trade.TakeProfit.HasValue)
                            trade.TakeProfit = RoundToStep(trade.TakeProfit.Value, futuresTick.Value);
                    }

                    if (trade.StopLoss.HasValue && string.IsNullOrWhiteSpace(trade.StopLossOrderId))
                    {
                        var stop = await binance.UsdFuturesApi.Trading.PlaceOrderAsync(
                            symbol: trade.Symbol, side: protectionSide, type: FuturesOrderType.StopMarket,
                            quantity: filledQuantity, positionSide: PositionSide.Both, reduceOnly: true,
                            stopPrice: trade.StopLoss.Value);

                        if (!stop.Success || stop.Data == null)
                        {
                            trade.ProtectionStatus = "ProtectionPending";
                            trade.Reason = LimitLength($"Binance Futures stop-loss was not accepted: {stop.Error}", 480);
                            trade.UpdatedAt = DateTime.UtcNow;
                            await _context.SaveChangesAsync();
                            return false;
                        }

                        trade.StopLossOrderId = stop.Data.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        await PersistExchangeOrderAsync(trade.UserId, trade, connection, trade.StopLossOrderId,
                            protectionSide == OrderSide.Buy ? "BUY" : "SELL", "StopMarket", true, "StopLoss", parent.Id);
                    }

                    if (trade.TakeProfit.HasValue && string.IsNullOrWhiteSpace(trade.TakeProfitOrderId))
                    {
                        var tp = await binance.UsdFuturesApi.Trading.PlaceOrderAsync(
                            symbol: trade.Symbol, side: protectionSide, type: FuturesOrderType.TakeProfitMarket,
                            quantity: filledQuantity, positionSide: PositionSide.Both, reduceOnly: true,
                            stopPrice: trade.TakeProfit.Value);

                        if (!tp.Success || tp.Data == null)
                        {
                            trade.ProtectionStatus = "ProtectionPending";
                            trade.Reason = LimitLength($"Binance Futures take-profit was not accepted: {tp.Error}", 480);
                            trade.UpdatedAt = DateTime.UtcNow;
                            await _context.SaveChangesAsync();
                            return false;
                        }

                        trade.TakeProfitOrderId = tp.Data.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        await PersistExchangeOrderAsync(trade.UserId, trade, connection, trade.TakeProfitOrderId,
                            protectionSide == OrderSide.Buy ? "BUY" : "SELL", "TakeProfitMarket", true, "TakeProfit", parent.Id);
                    }

                    trade.ProtectionStatus = "Protected";
                    trade.UpdatedAt = DateTime.UtcNow;
                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = trade.UserId, Action = "ProtectionOrdersCreated",
                        Details = $"Binance Futures protection created for trade {trade.Id}. SL={trade.StopLossOrderId ?? "none"}, TP={trade.TakeProfitOrderId ?? "none"}.",
                        CreatedAt = DateTime.UtcNow
                    });
                    await _context.SaveChangesAsync();
                    return true;
                }

                if (!entrySide.Equals("BUY", StringComparison.OrdinalIgnoreCase))
                    return false;

                var spotEntry = await binance.SpotApi.Trading.GetOrderAsync(
                    trade.Symbol, numericEntryOrderId, null, null, CancellationToken.None);

                if (!spotEntry.Success || spotEntry.Data == null)
                {
                    trade.ProtectionStatus = "ProtectionPending";
                    trade.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return false;
                }

                var filledSpotQuantity = Convert.ToDecimal(spotEntry.Data.QuantityFilled, System.Globalization.CultureInfo.InvariantCulture);
                if (filledSpotQuantity <= 0m)
                {
                    trade.ProtectionStatus = "Pending";
                    trade.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return true;
                }

                if (!trade.StopLoss.HasValue || !trade.TakeProfit.HasValue)
                    return false;

                var protectQuantity = filledSpotQuantity;
                var freeBase = await GetSpotAssetBalanceAsync(ExtractBaseAsset(trade.Symbol), connection);
                if (freeBase > 0m && freeBase < protectQuantity)
                    protectQuantity = freeBase;

                var stepSize = await TryGetSpotStepSizeAsync(binance, trade.Symbol);
                if (stepSize.HasValue)
                    protectQuantity = RoundDownToStep(protectQuantity, stepSize.Value);

                if (protectQuantity <= 0m)
                {
                    trade.ProtectionStatus = "ProtectionPending";
                    trade.Reason = "Protection skipped: no sellable quantity after fees.";
                    trade.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return false;
                }

                var tickSize = await TryGetSpotTickSizeAsync(binance, trade.Symbol);
                if (tickSize.HasValue)
                {
                    trade.StopLoss = RoundToStep(trade.StopLoss.Value, tickSize.Value);
                    trade.TakeProfit = RoundToStep(trade.TakeProfit.Value, tickSize.Value);
                }

                var oco = await binance.SpotApi.Trading.PlaceOcoOrderAsync(
                    symbol: trade.Symbol, side: OrderSide.Sell, quantity: protectQuantity,
                    price: trade.TakeProfit.Value, stopPrice: trade.StopLoss.Value,
                    stopLimitPrice: trade.StopLoss.Value, stopLimitTimeInForce: TimeInForce.GoodTillCanceled);

                if (!oco.Success || oco.Data == null)
                {
                    var ocoError = oco.Error?.ToString() ?? "no error detail returned";
                    trade.ProtectionStatus = "ProtectionPending";
                    trade.Reason = LimitLength(
                        $"Binance Spot OCO protection was not accepted: {ocoError}", 480);
                    trade.UpdatedAt = DateTime.UtcNow;
                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = trade.UserId, Action = "ProtectionOrdersPartialFailure",
                        Details = $"Binance Spot OCO protection failed for trade {trade.Id} " +
                                  $"(qty={protectQuantity}, SL={trade.StopLoss}, TP={trade.TakeProfit}, " +
                                  $"tick={(tickSize.HasValue ? tickSize.Value.ToString() : "unknown")}): {ocoError}",
                        CreatedAt = DateTime.UtcNow
                    });
                    await _context.SaveChangesAsync();
                    return false;
                }

                var persistedProtection = await PersistSpotOcoProtectionOrdersAsync(
                    trade,
                    connection,
                    parent.Id,
                    oco.Data,
                    binance);

                if (!persistedProtection)
                {
                    trade.ProtectionStatus = "ProtectionPending";
                    trade.Reason = "Spot OCO was accepted by Binance but its child orders could not be persisted locally; reconciliation will retry.";
                    trade.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return false;
                }

                trade.ProtectionStatus = "Protected";
                trade.Reason = null;
                trade.UpdatedAt = DateTime.UtcNow;
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = trade.UserId, Action = "ProtectionOrdersCreated",
                    Details = $"Binance Spot OCO protection created for trade {trade.Id}. SL={trade.StopLossOrderId}, TP={trade.TakeProfitOrderId}.",
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return true;
            }
            catch
            {
                trade.ProtectionStatus = "ProtectionPending";
                trade.Reason = "Protection creation failed unexpectedly; reconciliation will retry.";
                trade.UpdatedAt = DateTime.UtcNow;
                try { await _context.SaveChangesAsync(); } catch { }
                return false;
            }
        }

        // ------------------------------------------------------------------
        // RAW BYBIT SPOT CONDITIONAL ORDER (WORKAROUND)
        // ------------------------------------------------------------------
        private async Task<BrokerOrderResult> PlaceBybitSpotStopOrderAsync(
            BrokerConnection connection,
            string symbol,
            Bybit.Net.Enums.OrderSide side,
            decimal quantity,
            decimal triggerPrice)

        {   
            try
            {
                var apiKey = _protector.Unprotect(connection.ApiKey);
                var apiSecret = _protector.Unprotect(connection.ApiSecret);

                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                const string recvWindow = "20000";   

                var sideString = side == Bybit.Net.Enums.OrderSide.Buy ? "Buy" : "Sell";
                var qtyString = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var triggerString = triggerPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);

                var bodyJson =
                     "{" +
                    "\"category\":\"spot\"," +
                    $"\"symbol\":\"{symbol}\"," +
                    $"\"side\":\"{sideString}\"," +
                    "\"orderType\":\"Market\"," +
                    $"\"qty\":\"{qtyString}\"," +
                    $"\"triggerPrice\":\"{triggerString}\"," +
                    "\"triggerDirection\":2," +
                    "\"triggerBy\":\"LastPrice\"," +
                    "\"closeOnTrigger\":true," +  // <-- THE FIX
                    "\"orderFilter\":\"StopOrder\"" +
                    "}";   
                    

                var signaturePayload = timestamp + apiKey + recvWindow + bodyJson;

                using var hmac = new System.Security.Cryptography.HMACSHA256(
                    System.Text.Encoding.UTF8.GetBytes(apiSecret));

                var signatureBytes = hmac.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(signaturePayload));

                var signature = Convert.ToHexString(signatureBytes).ToLowerInvariant();
  
                var baseUrl = connection.IsTestnet
                    ? "https://api-demo.bybit.com"
                    : "https://api.bybit.com";

                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post,
                    baseUrl + "/v5/order/create");

                request.Headers.Add("X-BAPI-API-KEY", apiKey);
                request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
                request.Headers.Add("X-BAPI-SIGN", signature);
                request.Headers.Add("X-BAPI-RECV-WINDOW", recvWindow);

                request.Content = new System.Net.Http.StringContent(
                    bodyJson,
                    System.Text.Encoding.UTF8,
                    "application/json");

                var response = await httpClient.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("retCode", out var retCodeElement))
                    return BrokerOrderResult.Fail($"Bybit response missing retCode: {responseJson}");

                var retCode = retCodeElement.GetInt32();

                if (retCode != 0)
                {
                    var retMsg = root.TryGetProperty("retMsg", out var msgEl)
                        ? msgEl.GetString() ?? "unknown"
                        : "unknown";

                    return BrokerOrderResult.Fail($"Bybit API error {retCode}: {retMsg}");
                }

                if (!root.TryGetProperty("result", out var resultEl) ||
                    !resultEl.TryGetProperty("orderId", out var orderIdEl))
                {
                    return BrokerOrderResult.Fail($"Bybit response missing orderId: {responseJson}");
                }

                var orderId = orderIdEl.GetString();

                if (string.IsNullOrWhiteSpace(orderId))
                    return BrokerOrderResult.Fail("Bybit returned an empty orderId.");

                return BrokerOrderResult.SuccessResult(orderId, null);
            }
            catch (Exception ex)
            {
                return BrokerOrderResult.Fail($"Raw HTTP order placement failed: {ex.Message}");
            }
        }

        private async Task<bool> PersistSpotOcoProtectionOrdersAsync(
            Trade trade,
            BrokerConnection connection,
            int parentExchangeOrderId,
            object ocoData,
            BinanceRestClient binance)
        {
            try
            {
                var orders = GetEnumerableProperty(ocoData, "Orders")
                    .Where(x => x != null)
                    .ToList();

                if (orders.Count < 2)
                    return false;

                var reports = GetEnumerableProperty(ocoData, "OrderReports")
                    .Where(x => x != null)
                    .ToList();

                string? stopLossOrderId = null;
                string? takeProfitOrderId = null;

                foreach (var order in orders)
                {
                    var id = GetStringProperty(order!, "OrderId");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var type = FindOrderTypeForId(reports, id);

                    if (string.IsNullOrWhiteSpace(type) &&
                        long.TryParse(id, out var numericId))
                    {
                        var result = await binance.SpotApi.Trading.GetOrderAsync(
                            trade.Symbol,
                            numericId,
                            null,
                            null,
                            CancellationToken.None);

                        if (result.Success && result.Data != null)
                            type = result.Data.Type.ToString();
                    }

                    if (type.Contains("STOP", StringComparison.OrdinalIgnoreCase))
                        stopLossOrderId = id;
                    else if (type.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
                        takeProfitOrderId = id;
                }

                if (string.IsNullOrWhiteSpace(stopLossOrderId) ||
                    string.IsNullOrWhiteSpace(takeProfitOrderId))
                    return false;

                trade.StopLossOrderId = stopLossOrderId;
                trade.TakeProfitOrderId = takeProfitOrderId;

                await PersistExchangeOrderIfMissingAsync(
                    trade,
                    connection,
                    stopLossOrderId,
                    "SELL",
                    "StopLoss",
                    "StopLossLimit",
                    parentExchangeOrderId);

                await PersistExchangeOrderIfMissingAsync(
                    trade,
                    connection,
                    takeProfitOrderId,
                    "SELL",
                    "TakeProfit",
                    "LimitMaker",
                    parentExchangeOrderId);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task PersistExchangeOrderIfMissingAsync(
            Trade trade,
            BrokerConnection connection,
            string exchangeOrderId,
            string side,
            string orderRole,
            string orderType,
            int parentExchangeOrderId)
        {
            var existing = await _context.ExchangeOrders.FirstOrDefaultAsync(o =>
                o.UserId == trade.UserId &&
                o.TradeId == trade.Id &&
                o.ExchangeOrderId == exchangeOrderId);

            if (existing != null)
            {
                existing.OrderRole = orderRole;
                existing.OrderType = orderType;
                existing.ParentExchangeOrderId = parentExchangeOrderId;
                existing.UpdatedAt = DateTime.UtcNow;
                return;
            }

            _context.ExchangeOrders.Add(new ExchangeOrder
            {
                UserId = trade.UserId,
                TradeId = trade.Id,
                BrokerName = connection.BrokerName,
                ExchangeOrderId = exchangeOrderId,
                Symbol = trade.Symbol,
                Side = side,
                OrderType = orderType,
                RequestedQuantity = trade.Quantity,
                FilledQuantity = 0m,
                AverageFillPrice = 0m,
                Status = "New",
                IsTestnet = connection.IsTestnet,
                IsFutures = false,
                OrderRole = orderRole,
                ParentExchangeOrderId = parentExchangeOrderId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastReconciledAt = null
            });

            await _context.SaveChangesAsync();
        }

        private static IEnumerable<object?> GetEnumerableProperty(
            object source,
            string propertyName)
        {
            var property = source.GetType().GetProperty(propertyName);
            var value = property?.GetValue(source);

            return value is System.Collections.IEnumerable enumerable
                ? enumerable.Cast<object?>()
                : Enumerable.Empty<object?>();
        }

        private static string GetStringProperty(object source, string propertyName)
        {
            var property = source.GetType().GetProperty(propertyName);
            return property?.GetValue(source)?.ToString() ?? string.Empty;
        }

        private static string FindOrderTypeForId(
            IEnumerable<object?> reports,
            string orderId)
        {
            foreach (var report in reports)
            {
                if (report == null)
                    continue;

                var reportId = GetStringProperty(report, "OrderId");
                if (reportId == orderId)
                    return GetStringProperty(report, "Type");
            }

            return string.Empty;
        }

        private async Task<decimal> GetSpotAssetBalanceAsync(string asset, BrokerConnection connection)
        {
            try
            {
                var client = CreateClient(connection);
                if (client is BinanceRestClient binance)
                {
                    var result = await binance.SpotApi.Account.GetAccountInfoAsync();
                    if (result.Success)
                        return result.Data.Balances.FirstOrDefault(b => b.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase))?.Available ?? 0m;
                }

                if (client is BybitRestClient bybit)
                {
                    var result = await bybit.V5Api.Account.GetBalancesAsync(Bybit.Net.Enums.AccountType.Unified, asset, CancellationToken.None);
                    if (result.Success)
                    {
                        var coin = result.Data.List?.FirstOrDefault()?.Assets?.FirstOrDefault(a => a.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase));
                        if (coin != null)
                            return coin.Free ?? Math.Max(0m, coin.WalletBalance - (coin.Locked ?? 0m));
                    }
                }
            }
            catch { }
            return 0m;
        }

        private static string ExtractBaseAsset(string symbol)
        {
            var normalized = symbol.Trim().ToUpperInvariant();
            foreach (var quote in new[] { "USDT", "USDC", "BUSD", "FDUSD", "BTC", "ETH" })
                if (normalized.EndsWith(quote, StringComparison.Ordinal))
                    return normalized[..^quote.Length];
            return normalized;
        }

        private async Task<BrokerOrderResult> AuditOrderFailureAsync(
            int userId, string action, string message, string details)
        {
            _context.AuditLogs.Add(new AuditLog
            { UserId = userId, Action = action, Details = details, CreatedAt = DateTime.UtcNow });
            await _context.SaveChangesAsync();
            return BrokerOrderResult.Fail(message);
        }

        private async Task AuditOrderEventAsync(
            int userId, string action, string details)
        {
            _context.AuditLogs.Add(new AuditLog
            { UserId = userId, Action = action, Details = details, CreatedAt = DateTime.UtcNow });
            await _context.SaveChangesAsync();
        }

        private async Task PersistExchangeOrderAsync(
            int userId,
            Trade trade,
            BrokerConnection connection,
            string exchangeOrderId,
            string side,
            string orderType,
            bool useFutures,
            string orderRole = "Entry",
            int? parentExchangeOrderId = null)
        {
            var exchangeOrder = new ExchangeOrder
            {
                UserId = userId,
                TradeId = trade.Id,
                BrokerName = connection.BrokerName,
                ExchangeOrderId = exchangeOrderId,
                Symbol = trade.Symbol,
                Side = side,
                OrderType = orderType,
                RequestedQuantity = trade.Quantity,
                FilledQuantity = 0m,
                AverageFillPrice = 0m,
                Status = "New",
                IsTestnet = connection.IsTestnet,
                IsFutures = useFutures,
                OrderRole = orderRole,
                ParentExchangeOrderId = parentExchangeOrderId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastReconciledAt = null
            };

            _context.ExchangeOrders.Add(exchangeOrder);

            await _context.SaveChangesAsync();
        }

        public async Task<bool> CancelOrderAsync(
            int userId, string symbol, string orderId, bool useFutures = false)
        {
            symbol = symbol?.Trim().ToUpperInvariant() ?? string.Empty;
            orderId = orderId?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(symbol) ||
                string.IsNullOrWhiteSpace(orderId))
            {
                await AuditOrderEventAsync(
                    userId,
                    "OrderCancellationFailed",
                    "Order cancellation was rejected because the symbol or order ID was missing.");
                return false;
            }

            var localOrder = await _context.ExchangeOrders
                .AsNoTracking()
                .FirstOrDefaultAsync(o =>
                    o.UserId == userId &&
                    o.ExchangeOrderId == orderId &&
                    o.Symbol == symbol);

            if (localOrder == null)
            {
                await AuditOrderEventAsync(
                    userId,
                    "OrderCancellationFailed",
                    $"Order {orderId} cancellation was rejected because no matching local order exists for {symbol}.");
                return false;
            }

            if (string.Equals(localOrder.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(localOrder.Status, "FILLED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(localOrder.Status, "REJECTED", StringComparison.OrdinalIgnoreCase))
            {
                await AuditOrderEventAsync(
                    userId,
                    "OrderCancellationFailed",
                    $"Order {orderId} cancellation was rejected because the local order is already in terminal state '{localOrder.Status}'.");
                return false;
            }

            var connection = await GetConnectionAsync(userId);

            if (connection == null || !connection.IsConnected)
            {
                await AuditOrderEventAsync(userId, "OrderCancellationFailed",
                    $"Order {orderId} cancellation failed because no active broker connection exists.");
                return false;
            }

            try
            {
                var client = CreateClient(connection);

                if (client is BinanceRestClient binance && long.TryParse(orderId, out var numericOrderId))
                {
                    bool success;
                    if (useFutures)
                    {
                        success = (await binance.UsdFuturesApi.Trading.CancelOrderAsync(symbol, numericOrderId)).Success;
                    }
                    else
                    {
                        success = (await binance.SpotApi.Trading.CancelOrderAsync(symbol, numericOrderId)).Success;
                    }

                    await AuditOrderEventAsync(userId, success ? "OrderCancelled" : "OrderCancellationFailed",
                        $"Order {orderId} cancellation on {connection.BrokerName} for {symbol} {(success ? "succeeded" : "failed")}.");
                    return success;
                }

                if (client is BybitRestClient bybit)
                {
                    var category = useFutures ? Bybit.Net.Enums.Category.Linear : Bybit.Net.Enums.Category.Spot;
                    var success = (await bybit.V5Api.Trading.CancelOrderAsync(category, symbol, orderId)).Success;
                    await AuditOrderEventAsync(userId, success ? "OrderCancelled" : "OrderCancellationFailed",
                        $"Order {orderId} cancellation on {connection.BrokerName} for {symbol} {(success ? "succeeded" : "failed")}.");
                    return success;
                }

                await AuditOrderEventAsync(userId, "OrderCancellationFailed",
                    $"Order {orderId} cancellation failed because the broker '{connection.BrokerName}' or order ID format is unsupported.");
            }
            catch
            {
                await AuditOrderEventAsync(userId, "OrderCancellationFailed",
                    $"Order {orderId} cancellation failed for {symbol} on {connection.BrokerName}.");
            }

            return false;
        }

        public async Task<bool> CancelOpenExchangeOrderForUserAsync(
            int userId,
            string symbol,
            string orderId,
            bool useFutures = false)
        {
            symbol = symbol?.Trim().ToUpperInvariant() ?? string.Empty;
            orderId = orderId?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(symbol) ||
                string.IsNullOrWhiteSpace(orderId))
                return false;

            var connection = await GetConnectionAsync(userId);

            if (connection == null ||
                !connection.IsConnected ||
                !connection.IsTestnet ||
                !connection.BrokerName.Equals("Binance", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!long.TryParse(orderId, out var numericOrderId))
                return false;

            try
            {
                var client = CreateClient(connection);
                if (client is not BinanceRestClient binance)
                    return false;

                var openOrders = await binance.SpotApi.Trading.GetOpenOrdersAsync(symbol);
                if (!openOrders.Success || openOrders.Data == null)
                    return false;

                var isOpen = openOrders.Data.Any(o =>
                    o.Id == numericOrderId);

                if (!isOpen)
                    return false;

                var result = await binance.SpotApi.Trading.CancelOrderAsync(
                    symbol,
                    numericOrderId);

                await AuditOrderEventAsync(
                    userId,
                    result.Success ? "TestnetOpenOrderCancelled" : "TestnetOpenOrderCancellationFailed",
                    $"Testnet Binance open order {orderId} cancellation for {symbol}: {(result.Success ? "succeeded" : "failed")}.");

                return result.Success;
            }
            catch
            {
                return false;
            }
        }

        public async Task<object?> GetOpenOrdersAsync(
            int userId,
            string? symbol = null,
            bool useFutures = false)
        {
            var connection =
                await GetConnectionAsync(userId);

            if (connection == null ||
                !connection.IsConnected)
            {
                return null;
            }

            try
            {
                var client =
                    CreateClient(connection);

                if (client is BinanceRestClient binance)
                {
                    if (useFutures)
                    {
                        var result =
                            await binance
                                .UsdFuturesApi
                                .Trading
                                .GetOpenOrdersAsync(
                                    symbol);

                        return result.Success
                            ? result.Data
                            : null;
                    }

                    var spot =
                        await binance
                            .SpotApi
                            .Trading
                            .GetOpenOrdersAsync(
                                symbol);

                    return spot.Success
                        ? spot.Data
                        : null;
                }

                if (client is BybitRestClient bybit)
                {
                    var category =
                        useFutures
                            ? Bybit.Net.Enums.Category.Linear
                            : Bybit.Net.Enums.Category.Spot;

                    var result =
                        await bybit
                            .V5Api
                            .Trading
                            .GetOrdersAsync(
                                category,
                                symbol);

                    return result.Success
                        ? result.Data
                        : null;
                }
            }
            catch
            {
            }

            return null;
        }

        private async Task<BrokerConnection?>
            GetConnectionAsync(int userId, int? connectionId = null)
        {
            var query = _context.BrokerConnections
                .Where(b =>
                    b.UserId == userId &&
                    b.IsActive &&
                    b.IsConnected);

            if (connectionId.HasValue)
            {
                return await query.FirstOrDefaultAsync(b => b.Id == connectionId.Value);
            }

            return await query
                .OrderByDescending(b => b.LastConnectedAt)
                .ThenByDescending(b => b.Id)
                .FirstOrDefaultAsync();
        }

        internal object CreateClient(
            BrokerConnection connection)
        {
            var apiKey =
                _protector.Unprotect(
                    connection.ApiKey);

            var apiSecret =
                string.IsNullOrWhiteSpace(
                    connection.ApiSecret)
                    ? null
                    : _protector.Unprotect(
                        connection.ApiSecret);

            if (string.IsNullOrWhiteSpace(apiSecret))
            {
                throw new InvalidOperationException(
                    "Broker secret is missing.");
            }

            if (connection.BrokerName.Equals(
                    "binance",
                    StringComparison.OrdinalIgnoreCase))
            {
                var client =
                    new BinanceRestClient(options =>
                    {
                        options.Environment =
                            connection.IsTestnet
                                ? BinanceEnvironment.Testnet
                                : BinanceEnvironment.Live;
                    });

                client.SetApiCredentials(
                    new ApiCredentials(
                        apiKey,
                        apiSecret));

                return client;
            }

            if (connection.BrokerName.Equals(
                    "bybit",
                    StringComparison.OrdinalIgnoreCase))
            {
                var client =
                    new BybitRestClient(options =>
                    {
                        options.Environment =
                            BybitEnvironments.ForTrading(connection.IsTestnet);
                    });

                client.SetApiCredentials(
                    new ApiCredentials(
                        apiKey,
                        apiSecret));

                return client;
            }

            throw new InvalidOperationException(
                "Unsupported broker.");
        }

        private static async Task<bool>
            VerifyConnectionWithRetryAsync(
                object client,
                BrokerConnection connection)
        {
            const int maxAttempts = 3;
            var delay = TimeSpan.FromSeconds(1);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (await VerifyConnectionAsync(client, connection))
                        return true;
                }
                catch (Exception verifyException) when (attempt < maxAttempts)
                {
                    Console.Error.WriteLine(
                        $"[BrokerVerify] attempt {attempt}/{maxAttempts} threw: {verifyException.Message}");
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(delay);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                }
            }

            return false;
        }

        private static async Task<bool>
            VerifyConnectionAsync(
                object client,
                BrokerConnection connection)
        {
            if (client is BinanceRestClient binance)
            {
                var result =
                    await binance
                        .SpotApi
                        .Account
                        .GetAccountInfoAsync();

                if (!result.Success)
                {
                    Console.Error.WriteLine(
                        $"[BrokerVerify] Binance verification failed (testnet={connection.IsTestnet}): {result.Error}");
                }

                return result.Success;
            }

            if (client is BybitRestClient bybit)
            {
                var result =
                    await bybit
                        .V5Api
                        .Account
                        .GetMarginAccountInfoAsync(
                            CancellationToken.None);

                if (!result.Success)
                {
                    Console.Error.WriteLine(
                        $"[BrokerVerify] Bybit verification failed " +
                        $"(testnet={connection.IsTestnet}, UseDemoTrading={BybitEnvironments.UseDemoTrading}): {result.Error}");
                }

                return result.Success;
            }

            return false;
        }
    }

    public sealed class BrokerConnectionStatus
    {
        public int Id { get; set; }

        public string BrokerName { get; set; }
            = string.Empty;

        public bool IsConnected { get; set; }

        public bool IsTestnet { get; set; }

        public bool IsLiveTradingEnabled { get; set; }

        public DateTime? LastConnectedAt { get; set; }

        public DateTime? LastDisconnectedAt { get; set; }
    }

    public sealed class BrokerOrderResult
    {
        public bool Success { get; init; }

        public string? OrderId { get; init; }

        public decimal? Price { get; init; }

        public string? Error { get; init; }

        public static BrokerOrderResult SuccessResult(
            string? orderId,
            decimal? price) =>
            new()
            {
                Success = true,
                OrderId = orderId,
                Price = price
            };

        public static BrokerOrderResult Fail(
            string error) =>
            new()
            {
                Success = false,
                Error = error
            };
    }
}