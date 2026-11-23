using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TradingBotEngine.API.DTOs.User;
using TradingBotEngine.Data;
using TradingBotEngine.Data.Models;
using TradingBotEngine.Services;

namespace TradingBotEngine.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly TelegramBotService _telegram;

    public UserController(ApplicationDbContext context, TelegramBotService telegram)
    {
        _context = context;
        _telegram = telegram;
    }

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        var user = await _context.Users
            .Include(u => u.Subscription)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null) return NotFound(new { message = "User not found." });

        // FIXED: Use the SubscriptionGuard so Admins always show as active on the frontend
        var subscriptionActive = SubscriptionGuard.HasActiveSubscription(user);

        return Ok(new UserProfileResponseDto
        {
            Id = user.Id,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Role = user.Role,
            IsActive = user.IsActive,
            IsEmailVerified = user.IsEmailVerified,
            IsAutoTradeEnabled = user.IsAutoTradeEnabled,
            SubscriptionEnd = user.Subscription?.EndDate,
            IsTrial = user.Subscription?.IsTrial ?? false,
            IsSubscriptionActive = subscriptionActive,
            TelegramChatId = user.TelegramChatId,
            TelegramUsername = user.TelegramUsername
        });
    }

    [HttpPut("auto-trade")]
    public async Task<IActionResult> ToggleAutoTrade([FromBody] ToggleAutoTradeRequestDto request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Invalid user identity." });

        var user = await _context.Users.Include(u => u.Subscription).FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return NotFound(new { message = "User not found." });
        if (!user.IsActive) return BadRequest(new { message = "Account is deactivated." });

        // FIXED: Replaced the manual date check with SubscriptionGuard so Admins can always toggle this
        if (request.IsAutoTradeEnabled && !SubscriptionGuard.HasActiveSubscription(user))
            return BadRequest(new { message = "An active subscription is required before enabling auto-trade." });

        user.IsAutoTradeEnabled = request.IsAutoTradeEnabled;
        await _context.SaveChangesAsync();

        return Ok(new AutoTradeResponseDto
        {
            Success = true,
            IsAutoTradeEnabled = user.IsAutoTradeEnabled,
            Message = $"Auto-trade {(user.IsAutoTradeEnabled ? "enabled" : "disabled")}"
        });
    }

    [HttpGet("auto-trade-status")]
    public async Task<IActionResult> GetAutoTradeStatus()
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(new { message = "Invalid user identity." });
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return NotFound(new { message = "User not found." });
        return Ok(new AutoTradeStatusResponseDto { IsAutoTradeEnabled = user.IsAutoTradeEnabled });
    }

    [HttpGet("emergency-kill-switch")]
    public async Task<IActionResult> GetEmergencyKillSwitch()
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(new { message = "Invalid user identity." });
        var riskSetting = await _context.RiskSettings.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId);
        return Ok(new { enabled = riskSetting?.IsEmergencyKillSwitch ?? false, activatedAt = riskSetting?.EmergencyKillSwitchActivatedAt });
    }

    [HttpPut("emergency-kill-switch")]
    public async Task<IActionResult> SetEmergencyKillSwitch([FromBody] EmergencyKillSwitchRequestDto request)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(new { message = "Invalid user identity." });
        var riskSetting = await _context.RiskSettings.FirstOrDefaultAsync(r => r.UserId == userId);
        if (riskSetting == null) return NotFound(new { message = "Risk settings are not configured for this user." });

        var now = DateTime.UtcNow;
        riskSetting.IsEmergencyKillSwitch = request.Enabled;
        riskSetting.EmergencyKillSwitchActivatedAt = request.Enabled ? now : null;
        riskSetting.UpdatedAt = now;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = request.Enabled ? "EmergencyKillSwitchActivated" : "EmergencyKillSwitchDeactivated",
            Details = request.Enabled ? "Emergency kill switch activated. New trading orders are blocked." : "Emergency kill switch deactivated.",
            CreatedAt = now
        });

        await _context.SaveChangesAsync();

        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user?.TelegramChatId != null)
        {
            string alertMsg = request.Enabled ? "🚨 EMERGENCY KILL SWITCH ACTIVATED. All new orders are blocked." : "✅ Emergency Kill Switch deactivated. Trading has resumed.";
            await _telegram.SendAlertAsync(user.TelegramChatId.Value, alertMsg);
        }

        return Ok(new EmergencyKillSwitchResponseDto
        {
            IsEmergencyKillSwitch = riskSetting.IsEmergencyKillSwitch,
            EmergencyKillSwitchActivatedAt = riskSetting.EmergencyKillSwitchActivatedAt
        });
    }

    // ---- New Risk Level Endpoints ----

    [HttpGet("risk-settings")]
    public async Task<IActionResult> GetRiskSettings()
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(new { message = "Invalid user identity." });
        
        var risk = await _context.RiskSettings.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId);
        if (risk == null) return NotFound(new { message = "Risk settings not found." });

        return Ok(new RiskSettingResponseDto
        {
            RiskLevel = risk.RiskLevel,
            RiskPerTrade = risk.RiskPerTrade,
            MaxOpenTrades = risk.MaxOpenTrades,
            DailyLossLimit = risk.DailyLossLimit,
            MaxDrawdown = risk.MaxDrawdown,
            Leverage = risk.Leverage
        });
    }

    [HttpPut("risk-settings")]
    public async Task<IActionResult> UpdateRiskSettings([FromBody] UpdateRiskLevelRequestDto request)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(new { message = "Invalid user identity." });

        var risk = await _context.RiskSettings.FirstOrDefaultAsync(r => r.UserId == userId);
        if (risk == null) return NotFound(new { message = "Risk settings not found." });

        // Map the string level to the actual numbers
        switch (request.RiskLevel)
        {
            case "Conservative":
                risk.RiskPerTrade = 0.5m;
                risk.MaxOpenTrades = 2;
                break;
            case "Balanced":
                risk.RiskPerTrade = 1.0m;
                risk.MaxOpenTrades = 5;
                break;
            case "Aggressive":
                risk.RiskPerTrade = 2.0m;
                risk.MaxOpenTrades = 10;
                break;
        }

        risk.RiskLevel = request.RiskLevel;
        risk.UpdatedAt = DateTime.UtcNow;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "RiskLevelUpdated",
            Details = $"User changed risk level to {request.RiskLevel} (Risk: {risk.RiskPerTrade}%, Max Trades: {risk.MaxOpenTrades}).",
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        return Ok(new RiskSettingResponseDto
        {
            RiskLevel = risk.RiskLevel,
            RiskPerTrade = risk.RiskPerTrade,
            MaxOpenTrades = risk.MaxOpenTrades,
            DailyLossLimit = risk.DailyLossLimit,
            MaxDrawdown = risk.MaxDrawdown,
            Leverage = risk.Leverage
        });
    }

    [HttpPut("telegram")]
    public async Task<IActionResult> LinkTelegram([FromBody] LinkTelegramRequestDto request)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(new { message = "Invalid user identity." });
        if (request.ChatId <= 0) return BadRequest(new { message = "Invalid Chat ID." });

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "User not found." });

        var existing = await _context.Users.AnyAsync(u => u.TelegramChatId == request.ChatId && u.Id != userId);
        if (existing) return Conflict(new { message = "This Telegram account is already linked to another user." });

        user.TelegramChatId = request.ChatId;
        user.TelegramUsername = request.Username;
        await _context.SaveChangesAsync();

        return Ok(new { success = true, message = "Telegram linked successfully." });
    }

    [HttpDelete("telegram")]
    public async Task<IActionResult> UnlinkTelegram()
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(new { message = "Invalid user identity." });
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "User not found." });

        user.TelegramChatId = null;
        user.TelegramUsername = null;
        await _context.SaveChangesAsync();

        return Ok(new { success = true, message = "Telegram disconnected." });
    }

    private bool TryGetUserId(out int userId)
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }
}      