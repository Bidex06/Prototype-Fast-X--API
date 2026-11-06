using Skender.Stock.Indicators;
using TradingBotEngine.Core.Models;

namespace TradingBotEngine.Core
{
    public class IndicatorCalculator
    {
        public Task<Dictionary<string, object>> CalculateIndicators(
            List<Candle> candles,
            Dictionary<string, object>? parameters = null)
        {
            if (candles == null || candles.Count < 200)
                throw new ArgumentException("At least 200 candles are required.", nameof(candles));

            var indicators = new Dictionary<string, object>();
            var quotes = candles.Select(c => new Quote
            {
                Date = c.Timestamp,
                Open = (decimal)c.Open,
                High = (decimal)c.High,
                Low = (decimal)c.Low,
                Close = (decimal)c.Close,
                Volume = (decimal)c.Volume
            }).ToList();

            // 1. Core Indicators
            indicators["RSI"] = quotes.GetRsi(14).Last().Rsi ?? 50;
            indicators["MA50"] = quotes.GetSma(50).Last().Sma ?? 0;
            indicators["MA200"] = quotes.GetSma(200).Last().Sma ?? 0;
            indicators["EMA50"] = quotes.GetEma(50).Last().Ema ?? 0;
            indicators["EMA200"] = quotes.GetEma(200).Last().Ema ?? 0;
            
            var currentCandle = candles.Last();
            var avgVolume = (decimal)candles.TakeLast(20).Average(c => c.Volume);
            var currentPrice = (decimal)currentCandle.Close;
            var ma50 = Convert.ToDecimal(indicators["MA50"]);

            indicators["Price"] = currentPrice;
            indicators["Volume"] = (decimal)currentCandle.Volume;
            indicators["AverageVolume"] = avgVolume;

            // 2. Trend Structure (Higher Highs / Lower Lows)
            var (isUptrend, isDowntrend) = DetermineTrendStructure(candles);
            indicators["IsUptrend"] = isUptrend;
            indicators["IsDowntrend"] = isDowntrend;
            indicators["Trend"] = isUptrend ? "Uptrend" : isDowntrend ? "Downtrend" : "Ranging";

            // 3. THE LOOSENING: Look back 3 candles for Rejection and Volume
            var lookbackWindow = candles.TakeLast(3).ToList();
            
            bool recentBullishRejection = false;
            bool recentBearishRejection = false;
            bool recentVolumeSpike = false;

            foreach (var candle in lookbackWindow)
            {
                if (DetectRejectionCandle(candle).Bullish) recentBullishRejection = true;
                if (DetectRejectionCandle(candle).Bearish) recentBearishRejection = true;
                if ((decimal)candle.Volume > avgVolume) recentVolumeSpike = true;
            }

            indicators["BullishRejection"] = recentBullishRejection;
            indicators["BearishRejection"] = recentBearishRejection;
            indicators["VolumeSpike"] = recentVolumeSpike;

            // 4. Support & Resistance (Role Reversal)
            var (support, resistance, brokenResistanceIsSupport, brokenSupportIsResistance) = FindDynamicSupportResistance(candles);
            indicators["Support"] = support;
            indicators["Resistance"] = resistance;
            indicators["BrokenResistanceIsSupport"] = brokenResistanceIsSupport;
            indicators["BrokenSupportIsResistance"] = brokenSupportIsResistance;

            // 5. MA Retest Check (Did we touch MA50 in the last 5 candles?)
            var recentCandles = candles.TakeLast(5).ToList();
            bool retestedMaFromAbove = currentPrice > ma50 && recentCandles.Any(c => (decimal)c.Low <= ma50 * 1.005m);
            bool retestedMaFromBelow = currentPrice < ma50 && recentCandles.Any(c => (decimal)c.High >= ma50 * 0.995m);

            indicators["RetestedMaFromAbove"] = retestedMaFromAbove;
            indicators["RetestedMaFromBelow"] = retestedMaFromBelow;

            return Task.FromResult(indicators);
        }

        private (bool IsUptrend, bool IsDowntrend) DetermineTrendStructure(List<Candle> candles)
        {
            var recent = candles.TakeLast(20).ToList();
            var highs = recent.Select(c => c.High).ToList();
            var lows = recent.Select(c => c.Low).ToList();

            var recentHighs = highs.TakeLast(5).Average();
            var previousHighs = highs.SkipLast(5).Average();
            var recentLows = lows.TakeLast(5).Average();
            var previousLows = lows.SkipLast(5).Average();

            bool isUptrend = recentHighs > previousHighs && recentLows > previousLows;
            bool isDowntrend = recentHighs < previousHighs && recentLows < previousLows;

            return (isUptrend, isDowntrend);
        }

        private (bool Bullish, bool Bearish) DetectRejectionCandle(Candle candle)
        {
            var open = (decimal)candle.Open;
            var close = (decimal)candle.Close;
            var high = (decimal)candle.High;
            var low = (decimal)candle.Low;

            var body = Math.Abs(open - close);
            var totalRange = high - low;

            if (totalRange == 0) return (false, false);

            var upperWick = high - Math.Max(open, close);
            var lowerWick = Math.Min(open, close) - low;

            bool isBullish = lowerWick > (body * 2m) && upperWick < (body * 0.5m) && close > (low + (totalRange * 0.6m));
            bool isBearish = upperWick > (body * 2m) && lowerWick < (body * 0.5m) && close < (low + (totalRange * 0.4m));

            return (isBullish, isBearish);
        }

        private (decimal Support, decimal Resistance, bool BrokenResistanceIsSupport, bool BrokenSupportIsResistance) FindDynamicSupportResistance(List<Candle> candles)
        {
            var recent = candles.TakeLast(50).ToList();
            var highs = recent.Select(c => (decimal)c.High).ToList();
            var lows = recent.Select(c => (decimal)c.Low).ToList();

            var resistance = highs.Max();
            var support = lows.Min();
            var currentPrice = (decimal)candles.Last().Close;

            bool brokenResistanceIsSupport = currentPrice > resistance && (currentPrice - resistance) / resistance < 0.01m;
            bool brokenSupportIsResistance = currentPrice < support && (support - currentPrice) / support < 0.01m;

            return (support, resistance, brokenResistanceIsSupport, brokenSupportIsResistance);
        }
    }
} 