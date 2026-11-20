using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TradingBotEngine.API.DTOs.Broker;
using TradingBotEngine.Data.Models;
using TradingBotEngine.Services;

namespace TradingBotEngine.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BrokerController : ControllerBase
{
    private readonly BrokerService _brokerService;

    public BrokerController(BrokerService brokerService)
    {
        _brokerService = brokerService;
    }

    [HttpGet("connections")]
    public async Task<IActionResult> GetConnections()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new
            {
                message = "Invalid user identity."
            });
        }

        var connections =
            await _brokerService.GetConnectionsAsync(userId);

        var response = connections
            .Select(connection => new BrokerConnectionResponseDto
            {
                Id = connection.Id,
                BrokerName = connection.BrokerName,
                IsConnected = connection.IsConnected,
                IsTestnet = connection.IsTestnet,
                IsLiveTradingEnabled =
                    connection.IsLiveTradingEnabled,
                LastConnectedAt =
                    connection.LastConnectedAt,
                LastDisconnectedAt =
                    connection.LastDisconnectedAt
            })
            .ToList();

        return Ok(response);
    }

    [HttpPost("connect")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Connect(
        [FromBody] ConnectBrokerRequestDto request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new
            {
                message = "Invalid user identity."
            });
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var connection = new BrokerConnection
        {
            UserId = userId,
            BrokerName = request.Broker.Trim(),
            ApiKey = request.ApiKey.Trim(),
            ApiSecret = request.ApiSecret.Trim(),

            IsActive = true,
            IsConnected = false,

            // All new connections begin in testnet.
            IsTestnet = true,
            IsLiveTradingEnabled = false,

 IsFutures = request.UseFutures,  


            CreatedAt = DateTime.UtcNow
        };

        var connected =
            await _brokerService.ConnectAsync(
                userId,
                connection);

        if (!connected)
        {
            return BadRequest(new
            {
                success = false,
                message =
                    "Broker connection failed. Verify the credentials and testnet account."
            });
        }

        var connections = await _brokerService.GetConnectionsAsync(userId);
        var responseConnectionId = connections
            .Where(x => x.BrokerName.Equals(connection.BrokerName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.LastConnectedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefault();

        var response = new ConnectBrokerResponseDto
        {
            Success = true,
            Message = "Broker connected in TESTNET mode.",
            IsTestnet = true,
            ConnectionId = responseConnectionId
        };

        return Ok(response);
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(
        [FromBody] DisconnectBrokerRequestDto? request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new
            {
                message = "Invalid user identity."
            });
        }

        var success =
            await _brokerService.DisconnectAsync(
                userId,
                request?.ConnectionId);

        if (!success)
        {
            return NotFound(new
            {
                success = false,
                message =
                    "No active broker connection found."
            });
        }

        return Ok(new BrokerActionResponseDto
        {
            Success = true,
            Message = "Broker disconnected."
        });
    }

    [HttpPost("{connectionId:int}/live-trading")]
    public async Task<IActionResult> SetLiveTrading(
        int connectionId,
        [FromBody] LiveTradingRequestDto request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new
            {
                message = "Invalid user identity."
            });
        }

        if (!request.Confirm)
        {
            return BadRequest(new
            {
                message =
                    "Explicit confirmation is required before enabling live trading."
            });
        }

        var success =
            await _brokerService.SetLiveTradingOptInAsync(
                userId,
                connectionId,
                request.Enabled);

        if (!success)
        {
            return NotFound(new
            {
                message =
                    "Broker connection not found."
            });
        }

        return Ok(new LiveTradingResponseDto
        {
            Success = true,
            IsLiveTradingEnabled = request.Enabled,
            IsTestnet = !request.Enabled
        });
    }

    private bool TryGetUserId(out int userId)
    {
        return int.TryParse(
            User.FindFirstValue(
                ClaimTypes.NameIdentifier),
            out userId);
    }
}