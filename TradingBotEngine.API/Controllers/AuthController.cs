using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using TradingBotEngine.Services;
using TradingBotEngine.Services.Models;

namespace TradingBotEngine.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _authService;

        public AuthController(AuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("register")]
        [EnableRateLimiting("auth")]
        [RequestSizeLimit(16 * 1024)]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            var result = await _authService.RegisterAsync(request);
            if (!result.Success)
                return BadRequest(new { message = result.Message });

            return Ok(new
            {
                message = result.Message,
                accessToken = result.AccessToken,
                refreshToken = result.RefreshToken,
                user = result.User
            });
        }

        [HttpPost("login")]
        [EnableRateLimiting("auth")]
        [RequestSizeLimit(16 * 1024)]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var result = await _authService.LoginAsync(request);
            if (!result.Success)
                return Unauthorized(new { message = result.Message });

            return Ok(new
            {
                accessToken = result.AccessToken,
                refreshToken = result.RefreshToken,
                user = result.User
            });
        }

        [HttpPost("refresh")]
        [EnableRateLimiting("auth")]
        [RequestSizeLimit(16 * 1024)]
        public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
        {
            var result = await _authService.RefreshTokenAsync(request.RefreshToken);
            if (!result.Success)
                return Unauthorized(new { message = result.Message });

            return Ok(new
            {
                accessToken = result.AccessToken,
                refreshToken = result.RefreshToken,
                user = result.User
            });
        }

        // The refresh token is the credential here, so no JWT is required (the access token
        // is often already expired when the user logs out).
        [HttpPost("logout")]
        [EnableRateLimiting("auth")]
        [RequestSizeLimit(16 * 1024)]
        public async Task<IActionResult> Logout([FromBody] RefreshRequest request)
        {
            await _authService.LogoutAsync(request.RefreshToken);
            return NoContent();
        }

        [HttpGet("me")]
        [Authorize]
        public async Task<IActionResult> GetMe()
        {
            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdValue, out var userId))
                return Unauthorized(new { message = "Invalid user identity." });

            var user = await _authService.GetCurrentUserAsync(userId);
            return user == null ? NotFound(new { message = "User not found." }) : Ok(user);
        }
    }
}
