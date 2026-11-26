using Binance.Net;

namespace TradingBotEngine.Services
{
    /// <summary>
    /// Chooses the Binance server for a connection.
    /// When <see cref="UseDemoTrading"/> is on, a Binance connection marked "testnet" talks to
    /// Binance Demo Trading (demo-api.binance.com / demo-fapi.binance.com) instead of the old
    /// Spot Testnet (testnet.binance.vision). Demo API keys are created on demo.binance.com and
    /// do NOT work on the Spot Testnet, and Spot Testnet keys do NOT work on Demo.
    /// Live connections always use the live server.
    /// </summary>
    public static class BinanceEnvironments
    {
        // Set once at startup from configuration: Binance:UseDemoTrading
        public static bool UseDemoTrading { get; set; }

        private static readonly BinanceEnvironment Demo =
            BinanceEnvironment.CreateCustom(
                "demo",
                "https://demo-api.binance.com",
                "wss://demo-stream.binance.com",
                "wss://demo-ws-api.binance.com/ws-api/v3",
                null,
                "https://demo-fapi.binance.com",
                "wss://fstream.binancefuture.com",
                null,
                null);

        // REST base URL of the USD-M futures server, for the few calls made without the library.
        public static string FuturesRestBaseUrl(bool isTestnet)
        {
            if (!isTestnet)
                return "https://fapi.binance.com";

            return UseDemoTrading ? "https://demo-fapi.binance.com" : "https://testnet.binancefuture.com";
        }

        // Used for orders, balances, positions and order rules.
        public static BinanceEnvironment ForTrading(bool isTestnet)
        {
            if (!isTestnet)
                return BinanceEnvironment.Live;

            return UseDemoTrading ? Demo : BinanceEnvironment.Testnet;
        }

        // Used for public market data (candles). Demo trading uses live prices.
        public static BinanceEnvironment ForMarketData(bool isTestnet)
        {
            if (!isTestnet)
                return BinanceEnvironment.Live;

            return UseDemoTrading ? BinanceEnvironment.Live : BinanceEnvironment.Testnet;
        }
    }
}
