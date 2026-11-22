using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TradingBotEngine.Data;
using TradingBotEngine.Data.Models;
using TradingBotEngine.Services;
using Xunit;

namespace TradingBotEngine.Tests;

public class BrokerServiceMaxDrawdownTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }

    private sealed class TestBrokerService : BrokerService
    {
        public TestBrokerService(ApplicationDbContext context)
            : base(context, new FakeCredentialProtector())
        {
        }

        public override Task<decimal> GetBalanceAsync(
            int userId,
            string currency = "USDT",
            bool useFutures = false,
            int? connectionId = null)
        {
            return Task.FromResult(90m);
        }
    }

    [Fact]
    public async Task PlaceOrderAsync_BlocksOrder_WhenMaximumDrawdownIsReached()
    {
        await using var context = CreateContext();

        const int userId = 6;

        context.BrokerConnections.Add(new BrokerConnection
        {
            Id = 1,
            UserId = userId,
            BrokerName = "Bybit",
            ApiKey = "test-key",
            ApiSecret = "test-secret",
            IsActive = true,
            IsConnected = true,
            IsTestnet = true,
            IsLiveTradingEnabled = false
        });

        context.RiskSettings.Add(new RiskSetting
        {
            UserId = userId,
            RiskLevel = "Balanced",
            RiskPerTrade = 1m,
            Leverage = 1m,
            MaxOpenTrades = 5,
            DailyLossLimit = 5m,
            MaxDrawdown = 10m,
            EquityHighWaterMark = 100m,
            EquityHighWaterMarkUpdatedAt = DateTime.UtcNow.AddHours(-1),
            UseFixedLotSize = false,
            CreatedAt = DateTime.UtcNow
        });

        await context.SaveChangesAsync();

        var service = new TestBrokerService(context);

        var result = await service.PlaceOrderAsync(
            userId: userId,
            symbol: "BTCUSDT",
            direction: "BUY",
            orderType: "Market",
            idempotencyKey: "max-drawdown-test-001");

        result.Success.Should().BeFalse();
        result.Error.Should().StartWith(
            "Maximum drawdown limit reached (10.00%)");

        var auditLog = await context.AuditLogs
            .SingleOrDefaultAsync(a =>
                a.UserId == userId &&
                a.Action == "RiskBlocked_MaxDrawdown");

        auditLog.Should().NotBeNull();
        auditLog!.Details.Should().Contain(
            "current equity (90.00 USDT)");
        auditLog.Details.Should().Contain(
            "equity high-water mark (100.00 USDT)");
        auditLog.Details.Should().Contain(
            "maximum drawdown of 10.00%");

        var trades = await context.Trades
            .Where(t => t.UserId == userId)
            .ToListAsync();

        trades.Should().BeEmpty();
    }
}