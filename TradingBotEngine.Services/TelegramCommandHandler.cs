using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using TradingBotEngine.Data;
using TradingBotEngine.Data.Models;

namespace TradingBotEngine.Services
{
    public class TelegramCommandHandler
    {
        private readonly ApplicationDbContext _context;
        private readonly BrokerService _brokerService;

        public TelegramCommandHandler(ApplicationDbContext context, BrokerService brokerService)
        {
            _context = context;
            _brokerService = brokerService;
        }

        public async Task<string> HandleCommandAsync(long chatId, string username, string command, Message message)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "Type /help for available commands.";

            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;

            return cmd switch
            {
                "/start" => await HandleStartAsync(chatId, username),
                "/logout" => await HandleLogoutAsync(chatId),
                "/balance" => await HandleBalanceAsync(chatId),
                "/positions" => await HandlePositionsAsync(chatId),
                "/pnl" => await HandlePnlAsync(chatId),
                "/history" => await HandleHistoryAsync(chatId),
                "/bots" => await HandleBotsAsync(chatId),
                "/status" => await HandleStatusAsync(chatId),
                "/stop_bot" => await HandleToggleBotAsync(chatId, arg, false),
                "/start_bot" => await HandleToggleBotAsync(chatId, arg, true),
                "/help" => HelpMessage(),
                "/login" or "/connect_binance" or "/connect_bybit" or "/trade"
                    => "🔒 For security, password/API-key entry and manual order placement are disabled in Telegram. Use the authenticated web dashboard instead.",
                _ => "❌ Unknown command. Type /help for available commands."
            };
        }

