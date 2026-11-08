using TradingBotEngine.Data.Models;

namespace TradingBotEngine.Data
{
    public static class SubscriptionGuard
    {
        public const string ExpiredMessage =
            "Your subscription has expired. Renew to create or run trading bots.";

        public static bool HasActiveSubscription(User user)
        {
            // 1. Admins always have access to test their own platform
            if (user.Role.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                return true;

            // 2. Regular users must have an active, non-expired subscription
            return user.Subscription != null &&
                   user.Subscription.IsActive &&
                   user.Subscription.EndDate > DateTime.UtcNow;
        }
    }
}   