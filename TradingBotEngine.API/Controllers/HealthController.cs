using Microsoft.AspNetCore.Mvc;

namespace TradingBotEngine.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    [HttpHead]
    public IActionResult Get() 
    {
        return Ok(new { status = "Healthy", timestamp = DateTime.UtcNow });
    }
}   