        private async Task<string> HandleStartAsync(long chatId, string username)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId);
            if (user == null)
            {
                return "👋 Welcome to Fast X Trading Bot.\n\n" +
                       "🆔 Your Telegram Chat ID:\n" +
                       $"{chatId}\n\n" +
                       "🔐 To link this chat to your trading account:\n\n" +
                       "1. Copy the Chat ID above\n" +
                       "2. Open the Fast X dashboard\n" +
                       "3. Settings → Telegram\n" +
                       "4. Paste the Chat ID there and click Connect";
            }
            return $"👋 Welcome back, {user.FirstName}!\n\n" +
                   $"📊 Account: {user.Email}\n" +
                   $"🔗 {await GetConnectedBrokersAsync(user.Id)}\n\n" +
                   "Type /help for available commands.";
        }

        private async Task<string> HandleLogoutAsync(long chatId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId);
            if (user == null) return "❌ Your Telegram is not linked to any account.";

            user.TelegramChatId = null;
            user.TelegramUsername = null;
            await _context.SaveChangesAsync();
            return "✅ Telegram has been unlinked from your account.";
        }

        private async Task<string> HandleBalanceAsync(long chatId)
        {
            var user = await GetUserByChatIdAsync(chatId);
            if (user == null) return "❌ Link Telegram from the web dashboard first.";

            var broker = await _context.BrokerConnections
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.UserId == user.Id && b.IsActive && b.IsConnected);

            if (broker == null) return "❌ No active broker connection.";

            var balance = await _brokerService.GetBalanceAsync(user.Id, "USDT", false);
            return $"💰 Balance\n\nExchange: {broker.BrokerName}\nBalance: ${balance:F2} USDT\nMode: {(broker.IsTestnet ? "TESTNET" : "LIVE")}";
        }

        private async Task<string> HandlePositionsAsync(long chatId)
        {
            var user = await GetUserByChatIdAsync(chatId);
            if (user == null) return "❌ Link Telegram from the web dashboard first.";

            var positions = await _context.Positions
                .AsNoTracking()
                .Where(p => p.UserId == user.Id && p.Status == "Open")
                .ToListAsync();

            if (positions.Count == 0) return "📭 No open positions.";

            var response = "📊 Open Positions\n\n";
            foreach (var position in positions)
            {
                response += $"🔹 {position.Symbol}\n" +
                            $"Direction: {position.Direction}\n" +
                            $"Entry: ${position.EntryPrice:F2} | Current: ${position.CurrentPrice:F2}\n" +
                            $"PnL: ${position.UnrealizedPnL:F2}\n\n";
            }
            return response;
        }

        private async Task<string> HandlePnlAsync(long chatId)
        {
            var user = await GetUserByChatIdAsync(chatId);
            if (user == null) return "❌ Link Telegram first.";

            var today = DateTime.UtcNow.Date;
            var trades = await _context.Trades
                .Where(t => t.UserId == user.Id && t.Status == "Closed" && t.ExitTime >= today)
                .ToListAsync();

            var totalPnl = trades.Sum(t => t.ProfitLoss ?? 0);
            var wins = trades.Count(t => t.ProfitLoss > 0);
            var losses = trades.Count(t => t.ProfitLoss < 0);
            var winRate = trades.Count > 0 ? ((double)wins / trades.Count) * 100 : 0;

            return $"📈 Today's Performance\n\n" +
                   $"Net P&L: ${totalPnl:F2}\n" +
                   $"Trades Closed: {trades.Count}\n" +
                   $"Wins: {wins} | Losses: {losses}\n" +
                   $"Win Rate: {winRate:F1}%";
        }

        private async Task<string> HandleHistoryAsync(long chatId)
        {
            var user = await GetUserByChatIdAsync(chatId);
            if (user == null) return "❌ Link Telegram first.";

            var trades = await _context.Trades
                .Where(t => t.UserId == user.Id && t.Status == "Closed")
                .OrderByDescending(t => t.ExitTime)
                .Take(5)
                .ToListAsync();

            if (trades.Count == 0) return "📭 No closed trades yet.";

            var response = "🕒 Recent Trades\n\n";
            foreach (var t in trades)
            {
                var pnlSign = (t.ProfitLoss ?? 0) >= 0 ? "+" : "";
                var emoji = (t.ProfitLoss ?? 0) >= 0 ? "🟢" : "🔴";
                response += $"{emoji} {t.Symbol} {t.Direction}\n" +
                            $"PnL: {pnlSign}${t.ProfitLoss:F2} ({(t.ProfitLossPercentage ?? 0):F2}%)\n" +
                            $"Closed: {t.ExitTime:MMM dd, HH:mm}\n\n";
            }
            return response;
        }

        private async Task<string> HandleBotsAsync(long chatId)
        {
            var user = await GetUserByChatIdAsync(chatId);
            if (user == null) return "❌ Link Telegram first.";

            var bots = await _context.TradingBots
                .Where(b => b.UserId == user.Id)
                .ToListAsync();

            if (bots.Count == 0) return "🤖 You have no trading bots configured.";

            var response = "🤖 Your Bots\n\n";
            foreach (var b in bots)
            {
                var status = !b.IsEnabled ? "⏸ Disabled" : b.IsRunning ? "▶️ Running" : "⏹ Stopped";
                response += $"[ID: {b.Id}] {b.Name}\n" +
                            $"Strategy: {b.Strategy} | Status: {status}\n\n";
            }
            response += "Use /stop_bot [ID] or /start_bot [ID] to control them.";
            return response;
        }

        private async Task<string> HandleStatusAsync(long chatId)
        {
            var user = await GetUserByChatIdAsync(chatId);
            if (user == null) return "❌ Link Telegram first.";

            var risk = await _context.RiskSettings.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == user.Id);
            var brokers = await GetConnectedBrokersAsync(user.Id);

            return $"⚙️ System Status\n\n" +
                   $"Auto-Trade: {(user.IsAutoTradeEnabled ? "🟢 Enabled" : "🔴 Disabled")}\n" +
                   $"Kill Switch: {(risk?.IsEmergencyKillSwitch == true ? "🚨 ACTIVE" : "✅ OFF")}\n" +
                   $"Brokers: {brokers}";
        }

        private async Task<string> HandleToggleBotAsync(long chatId, string? botIdStr, bool start)
        {
            var user = await GetUserByChatIdAsync(chatId);
            if (user == null) return "❌ Link Telegram first.";

            if (!int.TryParse(botIdStr, out var botId))
                return $"❌ Please provide a valid bot ID.\nExample: /{(start ? "start" : "stop")}_bot 1";

            var bot = await _context.TradingBots.FirstOrDefaultAsync(b => b.Id == botId && b.UserId == user.Id);
            if (bot == null) return $"❌ Bot with ID {botId} not found.";

            if (bot.IsRunning == start)
                return $"ℹ️ Bot '{bot.Name}' is already {(start ? "running" : "stopped")}.";

            if (start && !user.IsAutoTradeEnabled)
                return "⚠️ Cannot start bot: Account-level Auto-Trade is disabled in Settings.";

            bot.IsRunning = start;
            bot.UpdatedAt = DateTime.UtcNow;
            if (start) bot.LastStartedAt = DateTime.UtcNow;
            else bot.LastStoppedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return $"✅ Bot '{bot.Name}' has been {(start ? "started" : "stopped")}.";
        }

        private async Task<TradingBotEngine.Data.Models.User?> GetUserByChatIdAsync(long chatId)
            => await _context.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId && u.IsActive);

        private async Task<string> GetConnectedBrokersAsync(int userId)
        {
            var brokers = await _context.BrokerConnections
                .AsNoTracking()
                .Where(b => b.UserId == userId && b.IsActive && b.IsConnected)
                .Select(b => b.BrokerName)
                .ToListAsync();

            return brokers.Count > 0 ? string.Join(", ", brokers) : "None";
        }

        private static string HelpMessage() =>
            "📚 Available Commands\n\n" +
            "🔐 /start - Show Chat ID or Account Status\n" +
            "/logout - Unlink Telegram\n" +
            "/status - Check Auto-Trade and Kill Switch status\n\n" +
            "📊 /balance - Broker Balance\n" +
            "/positions - Open Positions\n" +
            "/pnl - Today's Profit & Loss\n" +
            "/history - Last 5 Closed Trades\n\n" +
            "🤖 /bots - List your trading bots\n" +
            "/start_bot <ID> - Start a bot\n" +
            "/stop_bot <ID> - Stop a bot\n\n" +
            "🔒 Manual order placement is disabled in Telegram for security.";
    }
} 