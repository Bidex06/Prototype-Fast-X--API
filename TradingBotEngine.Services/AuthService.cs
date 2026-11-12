using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using TradingBotEngine.Data;
using TradingBotEngine.Data.Models;
using TradingBotEngine.Services.Models;

namespace TradingBotEngine.Services
{
    public class AuthService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        public AuthService(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        public async Task<AuthResult> RegisterAsync(RegisterRequest request)
        {
            var email = NormalizeEmail(request.Email);
            var firstName = NormalizeName(request.FirstName);
            var lastName = NormalizeName(request.LastName);

            if (!new EmailAddressAttribute().IsValid(email))
                return Failure("Invalid email address.");

            if (firstName.Length < 2 || lastName.Length < 2)
                return Failure("First and last name must contain at least 2 characters.");

            if (await _context.Users.AnyAsync(u => u.Email == email))
                return Failure("Email already registered");

            if (!HasStrongPassword(request.Password))
                return Failure("Password must be at least 8 characters long.");

            var now = DateTime.UtcNow;
            var trialEnd = now.AddDays(21);

            var user = new User
            {
                Email = email,
                FirstName = firstName,
                LastName = lastName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                IsEmailVerified = false,
                IsActive = true,
                IsAutoTradeEnabled = false,
                Role = email.Equals("johnathandunlop5@gmail.com", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User"
            };

            var riskSettings = new RiskSetting
            {
                User = user,
                RiskLevel = "Balanced",
                RiskPerTrade = 1.0m,
                MaxOpenTrades = 5,
                DailyLossLimit = 5.0m,
                MaxDrawdown = 10.0m,
                UseFixedLotSize = false
            };

            var subscription = new Subscription
            {
                User = user,
                Plan = "Monthly",
                StartDate = now,
                EndDate = trialEnd,
                IsActive = true,
                IsTrial = true,
                TrialEndDate = trialEnd
            };

            _context.Users.Add(user);
            _context.RiskSettings.Add(riskSettings);
            _context.Subscriptions.Add(subscription);

            // The user must be persisted before a token is generated so the JWT contains the real DB id.
            await _context.SaveChangesAsync();

            return await CreateSuccessResultAsync(user, subscription, "Registration successful! Your 21-day free trial has started.");
        }

        public async Task<AuthResult> LoginAsync(LoginRequest request)
        {
            var email = NormalizeEmail(request.Email);
            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Email == email);

            if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return Failure("Invalid email or password");

            if (!user.IsActive)
                return Failure("Account is deactivated");

            await EnsureSubscriptionStateAsync(user.Subscription);

            user.LastLoginAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return await CreateSuccessResultAsync(user, user.Subscription, "Login successful");
        }

        public async Task<AuthResult> RefreshTokenAsync(string refreshToken)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
                return Failure("Invalid refresh token");

            await CleanupRefreshTokensAsync();

            var tokenHash = HashToken(refreshToken);
            var storedToken = await _context.RefreshTokens
                .Include(t => t.User)
                .ThenInclude(u => u.Subscription)
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

            if (storedToken == null)
                return Failure("Invalid or expired refresh token");

            var user = storedToken.User;
            if (!user.IsActive || !await EnsureSubscriptionStateAsync(user.Subscription))
            {
                await RevokeTokenAsync(storedToken.Id);
                return Failure("Account is not eligible for a new session");
            }

            var revokedRows = await _context.RefreshTokens
                .Where(t => t.Id == storedToken.Id && t.RevokedAt == null && t.ExpiresAt > DateTime.UtcNow)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, DateTime.UtcNow));

            if (revokedRows != 1)
                return Failure("Refresh token has already been used or expired");

            var tokens = await GenerateTokensAsync(user);
            storedToken.ReplacedByTokenHash = HashToken(tokens.RefreshToken);
            await _context.SaveChangesAsync();

            return new AuthResult
            {
                Success = true,
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                User = CreateUserResponse(user, user.Subscription),
                Message = "Session refreshed"
            };
        }

        public async Task<UserResponse?> GetCurrentUserAsync(int userId)
        {
            var user = await _context.Users
                .Include(u => u.Subscription)
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
                return null;

            return CreateUserResponse(user, user.Subscription);
        }

        private async Task<bool> EnsureSubscriptionStateAsync(Subscription? subscription)
        {
            if (subscription == null)
                return false;

            var active = subscription.IsActive && subscription.EndDate > DateTime.UtcNow;
            if (!active && subscription.IsActive)
            {
                subscription.IsActive = false;
                subscription.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            return active;
        }

        private async Task CleanupRefreshTokensAsync()
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            await _context.RefreshTokens
                .Where(t => t.ExpiresAt < DateTime.UtcNow || (t.RevokedAt != null && t.RevokedAt < cutoff))
                .ExecuteDeleteAsync();
        }

        private async Task RevokeTokenAsync(int tokenId)
        {
            await _context.RefreshTokens
                .Where(t => t.Id == tokenId && t.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
        }

        private async Task<AuthResult> CreateSuccessResultAsync(User user, Subscription subscription, string message)
        {
            var tokens = await GenerateTokensAsync(user);
            return new AuthResult
            {
                Success = true,
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                User = CreateUserResponse(user, subscription),
                Message = message
            };
        }

        private async Task<(string AccessToken, string RefreshToken)> GenerateTokensAsync(User user)
        {
            var accessToken = GenerateAccessToken(user);
            var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

            _context.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = HashToken(refreshToken),
                ExpiresAt = DateTime.UtcNow.AddDays(_configuration.GetValue<int?>("Jwt:RefreshTokenExpiryDays") ?? 7)
            });

            await _context.SaveChangesAsync();
            return (accessToken, refreshToken);
        }

        private string GenerateAccessToken(User user)
        {
            var keyValue = _configuration["Jwt:Key"]
                ?? throw new InvalidOperationException("JWT signing key is not configured.");
            var issuer = _configuration["Jwt:Issuer"]
                ?? throw new InvalidOperationException("JWT issuer is not configured.");
            var audience = _configuration["Jwt:Audience"]
                ?? throw new InvalidOperationException("JWT audience is not configured.");
            var expiryInHours = _configuration.GetValue<int?>("Jwt:ExpiryInHours") ?? 24;

            if (keyValue.Length < 32)
                throw new InvalidOperationException("JWT signing key must be at least 32 characters long.");

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Role, user.Role)
            };

            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddHours(expiryInHours),
                Issuer = issuer,
                Audience = audience,
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyValue)),
                    SecurityAlgorithms.HmacSha256Signature)
            };

            var handler = new JwtSecurityTokenHandler();
            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        private static UserResponse CreateUserResponse(User user, Subscription? subscription) => new()
        {
            Id = user.Id,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            SubscriptionEnd = subscription?.EndDate,
            IsTrial = subscription?.IsTrial ?? false,
            IsSubscriptionActive = subscription != null && subscription.IsActive && subscription.EndDate > DateTime.UtcNow,
            IsEmailVerified = user.IsEmailVerified,
            IsActive = user.IsActive,
            Role = user.Role,
            IsAutoTradeEnabled = user.IsAutoTradeEnabled
        };

        private static AuthResult Failure(string message) => new() { Success = false, Message = message };

        private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

        private static string NormalizeName(string value) => string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private static string HashToken(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        // --- THIS IS THE FIXED PASSWORD RULE ---
        private static bool HasStrongPassword(string password) =>
            password.Length >= 8 &&
            password.Length <= 128;
    }
}