using System.Globalization;
using System.Text.Json;
using TradingBotEngine.Core.Models;

namespace TradingBotEngine.Core   
{
    public class SignalGenerator
    {
        public Task<TradingSignal> GenerateSignal(
            string symbol,
            List<Candle> candles,
            Dictionary<string, object> indicators,
            string htf1Bias = "Neutral",
            string htf2Bias = "Neutral")
        {
            if (candles == null || candles.Count == 0)
                throw new ArgumentException("No candles provided.", nameof(candles));

            var currentPrice = (decimal)candles[^1].Close;
            var signal = new TradingSignal
            {
                Symbol = symbol.Trim().ToUpperInvariant(),
                GeneratedAt = DateTime.UtcNow,
                Price = currentPrice,
                Action = "HOLD",
                Confidence = 50,
                Reason = "Analyzing...",
                Pattern = "None",
                Trend = "Ranging"
            };

            TryGetDecimal(indicators, "RSI", out var rsi);
            TryGetDecimal(indicators, "MA50", out var ma50);
            TryGetDecimal(indicators, "Support", out var support);
            TryGetDecimal(indicators, "Resistance", out var resistance);

            TryGetBool(indicators, "IsUptrend", out var isUptrend);
            TryGetBool(indicators, "IsDowntrend", out var isDowntrend);
            TryGetBool(indicators, "BullishRejection", out var bullishRejection);
            TryGetBool(indicators, "BearishRejection", out var bearishRejection);
            TryGetBool(indicators, "VolumeSpike", out var volumeSpike);
            
            TryGetBool(indicators, "BrokenResistanceIsSupport", out var roleReversalBull);
            TryGetBool(indicators, "BrokenSupportIsResistance", out var roleReversalBear);

            signal.Trend = isUptrend ? "Uptrend" : isDowntrend ? "Downtrend" : "Ranging";
            signal.Rsi = rsi;
            signal.Support = support;
            signal.Resistance = resistance;

            // ==========================================
            // BUY CONDITIONS (Loosened: Removed retest requirement)
            // ==========================================
            if (rsi > 50 && currentPrice > ma50 && isUptrend && bullishRejection && volumeSpike && htf1Bias != "Downtrend")
            {
                signal.Action = "BUY";
                signal.StopLoss = support;
                signal.TakeProfit = resistance;
                signal.Confidence = roleReversalBull ? 90 : 80;
                signal.Reason = $"BUY Setup: RSI({rsi:F1})>50, Price>MA50, Uptrend, Bullish Rejection Candle, Volume Spike. HTF Bias: {htf1Bias}/{htf2Bias}.";
                if (roleReversalBull) signal.Reason += " Broken Resistance acting as Support.";
            }
            // ==========================================
            // SELL CONDITIONS (Loosened: Removed retest requirement)
            // ==========================================
            else if (rsi < 50 && currentPrice < ma50 && isDowntrend && bearishRejection && volumeSpike && htf1Bias != "Uptrend")
            {
                signal.Action = "SELL";
                signal.StopLoss = resistance;
                signal.TakeProfit = support;
                signal.Confidence = roleReversalBear ? 90 : 80;
                signal.Reason = $"SELL Setup: RSI({rsi:F1})<50, Price<MA50, Downtrend, Bearish Rejection Candle, Volume Spike. HTF Bias: {htf1Bias}/{htf2Bias}.";
                if (roleReversalBear) signal.Reason += " Broken Support acting as Resistance.";
            }
            // ==========================================
            // HOLD
            // ==========================================
            else
            {
                signal.Action = "HOLD";
                signal.Confidence = 50;
                
                var missingConditions = new List<string>();
                if (rsi <= 50 && rsi >= 50) missingConditions.Add("RSI Neutral");
                if (!isUptrend && !isDowntrend) missingConditions.Add("No clear trend");
                if (!bullishRejection && !bearishRejection) missingConditions.Add("No rejection candle in last 3 candles");
                if (!volumeSpike) missingConditions.Add("No volume spike");
                
                signal.Reason = $"HOLD (Ranging): Missing conditions. {string.Join(", ", missingConditions)}.";
            }

            signal.Indicators = JsonSerializer.Serialize(indicators);
            return Task.FromResult(signal);
        }

        private static bool TryGetDecimal(Dictionary<string, object> dict, string key, out decimal value)
        {
            value = 0m;
            if (!dict.TryGetValue(key, out var raw) || raw == null) return false;
            try { value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }

        private static bool TryGetBool(Dictionary<string, object> dict, string key, out bool value)
        {
            value = false;
            if (!dict.TryGetValue(key, out var raw) || raw == null) return false;
            try { value = Convert.ToBoolean(raw); return true; }
            catch { return false; }
        }
    }
}     