//ExchangeMarketDataService.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Bybit.Net;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using Microsoft.Extensions.Caching.Memory;
using TradingBotEngine.Core.Models;

namespace TradingBotEngine.Services;

public sealed class ExchangeMarketDataService
{
    private const int DefaultCandleLimit = 250;
    private const int MaxCandleLimit = 1000;
    private const int SymbolCacheMinutes = 5;

    private readonly IMemoryCache _cache;
    private readonly HttpClient _httpClient;

    public ExchangeMarketDataService(IMemoryCache cache)
    {
        _cache = cache;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TradingBotEngine/1.0");
    }

    public async Task<IReadOnlyList<MarketSymbol>> GetTradableSymbolsAsync(
        string brokerName,
        bool isTestnet,
        bool useFutures,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        brokerName = brokerName?.Trim() ?? string.Empty;
        search = string.IsNullOrWhiteSpace(search)
            ? null
            : search.Trim().ToUpperInvariant();

        if (brokerName.Equals("Binance", StringComparison.OrdinalIgnoreCase))
        {
            var all = await GetBinanceSymbolsAsync(isTestnet, useFutures, cancellationToken);
            return FilterSymbols(all, search);
        }

        if (brokerName.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
        {
            var all = await GetBybitSymbolsAsync(isTestnet, useFutures, cancellationToken);
            return FilterSymbols(all, search);
        }

        throw new NotSupportedException($"Market data is not supported for broker '{brokerName}'.");
    }

    public async Task<ExchangeOrderRules> GetBinanceRulesAsync(
        string symbol,
        bool useFutures,
        bool isTestnet = true)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol is required.", nameof(symbol));

        symbol = symbol.Trim().ToUpperInvariant();
        
        var client = new BinanceRestClient(options =>
        {
            options.Environment = BinanceEnvironments.ForTrading(isTestnet);
        });

        if (useFutures)
        {
            var info = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
            if (!info.Success)
                throw new InvalidOperationException($"Unable to get Binance Futures exchange info: {info.Error}");

            var market = info.Data.Symbols.FirstOrDefault(x =>
                string.Equals(x.Name, symbol, StringComparison.OrdinalIgnoreCase));
            if (market == null)
                throw new InvalidOperationException($"Binance Futures symbol '{symbol}' was not found.");

            var ticker = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol);
            if (!ticker.Success)
                throw new InvalidOperationException($"Unable to get Binance Futures price for {symbol}: {ticker.Error}");

            var lot = market.LotSizeFilter ?? throw new InvalidOperationException($"Binance Futures returned no lot-size rules for {symbol}.");
            return new ExchangeOrderRules
            {
                MinQuantity = lot.MinQuantity,
                MaxQuantity = lot.MaxQuantity,
                QuantityStep = lot.StepSize,
                MinNotional = 0m,
                MaxLeverage = 1m,
                ReferencePrice = ticker.Data.Price
            };
        }

