using System.ComponentModel.DataAnnotations;

namespace TradingBotEngine.API.DTOs.Trading;

public sealed class BalanceResponseDto
{
    public decimal Balance { get; set; }
    public string Currency { get; set; } = "USDT";
    public bool UseFutures { get; set; }
}

public sealed class SignalResponseDto
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal Confidence { get; set; }
    public string? Pattern { get; set; }
    public decimal? Rsi { get; set; }
    public string? Trend { get; set; }
    public decimal? Support { get; set; }
    public decimal? Resistance { get; set; }
    public string? Reason { get; set; }
    public DateTime GeneratedAt { get; set; }
    public bool WasExecuted { get; set; }
}

public sealed class AnalyzeRequestDto
{
    [Required]
    public string Symbol { get; set; } = string.Empty;
    
    [Required]
    public string Timeframe { get; set; } = "15m";
    
    public bool UseFutures { get; set; } = false;
    
    public int ConnectionId { get; set; }
}

public sealed class PlaceOrderRequestDto
{
    [Required]
    [StringLength(30, MinimumLength = 1)]
    public string Symbol { get; set; } = string.Empty;

    public int ConnectionId { get; set; }

    [Required]
    [RegularExpression(
        "^(BUY|SELL)$",
        ErrorMessage = "Direction must be BUY or SELL.")]
    public string Direction { get; set; } = string.Empty;

    [RegularExpression(
        "^(Market|Limit)$",
        ErrorMessage = "OrderType must be Market or Limit.")]
    public string OrderType { get; set; } = "Market";

    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal? EntryPrice { get; set; }
    public bool UseFutures { get; set; }
}

public sealed class PlaceOrderResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? OrderId { get; set; }
    public decimal? Price { get; set; }
}

public sealed class CancelOrderRequestDto
{
    [Required]
    [StringLength(30, MinimumLength = 1)]
    public string Symbol { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string OrderId { get; set; } = string.Empty;

    public bool UseFutures { get; set; }
}

public sealed class TradingActionResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class ExchangeOrderResponseDto
{
    public int Id { get; set; }
    public int TradeId { get; set; }
    public string BrokerName { get; set; } = string.Empty;
    public string ExchangeOrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public decimal RequestedQuantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal AverageFillPrice { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsTestnet { get; set; }
    public bool IsFutures { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastReconciledAt { get; set; }
}

public sealed class TradeResponseDto
{
    public int Id { get; set; }
    public int? BotId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public decimal? ProfitLoss { get; set; }
    public decimal? ProfitLossPercentage { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? BrokerName { get; set; }
    public string? OrderId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class PositionResponseDto
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal UnrealizedPnL { get; set; }
    public decimal UnrealizedPnLPercentage { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}