using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using ExpenseTracker.Api.Data;

// ── Bootstrap logger (captures startup errors before host is built) ───────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ───────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext()
           .WriteTo.Console(outputTemplate:
               "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

    // ── Validate required secrets at startup ──────────────────────────────────
    if (builder.Environment.IsProduction())
    {
        var jwtSecret = builder.Configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < 32)
        {
            Log.Fatal("Jwt__Secret environment variable is not set or too short (min 32 chars). Refusing to start in Production.");
            return 1;
        }
        var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            Log.Fatal("ConnectionStrings__DefaultConnection is not set. Refusing to start in Production.");
            return 1;
        }
    }

    // ── Services ──────────────────────────────────────────────────────────────
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // ── Database ──────────────────────────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));

    // ── Authentication (JWT) ──────────────────────────────────────────────────
    var jwtSecretValue = builder.Configuration["Jwt:Secret"] ?? "dev-jwt-secret-change-me-in-prod-32ch";
    var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecretValue));

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = "ExpenseTracker",
                ValidAudience = "ExpenseTracker",
                IssuerSigningKey = jwtKey
            };
        });

    builder.Services.AddAuthorization();

    // ── CORS (origins from config) ─────────────────────────────────────────────
    var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Frontend", policy =>
        {
            if (allowedOrigins.Length > 0)
                policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
        });
    });

    // ── Rate limiting ─────────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Global policy: 100 req/min per IP
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, IPAddress>(ctx =>
        {
            var ip = ctx.Connection.RemoteIpAddress ?? IPAddress.Loopback;
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
        });

        // Stricter policy for writes: 20 req/min per IP
        options.AddPolicy("writes", ctx =>
        {
            var ip = ctx.Connection.RemoteIpAddress ?? IPAddress.Loopback;
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
        });
    });

    // ── Health checks ─────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddNpgSql(connectionString, name: "postgres");

    // ── Problem details (RFC 7807) ────────────────────────────────────────────
    builder.Services.AddProblemDetails();

    var app = builder.Build();

    // ── Auto-migrate in Development only ──────────────────────────────────────
    if (app.Environment.IsDevelopment())
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();
    }

    // ── Middleware Pipeline ───────────────────────────────────────────────────
    app.UseExceptionHandler(errApp =>
    {
        errApp.Run(async ctx =>
        {
            var feature = ctx.Features.Get<IExceptionHandlerFeature>();
            var isDev = app.Environment.IsDevelopment();

            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/problem+json";

            var pd = new ProblemDetails
            {
                Status = 500,
                Title = "An unexpected error occurred.",
                Detail = isDev ? feature?.Error.ToString() : null
            };

            await ctx.Response.WriteAsJsonAsync(pd);
        });
    });

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors("Frontend");

    app.UseRateLimiter();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthChecks("/health").AllowAnonymous();
    app.MapControllers();

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