        var spotInfo = await client.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol);
        if (!spotInfo.Success)
            throw new InvalidOperationException($"Unable to get Binance Spot exchange info: {spotInfo.Error}");

        var spotMarket = spotInfo.Data.Symbols.FirstOrDefault(x =>
            string.Equals(x.Name, symbol, StringComparison.OrdinalIgnoreCase));
        if (spotMarket == null)
            throw new InvalidOperationException($"Binance Spot symbol '{symbol}' was not found.");

        var spotTicker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
        if (!spotTicker.Success)
            throw new InvalidOperationException($"Unable to get Binance Spot price for {symbol}: {spotTicker.Error}");

        var spotLot = spotMarket.LotSizeFilter ?? throw new InvalidOperationException($"Binance Spot returned no lot-size rules for {symbol}.");
        return new ExchangeOrderRules
        {
            MinQuantity = spotLot.MinQuantity,
            MaxQuantity = spotLot.MaxQuantity,
            QuantityStep = spotLot.StepSize,
            MinNotional = 0m,
            MaxLeverage = 1m,
            ReferencePrice = spotTicker.Data.LastPrice
        };
    }

    public async Task<ExchangeOrderRules> GetBybitRulesAsync(
        string symbol,
        bool useFutures,
        bool isTestnet = true)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol is required.", nameof(symbol));

        symbol = symbol.Trim().ToUpperInvariant();
        var client = new BybitRestClient(options =>
        {
            options.Environment = BybitEnvironments.ForMarketData(isTestnet);
        });

        if (useFutures)
        {
            var info = await client.V5Api.ExchangeData.GetLinearInverseSymbolsAsync(Category.Linear, symbol);
            if (!info.Success)
                throw new InvalidOperationException($"Unable to get Bybit Futures rules for {symbol}: {info.Error}");

            var market = info.Data.List.FirstOrDefault() ?? throw new InvalidOperationException($"Bybit Futures symbol '{symbol}' was not found.");
            var ticker = await client.V5Api.ExchangeData.GetLinearInverseTickersAsync(Category.Linear, symbol);
            if (!ticker.Success)
                throw new InvalidOperationException($"Unable to get Bybit Futures price for {symbol}: {ticker.Error}");

            var tickerData = ticker.Data.List.FirstOrDefault() ?? throw new InvalidOperationException($"Bybit returned no ticker for {symbol}.");
            var lot = market.LotSizeFilter ?? throw new InvalidOperationException($"Bybit Futures returned no lot-size rules for {symbol}.");
            return new ExchangeOrderRules
            {
                MinQuantity = lot.MinOrderQuantity,
                MaxQuantity = lot.MaxOrderQuantity,
                QuantityStep = lot.QuantityStep,
                MinNotional = 0m,
                MaxLeverage = market.LeverageFilter?.MaxLeverage ?? 1m,
                ReferencePrice = tickerData.LastPrice
            };
        }

        var spotInfo = await client.V5Api.ExchangeData.GetSpotSymbolsAsync(symbol);
        if (!spotInfo.Success)
            throw new InvalidOperationException($"Unable to get Bybit Spot rules for {symbol}: {spotInfo.Error}");

        var spotMarket = spotInfo.Data.List.FirstOrDefault() ?? throw new InvalidOperationException($"Bybit Spot symbol '{symbol}' was not found.");
        var spotTicker = await client.V5Api.ExchangeData.GetSpotTickersAsync(symbol);
        if (!spotTicker.Success)
            throw new InvalidOperationException($"Unable to get Bybit Spot price for {symbol}: {spotTicker.Error}");

        var spotTickerData = spotTicker.Data.List.FirstOrDefault() ?? throw new InvalidOperationException($"Bybit returned no Spot ticker for {symbol}.");
        var spotLot = spotMarket.LotSizeFilter ?? throw new InvalidOperationException($"Bybit Spot returned no lot-size rules for {symbol}.");
        return new ExchangeOrderRules
        {
            MinQuantity = spotLot.MinOrderQuantity,
            MaxQuantity = spotLot.MaxOrderQuantity,
            QuantityStep = spotLot.BasePrecision,
            MinNotional = spotLot.MinOrderValue,
            MaxLeverage = 1m,
            ReferencePrice = spotTickerData.LastPrice
        };
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string brokerName,
        string symbol,
        string timeframe,
        bool useFutures,
        int limit = DefaultCandleLimit,
        bool isTestnet = true)
    {
        if (string.IsNullOrWhiteSpace(brokerName))
            throw new ArgumentException("Broker name is required.", nameof(brokerName));
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        if (string.IsNullOrWhiteSpace(timeframe))
            throw new ArgumentException("Timeframe is required.", nameof(timeframe));
        if (limit < 1 || limit > MaxCandleLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Candle limit must be between 1 and {MaxCandleLimit}.");

        symbol = symbol.Trim().ToUpperInvariant();
        timeframe = timeframe.Trim();

        if (brokerName.Equals("Binance", StringComparison.OrdinalIgnoreCase))
            return await GetBinanceCandlesAsync(symbol, timeframe, useFutures, limit, isTestnet);
        if (brokerName.Equals("Bybit", StringComparison.OrdinalIgnoreCase))
            return await GetBybitCandlesAsync(symbol, timeframe, useFutures, limit, isTestnet);

        throw new NotSupportedException($"Market data is not supported for broker '{brokerName}'.");
    }

    private async Task<IReadOnlyList<MarketSymbol>> GetBinanceSymbolsAsync(bool isTestnet, bool useFutures, CancellationToken cancellationToken)
    {
        var key = $"market-symbols:binance:{isTestnet}:{useFutures}";
        if (_cache.TryGetValue(key, out IReadOnlyList<MarketSymbol>? cached) && cached != null)
            return cached;

        var demo = isTestnet && BinanceEnvironments.UseDemoTrading;
        var url = useFutures
            ? (demo
                ? "https://demo-fapi.binance.com/fapi/v1/exchangeInfo"
                : (isTestnet ? "https://testnet.binancefuture.com/fapi/v1/exchangeInfo" : "https://fapi.binance.com/fapi/v1/exchangeInfo"))
            : (demo
                ? "https://demo-api.binance.com/api/v3/exchangeInfo"
                : (isTestnet ? "https://testnet.binance.vision/api/v3/exchangeInfo" : "https://api.binance.com/api/v3/exchangeInfo"));

        var payload = await _httpClient.GetFromJsonAsync<BinanceExchangeInfo>(url, cancellationToken)
            ?? throw new InvalidOperationException("Binance returned an empty exchange-info response.");

        var symbols = payload.Symbols
            .Where(x => string.Equals(x.Status, "TRADING", StringComparison.OrdinalIgnoreCase))
            .Select(x => new MarketSymbol(x.Symbol, x.BaseAsset, x.QuoteAsset, "Binance", useFutures ? "futures" : "spot", x.Status))
            .OrderBy(x => x.Symbol, StringComparer.Ordinal)
            .ToList();

        _cache.Set(key, (IReadOnlyList<MarketSymbol>)symbols, TimeSpan.FromMinutes(SymbolCacheMinutes));
        return symbols;
    }

    private async Task<IReadOnlyList<MarketSymbol>> GetBybitSymbolsAsync(bool isTestnet, bool useFutures, CancellationToken cancellationToken)
    {
        var key = $"market-symbols:bybit:{isTestnet}:{useFutures}";
        if (_cache.TryGetValue(key, out IReadOnlyList<MarketSymbol>? cached) && cached != null)
            return cached;

        var baseUrl = (isTestnet && !BybitEnvironments.UseDemoTrading)
            ? "https://api-testnet.bybit.com"
            : "https://api.bybit.com";
        var category = useFutures ? "linear" : "spot";
        var symbols = new List<MarketSymbol>();
        string? cursor = null;

        do
        {
            var url = $"{baseUrl}/v5/market/instruments-info?category={category}&limit=1000";
            if (!string.IsNullOrWhiteSpace(cursor))
                url += $"&cursor={Uri.EscapeDataString(cursor)}";

            var payload = await _httpClient.GetFromJsonAsync<BybitInstrumentResponse>(url, cancellationToken)
                ?? throw new InvalidOperationException("Bybit returned an empty instrument-info response.");

            if (payload.RetCode != 0)
                throw new InvalidOperationException($"Bybit instrument-info request failed: {payload.RetMsg}");

            foreach (var item in payload.Result.List)
            {
                if (!string.Equals(item.Status, "Trading", StringComparison.OrdinalIgnoreCase))
                    continue;

                symbols.Add(new MarketSymbol(item.Symbol, item.BaseCoin, item.QuoteCoin, "Bybit", useFutures ? "futures" : "spot", item.Status));
            }

            cursor = payload.Result.NextPageCursor;
        }
        while (useFutures && !string.IsNullOrWhiteSpace(cursor));

        var distinct = symbols
            .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Symbol, StringComparer.Ordinal)
            .ToList();

        _cache.Set(key, (IReadOnlyList<MarketSymbol>)distinct, TimeSpan.FromMinutes(SymbolCacheMinutes));
        return distinct;
    }

    private static IReadOnlyList<MarketSymbol> FilterSymbols(IReadOnlyList<MarketSymbol> symbols, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return symbols;

        return symbols
            .Where(x => x.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        x.BaseAsset.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        x.QuoteAsset.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static async Task<IReadOnlyList<Candle>> GetBinanceCandlesAsync(string symbol, string timeframe, bool useFutures, int limit, bool isTestnet)
    {
        var interval = ParseBinanceInterval(timeframe);
        
        var client = new BinanceRestClient(options =>
        {
            options.Environment = BinanceEnvironments.ForMarketData(isTestnet);
        });

        if (useFutures)
        {
            var result = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: limit);
            if (!result.Success)
                throw new InvalidOperationException($"Unable to get Binance Futures candles for {symbol} ({timeframe}): {result.Error}");

            return result.Data.OrderBy(x => x.OpenTime).Select(x => new Candle
            {
                Timestamp = x.OpenTime,
                Open = (double)x.OpenPrice,
                High = (double)x.HighPrice,
                Low = (double)x.LowPrice,
                Close = (double)x.ClosePrice,
                Volume = (double)x.Volume
            }).ToList();
        }

        var spotResult = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: limit);
        if (!spotResult.Success)
            throw new InvalidOperationException($"Unable to get Binance Spot candles for {symbol} ({timeframe}): {spotResult.Error}");

        return spotResult.Data.OrderBy(x => x.OpenTime).Select(x => new Candle
        {
            Timestamp = x.OpenTime,
            Open = (double)x.OpenPrice,
            High = (double)x.HighPrice,
            Low = (double)x.LowPrice,
            Close = (double)x.ClosePrice,
            Volume = (double)x.Volume
        }).ToList();
    }

    private static async Task<IReadOnlyList<Candle>> GetBybitCandlesAsync(string symbol, string timeframe, bool useFutures, int limit, bool isTestnet)
    {
        var interval = ParseBybitInterval(timeframe);
        var category = useFutures ? Category.Linear : Category.Spot;
        var client = new BybitRestClient(options =>
        {
            options.Environment = BybitEnvironments.ForMarketData(isTestnet);
        });

        var result = await client.V5Api.ExchangeData.GetKlinesAsync(category, symbol, interval, limit: limit);
        if (!result.Success)
            throw new InvalidOperationException($"Unable to get Bybit candles for {symbol} ({timeframe}): {result.Error}");

        return result.Data.List.OrderBy(x => x.StartTime).Select(x => new Candle
        {
            Timestamp = x.StartTime,
            Open = (double)x.OpenPrice,
            High = (double)x.HighPrice,
            Low = (double)x.LowPrice,
            Close = (double)x.ClosePrice,
            Volume = (double)x.Volume
        }).ToList();
    }

    private static Binance.Net.Enums.KlineInterval ParseBinanceInterval(string timeframe) => timeframe.ToLowerInvariant() switch
    {
        "1m" => Binance.Net.Enums.KlineInterval.OneMinute,
        "3m" => Binance.Net.Enums.KlineInterval.ThreeMinutes,
        "5m" => Binance.Net.Enums.KlineInterval.FiveMinutes,
        "15m" => Binance.Net.Enums.KlineInterval.FifteenMinutes,
        "30m" => Binance.Net.Enums.KlineInterval.ThirtyMinutes,
        "1h" => Binance.Net.Enums.KlineInterval.OneHour,
        "2h" => Binance.Net.Enums.KlineInterval.TwoHour,
        "4h" => Binance.Net.Enums.KlineInterval.FourHour,
        "6h" => Binance.Net.Enums.KlineInterval.SixHour,
        "8h" => Binance.Net.Enums.KlineInterval.EightHour,
        "12h" => Binance.Net.Enums.KlineInterval.TwelveHour,
        "1d" => Binance.Net.Enums.KlineInterval.OneDay,
        "3d" => Binance.Net.Enums.KlineInterval.ThreeDay,
        "1w" => Binance.Net.Enums.KlineInterval.OneWeek,
        _ => throw new ArgumentException($"Unsupported Binance timeframe '{timeframe}'.", nameof(timeframe))
    };

    private static Bybit.Net.Enums.KlineInterval ParseBybitInterval(string timeframe) => timeframe.ToLowerInvariant() switch
    {
        "1m" => Bybit.Net.Enums.KlineInterval.OneMinute,
        "3m" => Bybit.Net.Enums.KlineInterval.ThreeMinutes,
        "5m" => Bybit.Net.Enums.KlineInterval.FiveMinutes,
        "15m" => Bybit.Net.Enums.KlineInterval.FifteenMinutes,
        "30m" => Bybit.Net.Enums.KlineInterval.ThirtyMinutes,
        "1h" => Bybit.Net.Enums.KlineInterval.OneHour,
        "2h" => Bybit.Net.Enums.KlineInterval.TwoHours,
        "4h" => Bybit.Net.Enums.KlineInterval.FourHours,
        "6h" => Bybit.Net.Enums.KlineInterval.SixHours,
        "12h" => Bybit.Net.Enums.KlineInterval.TwelveHours,
        "1d" => Bybit.Net.Enums.KlineInterval.OneDay,
        "1w" => Bybit.Net.Enums.KlineInterval.OneWeek,
        _ => throw new ArgumentException($"Unsupported Bybit timeframe '{timeframe}'.", nameof(timeframe))
    };

    private sealed class BinanceExchangeInfo
    {
        [JsonPropertyName("symbols")]
        public List<BinanceSymbol> Symbols { get; set; } = [];
    }

    private sealed class BinanceSymbol
    {
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = string.Empty;
        [JsonPropertyName("baseAsset")] public string BaseAsset { get; set; } = string.Empty;
        [JsonPropertyName("quoteAsset")] public string QuoteAsset { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    }

    private sealed class BybitInstrumentResponse
    {
        [JsonPropertyName("retCode")] public int RetCode { get; set; }
        [JsonPropertyName("retMsg")] public string RetMsg { get; set; } = string.Empty;
        [JsonPropertyName("result")] public BybitInstrumentResult Result { get; set; } = new();
    }

    private sealed class BybitInstrumentResult
    {
        [JsonPropertyName("list")] public List<BybitInstrument> List { get; set; } = [];
        [JsonPropertyName("nextPageCursor")] public string? NextPageCursor { get; set; }
    }

    private sealed class BybitInstrument
    {
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = string.Empty;
        [JsonPropertyName("baseCoin")] public string BaseCoin { get; set; } = string.Empty;
        [JsonPropertyName("quoteCoin")] public string QuoteCoin { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    }

    public sealed record MarketSymbol(
        string Symbol,
        string BaseAsset,
        string QuoteAsset,
        string Exchange,
        string Market,
        string Status);
}    