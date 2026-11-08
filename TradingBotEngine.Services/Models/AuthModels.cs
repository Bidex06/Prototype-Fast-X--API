using System.ComponentModel.DataAnnotations;

namespace TradingBotEngine.Services.Models
{
    public class RegisterRequest
    {
        [Required, EmailAddress, StringLength(100)]
        public string Email { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 8)]
        public string Password { get; set; } = string.Empty;

        [Required, StringLength(100, MinimumLength = 2)]
        [RegularExpression(@"^[A-Za-zÀ-ÖØ-öø-ÿ' -]+$", ErrorMessage = "First name contains invalid characters.")]
        public string FirstName { get; set; } = string.Empty;

        [Required, StringLength(100, MinimumLength = 2)]
        [RegularExpression(@"^[A-Za-zÀ-ÖØ-öø-ÿ' -]+$", ErrorMessage = "Last name contains invalid characters.")]
        public string LastName { get; set; } = string.Empty;
    }

    public class LoginRequest
    {
        [Required, EmailAddress, StringLength(100)]
        public string Email { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 1)]
        public string Password { get; set; } = string.Empty;
    }

    public class RefreshRequest
    {
        [Required, StringLength(512, MinimumLength = 32)]
        public string RefreshToken { get; set; } = string.Empty;
    }

    public class AuthResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public UserResponse? User { get; set; }
    }

    public class UserResponse
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public DateTime? SubscriptionEnd { get; set; }
        public bool IsTrial { get; set; }
        public bool IsSubscriptionActive { get; set; }
        public bool IsEmailVerified { get; set; }
        public bool IsActive { get; set; }
        public string Role { get; set; } = "User";
        public bool IsAutoTradeEnabled { get; set; }
    }
}
