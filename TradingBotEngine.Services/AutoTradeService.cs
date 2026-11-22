using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using TradingBotEngine.Core;
using TradingBotEngine.Core.Models;
using TradingBotEngine.Data;
using TradingBotEngine.Data.Models;
   
namespace TradingBotEngine.Services;

public sealed class AutoTradeService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoTradeService> _logger;
    private readonly TelegramBotService _telegram;
    private readonly ITradingRealtimePublisher _realtime;

    private static readonly TimeSpan WorkerInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MarketDataTimeout = TimeSpan.FromSeconds(20);
    private const int CandleLimit = 250;

    private static readonly SemaphoreSlim WakeSignal = new(0, 1);

    public static void RequestImmediateRun()
    {
        try { WakeSignal.Release(); }
        catch (SemaphoreFullException) { /* a wake-up is already pending */ }
    }

    private static readonly bool AllowSpotSellWithoutPosition = false;

    private static readonly string[] ActiveTradeStatuses =
    {
        "Pending", "Open", "New", "PartiallyFilled", "Filled", "PendingReconciliation"
    };

    private readonly Dictionary<string, DateTime> _lastProcessedCandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _processedCandleLock = new();
    private const int MaxMarketDataAttempts = 3;
    private DateTime _lastReconciliationAt = DateTime.MinValue;
    private long _completedCycles;
    private long _failedCycles;
    // Tracks consecutive API errors per bot. If it hits 5, the bot shuts down.
    private readonly Dictionary<int, int> _consecutiveApiErrors = new();
    private static readonly TimeSpan[] MarketDataRetryDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    };

    public AutoTradeService(
        IServiceProvider serviceProvider,
        ILogger<AutoTradeService> logger,
        TelegramBotService telegram,
        ITradingRealtimePublisher realtime)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _telegram = telegram;
        _realtime = realtime;
    } 

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AutoTradeService started. Interval={IntervalSeconds}s, reconciliation interval={ReconciliationIntervalSeconds}s.",
            WorkerInterval.TotalSeconds,
            ReconciliationInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStartedAt = DateTime.UtcNow;

            try
            {
                await ProcessRunningBotsAsync(stoppingToken);
                await MaybeRunReconciliationAsync(stoppingToken);

                _completedCycles++;
                _logger.LogInformation(
                    "AutoTradeService cycle completed. DurationMs={DurationMs}, CompletedCycles={CompletedCycles}, FailedCycles={FailedCycles}.",
                    (DateTime.UtcNow - cycleStartedAt).TotalMilliseconds,
                    _completedCycles,
                    _failedCycles);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _failedCycles++;
                _logger.LogError(
                    ex,
                    "AutoTradeService cycle failed but the worker will continue. DurationMs={DurationMs}, CompletedCycles={CompletedCycles}, FailedCycles={FailedCycles}.",
                    (DateTime.UtcNow - cycleStartedAt).TotalMilliseconds,
                    _completedCycles,
                    _failedCycles);
            }

            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delayTask = Task.Delay(WorkerInterval, waitCts.Token);
                var wakeTask = WakeSignal.WaitAsync(waitCts.Token);

                await Task.WhenAny(delayTask, wakeTask);
                waitCts.Cancel();

                try { await Task.WhenAll(delayTask, wakeTask); }
                catch (OperationCanceledException) { /* the losing wait was cancelled on purpose */ }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation(
            "AutoTradeService stopped. CompletedCycles={CompletedCycles}, FailedCycles={FailedCycles}.",
            _completedCycles,
            _failedCycles);
    }

    private async Task ProcessRunningBotsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var marketData = scope.ServiceProvider.GetRequiredService<ExchangeMarketDataService>();
        var indicatorCalculator = scope.ServiceProvider.GetRequiredService<IndicatorCalculator>();
        var signalGenerator = scope.ServiceProvider.GetRequiredService<SignalGenerator>();
        var brokerService = scope.ServiceProvider.GetRequiredService<BrokerService>();

        var bots = await db.TradingBots
            .Include(b => b.User)
                .ThenInclude(u => u.Subscription)
            .Include(b => b.User)
                .ThenInclude(u => u.RiskSetting)
            .Include(b => b.BrokerConnection)
            .Include(b => b.TrackedSymbols)
                .ThenInclude(bs => bs.TrackedSymbol)
            .Where(b => b.IsEnabled && b.IsRunning)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("P1.6D worker cycle found {BotCount} enabled/running bot(s).", bots.Count);

        if (bots.Count == 0)
        {
            _logger.LogDebug("No enabled and running trading bots are currently configured.");
            return;
        }

        foreach (var bot in bots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ProcessBotAsync(bot, marketData, indicatorCalculator, signalGenerator, brokerService, db, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-trade processing failed for bot {BotId} ({BotName}), user {UserId}.", bot.Id, bot.Name, bot.UserId);
            }
        }
    }

    private async Task ProcessBotAsync(
        TradingBot bot,
        ExchangeMarketDataService marketData,
        IndicatorCalculator indicatorCalculator,
        SignalGenerator signalGenerator,
        BrokerService brokerService,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("P1.6D processing bot {BotId} ({BotName}), user {UserId}.", bot.Id, bot.Name, bot.UserId);

        var eligibility = EvaluateEligibility(bot);

        if (!eligibility.IsEligible)
        {
            _logger.LogWarning("Auto-trade blocked for bot {BotId} ({BotName}), user {UserId}: {Reason}", bot.Id, bot.Name, bot.UserId, eligibility.Reason);
            return;
        }

        var broker = bot.BrokerConnection!;
        var enabledSymbols = bot.TrackedSymbols
            .Where(x => x.TrackedSymbol != null && x.TrackedSymbol.IsEnabled)
            .Select(x => x.TrackedSymbol!)
            .ToList();

        _logger.LogInformation(
            "P1.6D bot {BotId} eligible. Broker={Broker}, Testnet={IsTestnet}, Futures={UseFutures}, Symbols={SymbolCount}.",
            bot.Id, broker.BrokerName, broker.IsTestnet, bot.UseFutures, enabledSymbols.Count);

        foreach (var trackedSymbol in enabledSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("P1.6D starting symbol processing for bot {BotId}, symbol {Symbol}, timeframe {Timeframe}.", bot.Id, trackedSymbol.Symbol, bot.Timeframe);

                        try
            {
                await ProcessSymbolAsync(bot, broker, trackedSymbol.Symbol, marketData, indicatorCalculator, signalGenerator, brokerService, db, cancellationToken);
                
                // Success! Reset the error counter for this bot.
                _consecutiveApiErrors[bot.Id] = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-trade symbol processing failed for bot {BotId}, symbol {Symbol}.", bot.Id, trackedSymbol.Symbol);

                // Increment the error counter
                if (!_consecutiveApiErrors.ContainsKey(bot.Id)) _consecutiveApiErrors[bot.Id] = 0;
                _consecutiveApiErrors[bot.Id]++;

                if (_consecutiveApiErrors[bot.Id] >= 5)
                {
                    _logger.LogWarning("Bot {BotId} reached 5 consecutive API errors. Auto-shutting down.", bot.Id);
                    
                    var lastError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;

                    bot.IsRunning = false;
                    bot.UpdatedAt = DateTime.UtcNow;
                    
                    db.AuditLogs.Add(new AuditLog
                    {
                        UserId = bot.UserId,
                        Action = "AutoTradeBotShutdown",
                        Details = $"Bot {bot.Id} ('{bot.Name}') was stopped automatically after 5 consecutive errors. Last error on {trackedSymbol.Symbol}: {lastError}",
                        CreatedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync(cancellationToken);

                    // Optional: send Telegram alert
                    if (bot.User.TelegramChatId.HasValue)
                    {
                        await _telegram.SendAlertAsync(bot.User.TelegramChatId.Value, $"⚠️ Bot '{bot.Name}' was SHUT DOWN automatically after 5 repeated errors on {trackedSymbol.Symbol}. Last error: {lastError}");
                    }

                    _consecutiveApiErrors[bot.Id] = 0; // Reset after shutdown
                }
            }  
        }
    }  

    private async Task ProcessSymbolAsync(
        TradingBot bot,
        BrokerConnection broker,
        string symbol,
        ExchangeMarketDataService marketData,
        IndicatorCalculator indicatorCalculator,
        SignalGenerator signalGenerator,
        BrokerService brokerService,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        // =====================================================================
        // FEATURE #2: MULTI-TIMEFRAME ANALYSIS
        // =====================================================================
        _logger.LogInformation("Fetching HTF bias for {Symbol}...", symbol);
        
        var htf15m = await GetCandlesWithRetryAsync(marketData, broker.BrokerName, symbol, "15m", bot.UseFutures, broker.IsTestnet, cancellationToken);
        var htf1h = await GetCandlesWithRetryAsync(marketData, broker.BrokerName, symbol, "1h", bot.UseFutures, broker.IsTestnet, cancellationToken);

        var bias15m = CalculateMacroBias(htf15m);
        var bias1h = CalculateMacroBias(htf1h);

        _logger.LogInformation("HTF Bias for {Symbol}: 15m={Bias15m}, 1h={Bias1h}", symbol, bias15m, bias1h);

        // =====================================================================
        // PRIMARY TIMEFRAME
        // =====================================================================
        _logger.LogInformation("P1.6D fetching {CandleLimit} candles for bot {BotId}, {Symbol}.", CandleLimit, bot.Id, symbol);

        var candles = await GetCandlesWithRetryAsync(
            marketData, broker.BrokerName, symbol, bot.Timeframe, bot.UseFutures, broker.IsTestnet, cancellationToken);

        _logger.LogInformation("P1.6D market data returned {CandleCount} candles for bot {BotId}, {Symbol}.", candles.Count, bot.Id, symbol);

        var closedCandles = candles
            .Where(c => IsCandleClosed(c.Timestamp, bot.Timeframe))
            .OrderBy(c => c.Timestamp)
            .ToList();

        if (closedCandles.Count < 200)
        {
            _logger.LogDebug("Bot {BotId} skipped {Symbol}: only {CandleCount} closed candles are available; 200 are required.", bot.Id, symbol, closedCandles.Count);
            return;
        }

        var latestClosedCandle = closedCandles[^1];
        _logger.LogInformation("P1.6D found {ClosedCandleCount} closed candles for bot {BotId}, {Symbol}. Latest closed candle={CandleTime:O}.", closedCandles.Count, bot.Id, symbol, latestClosedCandle.Timestamp);

        var processKey = BuildProcessKey(bot.Id, symbol, bot.Timeframe);

        lock (_processedCandleLock)
        {
            if (_lastProcessedCandle.TryGetValue(processKey, out var lastProcessed) && latestClosedCandle.Timestamp <= lastProcessed)
            {
                _logger.LogInformation("P1.6D skipped bot {BotId}, {Symbol}: candle {CandleTime:O} was already processed in this process (last={LastProcessed:O}).", bot.Id, symbol, latestClosedCandle.Timestamp, lastProcessed);
                return;
            }
        }

        _logger.LogInformation("P1.6D calculating indicators for bot {BotId}, {Symbol}.", bot.Id, symbol);

        var indicatorInput = closedCandles;
        cancellationToken.ThrowIfCancellationRequested();
        var indicators = await indicatorCalculator.CalculateIndicators(indicatorInput);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("P1.6D generating signal for bot {BotId}, {Symbol}.", bot.Id, symbol);

        // =====================================================================
        // FEATURE #1 & #2: STRATEGY & MACRO BIAS INTEGRATION
        // =====================================================================
        cancellationToken.ThrowIfCancellationRequested();
        var signal = await signalGenerator.GenerateSignal(
            symbol,
            indicatorInput,
            indicators,
            bias15m,
            bias1h);
        cancellationToken.ThrowIfCancellationRequested();

        await _realtime.PublishSignalAsync(bot.UserId, signal, cancellationToken);

        _logger.LogInformation(
            "P1.6D signal generated for bot {BotId}, {Symbol}: Action={Action}, Price={Price}, SL={StopLoss}, TP={TakeProfit}, Confidence={Confidence}.",
            bot.Id, symbol, signal.Action, signal.Price, signal.StopLoss, signal.TakeProfit, signal.Confidence);

        if (signal.Action.Equals("HOLD", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("P1.6D bot {BotId} generated HOLD for {Symbol} on candle {CandleTime:O}. No order will be submitted.", bot.Id, symbol, latestClosedCandle.Timestamp);
            MarkCandleProcessed(processKey, latestClosedCandle.Timestamp);
            return;
        }

        var (resolvedStopLoss, resolvedTakeProfit) = ResolveProtectionLevels(bot, signal);

        if (!IsValidExecutableSignal(signal, bot, resolvedStopLoss, resolvedTakeProfit))
        {
            await WriteAuditAsync(db, bot.UserId, "AutoTradeSignalBlocked",
                $"Bot {bot.Id} generated an invalid executable {signal.Action} signal for {symbol}. " +
                "A valid protective stop-loss and take-profit are required before automated execution for any leg not set to None.",
                cancellationToken);

            _logger.LogWarning("Bot {BotId} signal blocked for {Symbol}: invalid protective levels or signal values.", bot.Id, symbol);
            MarkCandleProcessed(processKey, latestClosedCandle.Timestamp);
            return;
        }

        var spotSellSignal = !bot.UseFutures && signal.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase);

        var activeTrade = await db.Trades
            .Where(t => t.UserId == bot.UserId && t.Symbol == symbol && t.BrokerName == broker.BrokerName &&
                        (t.BotId == bot.Id || t.BotId == null) && ActiveTradeStatuses.Contains(t.Status))
            .OrderByDescending(t => t.EntryTime)
            .FirstOrDefaultAsync(cancellationToken);

        string? skipReason = null;

        if (activeTrade != null)
        {
            var oppositeSignal = !string.Equals(activeTrade.Direction, signal.Action, StringComparison.OrdinalIgnoreCase);

            var ownedByThisBot = activeTrade.BotId == bot.Id;

            if (oppositeSignal && ownedByThisBot && string.Equals(activeTrade.Status, "Open", StringComparison.OrdinalIgnoreCase))
            {
                var closeResult = await brokerService.ClosePositionAsync(bot.UserId, activeTrade.Id, broker, bot.UseFutures);

                await WriteAuditAsync(db, bot.UserId,
                    closeResult.Success ? "AutoTradePositionClosing" : "AutoTradePositionCloseFailed",
                    closeResult.Success
                        ? $"Bot {bot.Id} is closing {symbol} trade {activeTrade.Id} after an opposite {signal.Action} signal. Exit order {closeResult.OrderId ?? "unknown"}."
                        : $"Bot {bot.Id} could not close {symbol} trade {activeTrade.Id}: {closeResult.Error}",
                    cancellationToken);

                await PersistSignalAsync(db, bot.UserId, signal, wasExecuted: closeResult.Success, tradeId: activeTrade.Id, cancellationToken: cancellationToken);

                if (closeResult.Success)
                    await ReconcileUserNowAsync(bot.UserId, cancellationToken);

                MarkCandleProcessed(processKey, latestClosedCandle.Timestamp);
                return;
            }

            skipReason = ownedByThisBot
                ? "a trade for this symbol is still open, so no new position was opened."
                : "a manual trade is open on this symbol, so the bot left it alone and opened nothing.";
        }
        else if (spotSellSignal && !AllowSpotSellWithoutPosition)
        {
            skipReason = "spot SELL ignored: there is no open long position to close.";
        }

        if (skipReason != null)
        {
            await PersistSignalAsync(db, bot.UserId, signal, wasExecuted: false, tradeId: null, cancellationToken: cancellationToken);
            await WriteAuditAsync(db, bot.UserId, "AutoTradeSignalBlocked", $"Bot {bot.Id} skipped {signal.Action} for {symbol}: {skipReason}", cancellationToken);
            MarkCandleProcessed(processKey, latestClosedCandle.Timestamp);
            return;
        }

        var idempotencyKey = BuildIdempotencyKey(bot.Id, symbol, bot.Timeframe, latestClosedCandle.Timestamp);

        await WriteAuditAsync(db, bot.UserId, "AutoTradeExecutionAttempt",
            $"Bot {bot.Id} generated {signal.Action} for {symbol} at {latestClosedCandle.Timestamp:O}. " +
            $"Broker={broker.BrokerName}, Testnet={broker.IsTestnet}, Futures={bot.UseFutures}, IdempotencyKey={idempotencyKey}.",
            cancellationToken);

        var isSpot = !bot.UseFutures;
        var isSpotSell = isSpot && signal.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase);
        var useMarketEntry = !isSpot || broker.BrokerName.Equals("Binance", StringComparison.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();

        var result = await brokerService.PlaceOrderAsync(
            userId: bot.UserId,
            symbol: symbol,
            direction: signal.Action,
            orderType: useMarketEntry ? "Market" : "Limit",
            stopLoss: isSpotSell ? null : resolvedStopLoss,
            takeProfit: isSpotSell ? null : resolvedTakeProfit,
            entryPrice: isSpot ? signal.Price : null,
            useFutures: bot.UseFutures,
            idempotencyKey: idempotencyKey,
            botId: bot.Id,
            connectionId: bot.BrokerConnectionId);

        cancellationToken.ThrowIfCancellationRequested();

        if (bot.User.TelegramChatId.HasValue)
        {
            if (result.Success)
            {
                await _telegram.SendAlertAsync(bot.User.TelegramChatId.Value,
                    $"🟢 New Trade: {signal.Action} {symbol}\n" +
                    $"Bot: {bot.Name}\n" +
                    $"Price: {signal.Price}\n" +
                    $"Order ID: {result.OrderId}");
            }
            else
            {
                await _telegram.SendAlertAsync(bot.User.TelegramChatId.Value,
                    $"🔴 Order Failed: {signal.Action} {symbol}\n" +
                    $"Bot: {bot.Name}\n" +
                    $"Reason: {result.Error}");
            }
        }

        var trade = await db.Trades.FirstOrDefaultAsync(t => t.UserId == bot.UserId && t.IdempotencyKey == idempotencyKey, cancellationToken);

        if (result.Success)
        {
            if (trade != null)
            {
                var persistedSignal = await PersistSignalAsync(db, bot.UserId, signal, wasExecuted: true, tradeId: trade.Id, cancellationToken: cancellationToken);
                await WriteAuditAsync(db, bot.UserId, "AutoTradeExecutionAccepted", $"Bot {bot.Id} execution accepted for {symbol}. ExchangeOrderId={result.OrderId ?? "unknown"}, TradeId={trade.Id}, SignalId={persistedSignal.Id}.", cancellationToken);
            }
            else
            {
                await PersistSignalAsync(db, bot.UserId, signal, wasExecuted: false, tradeId: null, cancellationToken: cancellationToken);
                await WriteAuditAsync(db, bot.UserId, "AutoTradeExecutionPersistenceWarning", $"Bot {bot.Id} received an accepted execution result for {symbol}, but no local trade was found for idempotency key '{idempotencyKey}'. Reconciliation is required.", cancellationToken);
            }

            _logger.LogInformation("Bot {BotId} execution accepted for {Symbol}. ExchangeOrderId={OrderId}.", bot.Id, symbol, result.OrderId);
            MarkCandleProcessed(processKey, latestClosedCandle.Timestamp);
            return;
        }

        await PersistSignalAsync(db, bot.UserId, signal, wasExecuted: false, tradeId: trade?.Id, cancellationToken: cancellationToken);
        await WriteAuditAsync(db, bot.UserId, "AutoTradeExecutionBlocked", $"Bot {bot.Id} execution was not accepted for {symbol}. Reason: {result.Error ?? "Unknown execution error"}. No retry was issued automatically because the BrokerService result may represent a reserved or unresolved exchange request.", cancellationToken);

        _logger.LogWarning("Bot {BotId} execution was not accepted for {Symbol}: {Error}", bot.Id, symbol, result.Error ?? "Unknown execution error");
        MarkCandleProcessed(processKey, latestClosedCandle.Timestamp);
    }

    // =====================================================================
    // FEATURE #2: HELPER METHOD FOR MACRO BIAS
    // =====================================================================
    private string CalculateMacroBias(IReadOnlyList<Candle> candles) 
    {
        if (candles == null || candles.Count < 50) return "Neutral";
        
        var closes = candles.Select(c => (decimal)c.Close).ToList();
        var sma50 = closes.TakeLast(50).Average();
        var currentPrice = closes.Last();

        if (currentPrice > sma50 * 1.01m) return "Uptrend"; 
        if (currentPrice < sma50 * 0.99m) return "Downtrend"; 
        return "Neutral";
    }

    private async Task<IReadOnlyList<Candle>> GetCandlesWithRetryAsync(
        ExchangeMarketDataService marketData,
        string brokerName,
        string symbol,
        string timeframe,
        bool useFutures,
        bool isTestnet,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxMarketDataAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(MarketDataTimeout);

                return await marketData.GetCandlesAsync(brokerName, symbol, timeframe, useFutures, CandleLimit, isTestnet).WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException("Market-data request timed out.");
            }
            catch (Exception ex) when (IsTransientMarketDataException(ex))
            {
                lastException = ex;
            }

            if (attempt == MaxMarketDataAttempts) break;

            var delay = MarketDataRetryDelays[attempt - 1];
            _logger.LogWarning("P1.6E transient market-data failure for {Broker} {Symbol} {Timeframe}. Retry {Attempt}/{MaxAttempts} in {DelaySeconds}s.", brokerName, symbol, timeframe, attempt, MaxMarketDataAttempts, delay.TotalSeconds);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException($"Market-data retrieval failed after {MaxMarketDataAttempts} attempts for {brokerName} {symbol} {timeframe}.", lastException);
    }

    private static bool IsTransientMarketDataException(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TimeoutException || current is HttpRequestException || current is SocketException || current is IOException)
                return true;

            if (current is InvalidOperationException && current.Message.Contains("Unable to get", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void MarkCandleProcessed(string processKey, DateTime candleTimestamp)
    {
        lock (_processedCandleLock) { _lastProcessedCandle[processKey] = candleTimestamp; }
    }

    private static (decimal? StopLoss, decimal? TakeProfit) ResolveProtectionLevels(TradingBot bot, TradingSignal signal)
    {
        var isBuy = signal.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase);

        decimal? stopLoss = bot.StopLossMode switch
        {
            RiskManagementMode.None => null,
            RiskManagementMode.Manual when bot.StopLossPercent is > 0m =>
                isBuy ? signal.Price * (1 - bot.StopLossPercent.Value / 100m) : signal.Price * (1 + bot.StopLossPercent.Value / 100m),
            _ => signal.StopLoss
        };

        decimal? takeProfit = bot.TakeProfitMode switch
        {
            RiskManagementMode.None => null,
            RiskManagementMode.Manual when bot.TakeProfitPercent is > 0m =>
                isBuy ? signal.Price * (1 + bot.TakeProfitPercent.Value / 100m) : signal.Price * (1 - bot.TakeProfitPercent.Value / 100m),
            _ => signal.TakeProfit
        };

        return (stopLoss, takeProfit);
    }

    private static bool IsValidExecutableSignal(TradingSignal signal, TradingBot bot, decimal? resolvedStopLoss, decimal? resolvedTakeProfit)
    {
        if (signal == null || signal.Price <= 0m) return false;

        var requiresStopLoss = bot.StopLossMode != RiskManagementMode.None;
        var requiresTakeProfit = bot.TakeProfitMode != RiskManagementMode.None;

        if (requiresStopLoss && (resolvedStopLoss is null || resolvedStopLoss <= 0m)) return false;
        if (requiresTakeProfit && (resolvedTakeProfit is null || resolvedTakeProfit <= 0m)) return false;

        if (signal.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase))
        {
            if (requiresStopLoss && resolvedStopLoss!.Value >= signal.Price) return false;
            if (requiresTakeProfit && resolvedTakeProfit!.Value <= signal.Price) return false;
            return true;
        }

        if (signal.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
        {
            if (requiresStopLoss && resolvedStopLoss!.Value <= signal.Price) return false;
            if (requiresTakeProfit && resolvedTakeProfit!.Value >= signal.Price) return false;
            return true;
        }

        return false;
    }

    private static async Task<Signal> PersistSignalAsync(ApplicationDbContext db, int userId, TradingSignal signal, bool wasExecuted, int? tradeId, CancellationToken cancellationToken)
    {
        var entity = new Signal
        {
            UserId = userId,
            Symbol = signal.Symbol,
            Action = signal.Action.ToUpperInvariant(),
            Price = signal.Price,
            StopLoss = signal.StopLoss,
            TakeProfit = signal.TakeProfit,
            Confidence = signal.Confidence,
            Pattern = signal.Pattern,
            Rsi = signal.Rsi,
            Trend = signal.Trend,
            Support = signal.Support,
            Resistance = signal.Resistance,
            Indicators = signal.Indicators,
            Reason = signal.Reason,
            GeneratedAt = signal.GeneratedAt,
            WasExecuted = wasExecuted,
            ExecutedAt = wasExecuted ? DateTime.UtcNow : null,
            TradeId = tradeId
        };

        db.Signals.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private static async Task WriteAuditAsync(ApplicationDbContext db, int userId, string action, string details, CancellationToken cancellationToken)
    {
        db.AuditLogs.Add(new AuditLog { UserId = userId, Action = action, Details = details, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MaybeRunReconciliationAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (now - _lastReconciliationAt < ReconciliationInterval) return;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reconciliation = scope.ServiceProvider.GetRequiredService<IReconciliationService>();

        var orphanCutoff = now.AddHours(-1);
        var orphanTrades = await db.Trades
            .Where(t => t.Status == "Pending" && t.EntryTime < orphanCutoff && !db.ExchangeOrders.Any(o => o.TradeId == t.Id))
            .ToListAsync(cancellationToken);

        if (orphanTrades.Count > 0)
        {
            foreach (var orphan in orphanTrades)
            {
                // A crash/redeploy between "reserve" and "exchange answered" can leave a live order
                // on the exchange that we never recorded. Let the recovery job look it up by client
                // order id before releasing the trade; only trades without a key are cancelled outright.
                if (!string.IsNullOrWhiteSpace(orphan.IdempotencyKey))
                {
                    orphan.Status = "PendingReconciliation";
                    orphan.Reason = "No exchange record after 1 hour; checking the exchange by client order id before releasing.";
                }
                else
                {
                    orphan.Status = "Cancelled";
                    orphan.Reason = "Order never reached the exchange; expired automatically.";
                }
                orphan.UpdatedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("Expired {Count} pending trade(s) that had no exchange order.", orphanTrades.Count);
        }

        var unresolvedSubmissionUserIds = await db.Trades
            .AsNoTracking()
            .Where(t => t.Status == "PendingReconciliation" && !db.ExchangeOrders.Any(o => o.TradeId == t.Id))
            .Select(t => t.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (unresolvedSubmissionUserIds.Count > 0)
        {
            var brokerService = scope.ServiceProvider.GetRequiredService<BrokerService>();
            foreach (var unresolvedUserId in unresolvedSubmissionUserIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var recovered = await brokerService.RecoverUnknownSubmissionsAsync(unresolvedUserId, cancellationToken);
                    if (recovered > 0) _logger.LogWarning("Resolved {Count} unknown order submission(s) for user {UserId}.", recovered, unresolvedUserId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogError(ex, "Unknown-submission recovery failed for user {UserId}; other users will continue.", unresolvedUserId); }
            }
        }

        await EscalateUnprotectedTradesAsync(scope.ServiceProvider, db, now, cancellationToken);

        var protectionRetryCutoff = now.AddHours(-24);
        var userIds = await db.ExchangeOrders
            .AsNoTracking()
            .Where(o => o.Status == "Pending" || o.Status == "New" || o.Status == "NEW" || o.Status == "PartiallyFilled" || o.Status == "PendingReconciliation" ||
                       (o.OrderRole == "Entry" && o.Trade.Status == "Open" && o.Trade.EntryTime > protectionRetryCutoff && (o.Trade.ProtectionStatus == "ProtectionPending" || o.Trade.ProtectionStatus == "Pending")))
            .Select(o => o.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var bybitUserIds = await db.Trades.AsNoTracking().Where(t => t.Status == "Open" && t.BrokerName == "Bybit").Select(t => t.UserId).Distinct().ToListAsync(cancellationToken);
        userIds = userIds.Union(bybitUserIds).ToList();

        _lastReconciliationAt = now;

        if (userIds.Count == 0)
        {
            _logger.LogDebug("Reconciliation schedule reached: no pending exchange orders found.");
            return;
        }

        _logger.LogInformation("Starting scheduled reconciliation for {UserCount} user(s).", userIds.Count);

        foreach (var userId in userIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reconciled = await reconciliation.ReconcilePendingOrdersAsync(userId, cancellationToken);
                _logger.LogInformation("Scheduled reconciliation completed for user {UserId}. ReconciledOrders={ReconciledOrders}.", userId, reconciled);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Scheduled reconciliation failed for user {UserId}; other users will continue.", userId); }
        }
    }

    // SL/TP that still cannot be placed after this long is treated as failed: stop retrying,
    // exit at market (a position with no protection is worse than a small loss) and tell the user.
    private static readonly TimeSpan ProtectionGracePeriod = TimeSpan.FromMinutes(15);

    private async Task EscalateUnprotectedTradesAsync(IServiceProvider services, ApplicationDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - ProtectionGracePeriod;

        var stuckTrades = await db.Trades
            .Include(t => t.User)
            .Where(t => t.Status == "Open" && t.ProtectionStatus == "ProtectionPending" && t.EntryTime < cutoff)
            .ToListAsync(cancellationToken);

        if (stuckTrades.Count == 0) return;

        var brokerService = services.GetRequiredService<BrokerService>();

        foreach (var trade in stuckTrades)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var connection = await db.BrokerConnections.FirstOrDefaultAsync(
                    c => c.UserId == trade.UserId && c.BrokerName == trade.BrokerName && c.IsConnected, cancellationToken);

                var isFutures = await db.ExchangeOrders.AsNoTracking()
                    .Where(o => o.TradeId == trade.Id && o.OrderRole == "Entry")
                    .Select(o => o.IsFutures)
                    .FirstOrDefaultAsync(cancellationToken);

                var outcome = "no connected broker was available to exit the position";
                var exited = false;

                if (connection != null)
                {
                    var exit = await brokerService.ClosePositionAsync(trade.UserId, trade.Id, connection, isFutures);
                    exited = exit.Success;
                    outcome = exit.Success ? "a market exit was submitted" : $"the market exit failed: {exit.Error}";
                }

                // Stop the retry loop; the user has been told and can act manually.
                if (!exited && string.Equals(trade.Status, "Open", StringComparison.OrdinalIgnoreCase))
                {
                    trade.ProtectionStatus = "ProtectionFailed";
                    trade.UpdatedAt = now;
                }

                await WriteAuditAsync(db, trade.UserId, "ProtectionEscalated",
                    $"Trade {trade.Id} ({trade.Symbol}) still had no SL/TP after {ProtectionGracePeriod.TotalMinutes:F0} minutes; {outcome}.",
                    cancellationToken);

                _logger.LogWarning("Trade {TradeId} ({Symbol}) had no protection after {Minutes} min: {Outcome}.",
                    trade.Id, trade.Symbol, ProtectionGracePeriod.TotalMinutes, outcome);

                if (trade.User?.TelegramChatId is long chatId)
                {
                    await _telegram.SendAlertAsync(chatId,
                        $"⚠️ {trade.Symbol}: your stop-loss/take-profit could not be placed for {ProtectionGracePeriod.TotalMinutes:F0} minutes, so {outcome}. Please check your open positions.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Escalation failed for trade {TradeId}; other trades will continue.", trade.Id);
            }
        }
    }

    private static string BuildProcessKey(int botId, string symbol, string timeframe) => $"{botId}:{symbol.Trim().ToUpperInvariant()}:{timeframe.Trim().ToLowerInvariant()}";

    private static string BuildIdempotencyKey(int botId, string symbol, string timeframe, DateTime candleTimestamp) =>
        $"auto:{botId}:{symbol.Trim().ToUpperInvariant()}:{timeframe.Trim().ToLowerInvariant()}:{candleTimestamp.ToUniversalTime():yyyyMMddHHmmss}";

    private async Task ReconcileUserNowAsync(int userId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var reconciliation = scope.ServiceProvider.GetRequiredService<IReconciliationService>();
            await reconciliation.ReconcilePendingOrdersAsync(userId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Immediate reconciliation after closing a position failed for user {UserId}.", userId); }
    }

    private static bool IsCandleClosed(DateTime candleOpenTime, string timeframe)
    {
        var duration = GetTimeframeDuration(timeframe);
        return candleOpenTime.ToUniversalTime().Add(duration) <= DateTime.UtcNow;
    }

    private static TimeSpan GetTimeframeDuration(string timeframe) =>
        timeframe.Trim().ToLowerInvariant() switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "3m" => TimeSpan.FromMinutes(3),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "30m" => TimeSpan.FromMinutes(30),
            "1h" => TimeSpan.FromHours(1),
            "2h" => TimeSpan.FromHours(2),
            "4h" => TimeSpan.FromHours(4),
            "6h" => TimeSpan.FromHours(6),
            "8h" => TimeSpan.FromHours(8),
            "12h" => TimeSpan.FromHours(12),
            "1d" => TimeSpan.FromDays(1),
            "3d" => TimeSpan.FromDays(3),
            "1w" => TimeSpan.FromDays(7),
            _ => throw new ArgumentException($"Unsupported timeframe '{timeframe}'.", nameof(timeframe))
        };

    public static BotEligibilityResult EvaluateEligibility(TradingBot bot)
    {
        if (bot.User == null) return BotEligibilityResult.Blocked("user record is unavailable");
        if (!bot.User.IsActive) return BotEligibilityResult.Blocked("user account is inactive");
        if (!bot.User.IsAutoTradeEnabled) return BotEligibilityResult.Blocked("user-level auto-trading is disabled");
        if (!bot.AutoTradeEnabled) return BotEligibilityResult.Blocked("bot-level auto-trading is disabled");

       var subscription = bot.User.Subscription;
if (bot.User.Role != "Admin" && (subscription == null || !subscription.IsActive || subscription.EndDate <= DateTime.UtcNow)) 
    return BotEligibilityResult.Blocked("subscription is inactive or expired");

        var riskSetting = bot.User.RiskSetting;
        if (riskSetting == null) return BotEligibilityResult.Blocked("risk settings are not configured");
        if (!IsSupportedRiskLevel(riskSetting.RiskLevel)) return BotEligibilityResult.Blocked($"unsupported risk level '{riskSetting.RiskLevel}'");
        if (riskSetting.IsEmergencyKillSwitch) return BotEligibilityResult.Blocked("emergency kill switch is active");

        if (bot.BrokerConnection == null) return BotEligibilityResult.Blocked("no broker connection is assigned");
        var broker = bot.BrokerConnection;

        if (!broker.IsActive) return BotEligibilityResult.Blocked("assigned broker connection is inactive");
        if (!broker.IsConnected) return BotEligibilityResult.Blocked("assigned broker connection is not connected");
        if (!IsSupportedBroker(broker.BrokerName)) return BotEligibilityResult.Blocked($"unsupported broker '{broker.BrokerName}'");
        if (!broker.IsTestnet && !broker.IsLiveTradingEnabled) return BotEligibilityResult.Blocked("live trading is not explicitly enabled for the assigned broker connection");

        if (bot.TrackedSymbols == null || bot.TrackedSymbols.Count == 0) return BotEligibilityResult.Blocked("bot has no tracked symbols assigned");

        var enabledSymbols = bot.TrackedSymbols.Where(x => x.TrackedSymbol != null && x.TrackedSymbol.IsEnabled).Select(x => x.TrackedSymbol!).ToList();
        if (enabledSymbols.Count == 0) return BotEligibilityResult.Blocked("bot has no enabled tracked symbols");

        var mismatchedSymbol = enabledSymbols.FirstOrDefault(symbol => !symbol.Exchange.Equals(broker.BrokerName, StringComparison.OrdinalIgnoreCase));
        if (mismatchedSymbol != null) return BotEligibilityResult.Blocked($"tracked symbol '{mismatchedSymbol.Symbol}' belongs to exchange '{mismatchedSymbol.Exchange}', not broker '{broker.BrokerName}'");

        return BotEligibilityResult.Allowed(enabledSymbols.Select(x => x.Symbol).ToArray());
    }

    private static bool IsSupportedRiskLevel(string? riskLevel) =>
        riskLevel != null && (riskLevel.Equals("Conservative", StringComparison.OrdinalIgnoreCase) || riskLevel.Equals("Balanced", StringComparison.OrdinalIgnoreCase) || riskLevel.Equals("Aggressive", StringComparison.OrdinalIgnoreCase));

    private static bool IsSupportedBroker(string brokerName) =>
        brokerName.Equals("Binance", StringComparison.OrdinalIgnoreCase) || brokerName.Equals("Bybit", StringComparison.OrdinalIgnoreCase);

    public sealed record BotEligibilityResult(bool IsEligible, string Reason, IReadOnlyList<string> EnabledSymbols)
    {
        public static BotEligibilityResult Allowed(IReadOnlyList<string> symbols) => new(true, string.Empty, symbols);
        public static BotEligibilityResult Blocked(string reason) => new(false, reason, Array.Empty<string>());
    }
} 