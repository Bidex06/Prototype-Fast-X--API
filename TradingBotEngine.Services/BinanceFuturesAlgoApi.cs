using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingBotEngine.Services
{
    /// <summary>
    /// Binance moved USD-M futures conditional orders (STOP_MARKET, TAKE_PROFIT_MARKET, ...) to the
    /// Algo Order API. The old /fapi/v1/order endpoint now rejects them with error -4120, and the
    /// Binance.Net version in use does not know the new endpoints, so they are called directly here.
    /// Docs: POST /fapi/v1/algoOrder and DELETE /fapi/v1/algoOpenOrders (USDS-M Futures, algoType=CONDITIONAL).
    /// </summary>
    internal sealed class BinanceFuturesAlgoApi
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _apiSecret;

        public BinanceFuturesAlgoApi(string baseUrl, string apiKey, string apiSecret)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;
            _apiSecret = apiSecret;
        }

        // A stop-loss / take-profit that closes the WHOLE position when triggered (closePosition=true).
        public async Task<(bool Success, long AlgoId, string? Error)> PlaceCloseAllConditionalAsync(
            string symbol, string side, string type, decimal triggerPrice)
        {
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("algoType", "CONDITIONAL"),
                new("symbol", symbol),
                new("side", side),
                new("type", type),
                new("triggerPrice", triggerPrice.ToString("0.##########", CultureInfo.InvariantCulture)),
                new("closePosition", "true"),
                new("workingType", "MARK_PRICE")
            };

            var (ok, body) = await SendAsync(HttpMethod.Post, "/fapi/v1/algoOrder", parameters);
            if (!ok)
                return (false, 0, body);

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("algoId", out var id) && id.TryGetInt64(out var algoId))
                    return (true, algoId, null);
            }
            catch (JsonException)
            {
            }

            return (false, 0, $"Unexpected Binance response: {body}");
        }

        public async Task<(bool Success, string? Error)> CancelAllOpenAsync(string symbol)
        {
            var (ok, body) = await SendAsync(
                HttpMethod.Delete,
                "/fapi/v1/algoOpenOrders",
                new List<KeyValuePair<string, string>> { new("symbol", symbol) });

            return ok ? (true, null) : (false, body);
        }

        private async Task<(bool Ok, string Body)> SendAsync(
            HttpMethod method, string path, List<KeyValuePair<string, string>> parameters)
        {
            parameters.Add(new("recvWindow", "10000"));
            parameters.Add(new("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));

            var query = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            var signature = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(_apiSecret), Encoding.UTF8.GetBytes(query)))
                .ToLowerInvariant();

            using var request = new HttpRequestMessage(method, $"{_baseUrl}{path}?{query}&signature={signature}");
            request.Headers.Add("X-MBX-APIKEY", _apiKey);

            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return (response.IsSuccessStatusCode, body);
        }
    }
}
