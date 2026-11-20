using System.ComponentModel.DataAnnotations;

namespace TradingBotEngine.API.DTOs.Broker;

public sealed class ConnectBrokerRequestDto
{
    [Required]
    [RegularExpression(
    "^(?i:binance|bybit)$",
    ErrorMessage = "Broker must be Binance or Bybit.")]
    
       public string Broker { get; set; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 8)]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 8)]
    public string ApiSecret { get; set; } = string.Empty;

     public bool UseFutures { get; set; } = false; 
}

public sealed class DisconnectBrokerRequestDto
{
    public int? ConnectionId { get; set; }
}

public sealed class LiveTradingRequestDto
{
    public bool Enabled { get; set; }

    public bool Confirm { get; set; }
}

public sealed class BrokerConnectionResponseDto
{
    public int Id { get; set; }

    public string BrokerName { get; set; } = string.Empty;

    public bool IsConnected { get; set; }

    public bool IsTestnet { get; set; }

    public bool IsLiveTradingEnabled { get; set; }

    public DateTime? LastConnectedAt { get; set; }

    public DateTime? LastDisconnectedAt { get; set; }
}

public sealed class ConnectBrokerResponseDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool IsTestnet { get; set; }

    public int? ConnectionId { get; set; }
}

public sealed class BrokerActionResponseDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class LiveTradingResponseDto
{
    public bool Success { get; set; }

    public bool IsLiveTradingEnabled { get; set; }

    public bool IsTestnet { get; set; }
}