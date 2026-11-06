using System.ComponentModel.DataAnnotations;

namespace TradingBotEngine.API.DTOs.User;

public sealed class UserProfileResponseDto
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsEmailVerified { get; set; }
    public bool IsAutoTradeEnabled { get; set; }
    public DateTime? SubscriptionEnd { get; set; }
    public bool IsTrial { get; set; }
    public bool IsSubscriptionActive { get; set; }
    public long? TelegramChatId { get; set; }
    public string? TelegramUsername { get; set; }
}

public sealed class ToggleAutoTradeRequestDto
{
    public bool IsAutoTradeEnabled { get; set; }
}

public sealed class AutoTradeResponseDto
{
    public bool Success { get; set; }
    public bool IsAutoTradeEnabled { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class AutoTradeStatusResponseDto
{
    public bool IsAutoTradeEnabled { get; set; }
}

public sealed class EmergencyKillSwitchRequestDto
{
    public bool Enabled { get; set; }
}

public sealed class EmergencyKillSwitchResponseDto
{
    public bool IsEmergencyKillSwitch { get; set; }
    public DateTime? EmergencyKillSwitchActivatedAt { get; set; }
}

public sealed class LinkTelegramRequestDto
{
    public long ChatId { get; set; }
    public string? Username { get; set; }
}

public sealed class RiskSettingResponseDto
{
    public string RiskLevel { get; set; } = "Balanced";
    public decimal RiskPerTrade { get; set; }
    public int MaxOpenTrades { get; set; }
    public decimal DailyLossLimit { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal Leverage { get; set; }
}

public sealed class UpdateRiskLevelRequestDto
{
    [Required]
    [RegularExpression("^(Conservative|Balanced|Aggressive)$", ErrorMessage = "Invalid risk level.")]
    public string RiskLevel { get; set; } = "Balanced";
}