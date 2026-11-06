using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TradingBotEngine.API.DTOs.Trading;
using TradingBotEngine.Core;
using TradingBotEngine.Core.Models;
using TradingBotEngine.Data;
using TradingBotEngine.Data.Models;
using TradingBotEngine.Services;

namespace TradingBotEngine.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class TradingController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly BrokerService _brokerService;
    private readonly ExchangeMarketDataService _marketData;
    private readonly IndicatorCalculator _indicatorCalculator;
    private readonly SignalGenerator _signalGenerator;

    public TradingController(
        ApplicationDbContext context,
        BrokerService brokerService,
        ExchangeMarketDataService marketData,
        IndicatorCalculator indicatorCalculator,
        SignalGenerator signalGenerator)
    {
        _context = context;
        _brokerService = brokerService;
        _marketData = marketData;
        _indicatorCalculator = indicatorCalculator;
        _signalGenerator = signalGenerator;
    }

    [HttpGet("balance")]
    public async Task<IActionResult> GetBalance(
        [FromQuery] string currency = "USDT",
        [FromQuery] bool useFutures = false,
        [FromQuery] int? connectionId = null)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        currency = currency.Trim().ToUpperInvariant();

        if (currency.Length is < 2 or > 10)
            return BadRequest(new { message = "Currency is invalid." });

        var connected = connectionId.HasValue
            ? (await _brokerService.GetConnectionStatusAsync(userId, connectionId.Value))?.IsConnected == true
            : await _brokerService.IsConnectedAsync(userId);

        if (!connected)
        {
            return Conflict(new
            {
                message = "No active broker connection is available."
            });
        }

        var balance = await _brokerService.GetBalanceAsync(
            userId,
            currency,
            useFutures,
            connectionId);

        return Ok(new BalanceResponseDto
        {
            Balance = balance,
            Currency = currency,
            UseFutures = useFutures
        });
    }

    [HttpGet("signal")]
    public async Task<IActionResult> GetSignal(
        [FromQuery] string? symbol = null)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        symbol = string.IsNullOrWhiteSpace(symbol)
            ? null
            : symbol.Trim().ToUpperInvariant();

        var query = _context.Signals
            .AsNoTracking()
            .Where(s => s.UserId == userId);

        if (symbol != null)
            query = query.Where(s => s.Symbol == symbol);

        var signal = await query
            .OrderByDescending(s => s.GeneratedAt)
            .FirstOrDefaultAsync();

        if (signal == null)
            return Ok(null);

        return Ok(new SignalResponseDto
        {
            Id = signal.Id,
            Symbol = signal.Symbol,
            Action = signal.Action,
            Price = signal.Price,
            StopLoss = signal.StopLoss,
            TakeProfit = signal.TakeProfit,
            Confidence = signal.Confidence,
            Pattern = signal.Pattern,
            Rsi = signal.Rsi,
            Trend = signal.Trend,
            Support = signal.Support,
            Resistance = signal.Resistance,
            Reason = signal.Reason,
            GeneratedAt = signal.GeneratedAt,
            WasExecuted = signal.WasExecuted
        });
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeSignal([FromBody] AnalyzeRequestDto request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        var connection = await _context.BrokerConnections
            .FirstOrDefaultAsync(b => b.UserId == userId && b.IsActive && b.IsConnected && b.Id == request.ConnectionId);

        if (connection == null)
            return BadRequest(new { message = "Active broker connection not found." });

        try
        {
            // 1. Fetch HTF for Macro Bias
            var htf15m = await _marketData.GetCandlesAsync(connection.BrokerName, request.Symbol, "15m", request.UseFutures, 250, connection.IsTestnet);
            var htf1h = await _marketData.GetCandlesAsync(connection.BrokerName, request.Symbol, "1h", request.UseFutures, 250, connection.IsTestnet);
            
            var bias15m = CalculateMacroBias(htf15m);
            var bias1h = CalculateMacroBias(htf1h);

            // 2. Fetch Primary Timeframe
            var candles = await _marketData.GetCandlesAsync(connection.BrokerName, request.Symbol, request.Timeframe, request.UseFutures, 250, connection.IsTestnet);

            if (candles.Count < 200)
                return BadRequest(new { message = "Not enough market data to analyze." });

            // 3. Calculate Indicators & Generate Signal
            var indicators = await _indicatorCalculator.CalculateIndicators(candles.ToList());
            var signal = await _signalGenerator.GenerateSignal(request.Symbol, candles.ToList(), indicators, bias15m, bias1h);

            return Ok(new
            {
                action = signal.Action,
                confidence = signal.Confidence,
                price = signal.Price,
                stopLoss = signal.StopLoss,
                takeProfit = signal.TakeProfit,
                reason = signal.Reason,
                trend = signal.Trend,
                rsi = signal.Rsi,
                bias15m = bias15m,
                bias1h = bias1h
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Analysis failed: {ex.Message}" });
        }
    }

    [HttpGet("tracked-symbols")]
    public async Task<IActionResult> GetTrackedSymbols()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        var symbols = await _context.TrackedSymbols
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Exchange)
            .ThenBy(s => s.Symbol)
            .Select(s => new TrackedSymbolResponseDto
            {
                Id = s.Id,
                Symbol = s.Symbol,
                Exchange = s.Exchange,
                IsEnabled = s.IsEnabled,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt
            })
            .ToListAsync();

        return Ok(symbols);
    }

    [HttpPost("tracked-symbols")]
    public async Task<IActionResult> AddTrackedSymbol(
        [FromBody] TrackedSymbolRequestDto request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var exchange = request.Exchange.Trim();

        if (exchange.Equals("Binance", StringComparison.OrdinalIgnoreCase))
            exchange = "Binance";
        else if (exchange.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
            exchange = "Bybit";
        else
            return BadRequest(new { message = "Exchange must be Binance or Bybit." });

        const int maxTrackedSymbols = 50;
        var trackedSymbolCount = await _context.TrackedSymbols
            .CountAsync(s => s.UserId == userId);

        if (trackedSymbolCount >= maxTrackedSymbols)
        {
            return BadRequest(new
            {
                message = $"A user may track at most {maxTrackedSymbols} symbols."
            });
        }

        var exists = await _context.TrackedSymbols
            .AnyAsync(s =>
                s.UserId == userId &&
                s.Exchange == exchange &&
                s.Symbol == symbol);

        if (exists)
        {
            return Conflict(new
            {
                message = $"The symbol '{symbol}' is already configured for {exchange}."
            });
        }

        var now = DateTime.UtcNow;
        var trackedSymbol = new TrackedSymbol
        {
            UserId = userId,
            Symbol = symbol,
            Exchange = exchange,
            IsEnabled = request.IsEnabled,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.TrackedSymbols.Add(trackedSymbol);
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "TrackedSymbolAdded",
            Details = $"Tracked symbol '{symbol}' added for {exchange}. Enabled={request.IsEnabled}.",
            CreatedAt = now
        });

        await _context.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetTrackedSymbols),
            null,
            new TrackedSymbolResponseDto
            {
                Id = trackedSymbol.Id,
                Symbol = trackedSymbol.Symbol,
                Exchange = trackedSymbol.Exchange,
                IsEnabled = trackedSymbol.IsEnabled,
                CreatedAt = trackedSymbol.CreatedAt,
                UpdatedAt = trackedSymbol.UpdatedAt
            });
    }

    [HttpPut("tracked-symbols/{id:int}")]
    public async Task<IActionResult> UpdateTrackedSymbol(
        int id,
        [FromBody] TrackedSymbolRequestDto request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var trackedSymbol = await _context.TrackedSymbols
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

        if (trackedSymbol == null)
            return NotFound(new { message = "Tracked symbol not found." });

        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var exchange = request.Exchange.Trim();

        if (exchange.Equals("Binance", StringComparison.OrdinalIgnoreCase))
            exchange = "Binance";
        else if (exchange.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
            exchange = "Bybit";
        else
            return BadRequest(new { message = "Exchange must be Binance or Bybit." });

        var duplicate = await _context.TrackedSymbols
            .AnyAsync(s =>
                s.Id != id &&
                s.UserId == userId &&
                s.Exchange == exchange &&
                s.Symbol == symbol);

        if (duplicate)
        {
            return Conflict(new
            {
                message = $"The symbol '{symbol}' is already configured for {exchange}."
            });
        }

        var now = DateTime.UtcNow;
        trackedSymbol.Symbol = symbol;
        trackedSymbol.Exchange = exchange;
        trackedSymbol.IsEnabled = request.IsEnabled;
        trackedSymbol.UpdatedAt = now;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "TrackedSymbolUpdated",
            Details = $"Tracked symbol '{symbol}' updated for {exchange}. Enabled={request.IsEnabled}.",
            CreatedAt = now
        });

        await _context.SaveChangesAsync();

        return Ok(new TrackedSymbolResponseDto
        {
            Id = trackedSymbol.Id,
            Symbol = trackedSymbol.Symbol,
            Exchange = trackedSymbol.Exchange,
            IsEnabled = trackedSymbol.IsEnabled,
            CreatedAt = trackedSymbol.CreatedAt,
            UpdatedAt = trackedSymbol.UpdatedAt
        });
    }

    [HttpDelete("tracked-symbols/{id:int}")]
    public async Task<IActionResult> DeleteTrackedSymbol(int id)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        var trackedSymbol = await _context.TrackedSymbols
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

        if (trackedSymbol == null)
            return NotFound(new { message = "Tracked symbol not found." });

        var now = DateTime.UtcNow;
        _context.TrackedSymbols.Remove(trackedSymbol);
        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "TrackedSymbolDeleted",
            Details = $"Tracked symbol '{trackedSymbol.Symbol}' deleted for {trackedSymbol.Exchange}.",
            CreatedAt = now
        });

        await _context.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "Tracked symbol deleted."
        });
    }

    [HttpPost("place-order")]
    [EnableRateLimiting("trading")]
    public async Task<IActionResult> PlaceOrder(
        [FromBody] PlaceOrderRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var user = await _context.Users
            .Include(u => u.Subscription)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            return NotFound(new { message = "User not found." });

        if (!user.IsActive)
            return BadRequest(new { message = "Account is deactivated." });

        if (!SubscriptionGuard.HasActiveSubscription(user))
            return BadRequest(new { message = SubscriptionGuard.ExpiredMessage });

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(new
            {
                message =
                    "The Idempotency-Key header is required for every order."
            });
        }

        idempotencyKey = idempotencyKey.Trim();

        if (request.ConnectionId <= 0)
        {
            return BadRequest(new { message = "A valid broker connectionId is required." });
        }

        if (idempotencyKey.Length > 100)
        {
            return BadRequest(new
            {
                message =
                    "The Idempotency-Key header must not exceed 100 characters."
            });
        }

        var result = await _brokerService.PlaceOrderAsync(
            userId,
            request.Symbol,
            request.Direction,
            request.OrderType,
            request.StopLoss,
            request.TakeProfit,
            request.EntryPrice,
            request.UseFutures,
            idempotencyKey,
            request.ConnectionId);

        if (!result.Success)
        {
            return BadRequest(new PlaceOrderResponseDto
            {
                Success = false,
                Message =
                    result.Error ??
                    "The order could not be placed."
            });
        }

        return Ok(new PlaceOrderResponseDto
        {
            Success = true,
            Message = "Order submitted successfully.",
            OrderId = result.OrderId,
            Price = result.Price
        });
    }

    [HttpPost("cancel-order")]
    public async Task<IActionResult> CancelOrder(
        [FromBody] CancelOrderRequestDto request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var success = await _brokerService.CancelOrderAsync(
            userId,
            request.Symbol.Trim().ToUpperInvariant(),
            request.OrderId.Trim(),
            request.UseFutures);

        if (!success)
        {
            return BadRequest(new TradingActionResponseDto
            {
                Success = false,
                Message = "The order could not be cancelled."
            });
        }

        var exchangeOrder = await _context.ExchangeOrders
            .FirstOrDefaultAsync(o =>
                o.UserId == userId &&
                o.ExchangeOrderId == request.OrderId.Trim());

        if (exchangeOrder != null)
        {
            exchangeOrder.Status = "CANCELLED";
            exchangeOrder.UpdatedAt = DateTime.UtcNow;

            var trade = await _context.Trades
                .FirstOrDefaultAsync(t =>
                    t.Id == exchangeOrder.TradeId &&
                    t.UserId == userId);

            if (trade != null &&
                (trade.Status == "Pending" ||
                 trade.Status == "Open"))
            {
                trade.Status = "Cancelled";
                trade.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
        }

        return Ok(new TradingActionResponseDto
        {
            Success = true,
            Message = "Order cancelled successfully."
        });
    }

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        [FromQuery] int limit = 100)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        limit = Math.Clamp(limit, 1, 200);

        var orders = await _context.ExchangeOrders
            .AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit)
            .Select(o => new ExchangeOrderResponseDto
            {
                Id = o.Id,
                TradeId = o.TradeId,
                BrokerName = o.BrokerName,
                ExchangeOrderId = o.ExchangeOrderId,
                Symbol = o.Symbol,
                Side = o.Side,
                OrderType = o.OrderType,
                RequestedQuantity = o.RequestedQuantity,
                FilledQuantity = o.FilledQuantity,
                AverageFillPrice = o.AverageFillPrice,
                Status = o.Status,
                IsTestnet = o.IsTestnet,
                IsFutures = o.IsFutures,
                FailureReason = o.FailureReason,
                CreatedAt = o.CreatedAt,
                UpdatedAt = o.UpdatedAt,
                LastReconciledAt = o.LastReconciledAt
            })
            .ToListAsync();

        return Ok(orders);
    }

    [HttpGet("trade-history")]
    public async Task<IActionResult> GetTradeHistory(
        [FromQuery] int limit = 100)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        limit = Math.Clamp(limit, 1, 200);

        var trades = await _context.Trades
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.EntryTime)
            .Take(limit)
            .Select(t => new TradeResponseDto
            {
                Id = t.Id,
                BotId = t.BotId,
                Symbol = t.Symbol,
                Direction = t.Direction,
                EntryPrice = t.EntryPrice,
                ExitPrice = t.ExitPrice,
                Quantity = t.Quantity,
                StopLoss = t.StopLoss,
                TakeProfit = t.TakeProfit,
                EntryTime = t.EntryTime,
                ExitTime = t.ExitTime,
                ProfitLoss = t.ProfitLoss,
                ProfitLossPercentage = t.ProfitLossPercentage,
                Status = t.Status,
                Reason = t.Reason,
                BrokerName = t.BrokerName,
                OrderId = t.OrderId,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt
            })
            .ToListAsync();

        return Ok(trades);
    }

    [HttpGet("positions")]
    public async Task<IActionResult> GetPositions()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        var positions = await _context.Positions
            .AsNoTracking()
            .Where(p =>
                p.UserId == userId &&
                p.Status == "Open")
            .OrderByDescending(p => p.OpenedAt)
            .Select(p => new PositionResponseDto
            {
                Id = p.Id,
                Symbol = p.Symbol,
                Direction = p.Direction,
                EntryPrice = p.EntryPrice,
                CurrentPrice = p.CurrentPrice,
                Quantity = p.Quantity,
                StopLoss = p.StopLoss,
                TakeProfit = p.TakeProfit,
                UnrealizedPnL = p.UnrealizedPnL,
                UnrealizedPnLPercentage = p.UnrealizedPnLPercentage,
                Status = p.Status,
                OpenedAt = p.OpenedAt,
                ClosedAt = p.ClosedAt
            })
            .ToListAsync();

        return Ok(positions);
    }

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

    private bool TryGetUserId(out int userId)
    {
        return int.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            out userId);
    }
}   