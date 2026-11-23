using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using TradingBotEngine.API.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.SignalR; 
using Microsoft.OpenApi.Models;
using System.Text;
using TradingBotEngine.API.Hubs;
using TradingBotEngine.Core;
using TradingBotEngine.Data;
using TradingBotEngine.Services;

var builder = WebApplication.CreateBuilder(args);

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is not configured.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Jwt:Issuer is not configured.");
var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Jwt:Audience is not configured.");

if (jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters long.");

// The sample key from appsettings.json lets anyone forge tokens. Refuse to start with it
// outside Development.
if (jwtKey.Contains("YourSuperSecretKey", StringComparison.OrdinalIgnoreCase) &&
    !builder.Environment.IsDevelopment())
    throw new InvalidOperationException(
        "Jwt:Key is still the sample value. Set a unique secret (for example the environment variable Jwt__Key).");

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };

builder.Services.AddControllers();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 16 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16 * 1024;
});

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddMemoryCache();
// Broker API keys are encrypted with these keys. Without a persistent location they are lost
// on restart or redeploy (Docker, Azure) and every stored broker key becomes unreadable.
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
var dataProtectionBuilder = builder.Services.AddDataProtection();

var dataProtectionPersisted = false;
var keyRingSecret = builder.Configuration["DataProtection:KeyEncryptionSecret"];

if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    dataProtectionBuilder
        .SetApplicationName("TradingBotEngine")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
    dataProtectionPersisted = true;
}
else
{
    // No disk configured (e.g. Render free tier): keep the key ring in the app's own database so
    // stored broker keys survive restarts and redeploys. Set DataProtection:KeyEncryptionSecret
    // to also encrypt the key ring, so a database leak alone cannot decrypt broker keys.
    var keyRingConnection = builder.Configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrWhiteSpace(keyRingConnection))
    {
        dataProtectionBuilder.SetApplicationName("TradingBotEngine");
        dataProtectionBuilder.Services.Configure<KeyManagementOptions>(options =>
            options.XmlRepository = new PostgresXmlRepository(keyRingConnection, keyRingSecret));
        dataProtectionPersisted = true;
    }
}

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Render (and most hosts) put a proxy in front of the app. Without this, every request
// appears to come from the proxy's IP and all users share one rate-limit bucket.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("trading", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services.AddSingleton<ICredentialProtector, CredentialProtector>();

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<BrokerService>();
builder.Services.AddScoped<ExchangeMarketDataService>();
builder.Services.AddScoped<IndicatorCalculator>();
builder.Services.AddScoped<SignalGenerator>();
builder.Services.AddScoped<IReconciliationService, ReconciliationService>();
builder.Services.AddHostedService<AutoTradeService>();
builder.Services.AddSingleton<ITradingRealtimePublisher, TradingBotEngine.API.Hubs.SignalRTradingRealtimePublisher>();
builder.Services.AddSingleton<IUserIdProvider, TradingBotEngine.API.Hubs.TradingHubUserIdProvider>();
builder.Services.AddScoped<TelegramCommandHandler>();
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddHostedService<TelegramBotService>();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrWhiteSpace(accessToken) && path.StartsWithSegments("/tradingHub"))
                context.Token = accessToken;

            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "TradingBotEngine API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer [token]'.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddSignalR();

// Bybit "testnet" connections use Bybit Demo Trading when this is on (see BybitEnvironments).
BybitEnvironments.UseDemoTrading = builder.Configuration.GetValue<bool>("Bybit:UseDemoTrading");
BinanceEnvironments.UseDemoTrading = builder.Configuration.GetValue<bool>("Binance:UseDemoTrading");
var app = builder.Build();

app.UseForwardedHeaders();

// Self-heal: the code expects this column, but a database created from older migrations may not
// have it, which makes every broker-connection query (including the bot worker) fail.
// "IF NOT EXISTS" makes this safe to run on every start.
try
{
    var schemaConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrWhiteSpace(schemaConnectionString))
    {
        using var schemaConnection = new Npgsql.NpgsqlConnection(schemaConnectionString);
        schemaConnection.Open();
        using var schemaCommand = new Npgsql.NpgsqlCommand(
            "ALTER TABLE \"BrokerConnections\" ADD COLUMN IF NOT EXISTS \"IsFutures\" boolean NOT NULL DEFAULT false; " +
            "ALTER TABLE \"BrokerConnections\" ADD COLUMN IF NOT EXISTS \"EquityHighWaterMark\" numeric NOT NULL DEFAULT 0; " +
            "ALTER TABLE \"BrokerConnections\" ADD COLUMN IF NOT EXISTS \"EquityHighWaterMarkUpdatedAt\" timestamp with time zone NULL; " +
            "ALTER TABLE \"BrokerConnections\" ADD COLUMN IF NOT EXISTS \"FuturesEquityHighWaterMark\" numeric NOT NULL DEFAULT 0; " +
            "ALTER TABLE \"BrokerConnections\" ADD COLUMN IF NOT EXISTS \"FuturesEquityHighWaterMarkUpdatedAt\" timestamp with time zone NULL;",
            schemaConnection);
        schemaCommand.ExecuteNonQuery();
        app.Logger.LogInformation("Schema check done: BrokerConnections columns are present.");
    }
}
catch (Exception schemaException)
{
    app.Logger.LogError(schemaException, "Schema self-heal for BrokerConnections.IsFutures failed.");
}
app.UseExceptionHandler();

if (!dataProtectionPersisted && !app.Environment.IsDevelopment())
    app.Logger.LogWarning(
        "No persistent Data Protection key store. Stored broker API keys will be unreadable after a restart or redeploy.");

if (string.IsNullOrWhiteSpace(dataProtectionKeysPath) && dataProtectionPersisted && string.IsNullOrWhiteSpace(keyRingSecret))
    app.Logger.LogWarning(
        "DataProtection:KeyEncryptionSecret is not set: the Data Protection key ring is stored unencrypted in the database. Set it so a database leak cannot decrypt broker keys.");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// app.UseHttpsRedirection(); // Handled by Render's load balancer 
app.UseCors("AllowReactApp");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<TradingHub>("/tradingHub");

app.Run();

public partial class Program { }
