using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Infrastructure;
using ExpenseTracker.Api.Services;

// ── Bootstrap logger (captures startup errors before host is built) ───────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Configure Forwarded Headers (for Cloudflare → Nginx → Docker) ─────────
    // We only trust forwarded headers coming from our reverse proxy, which lives
    // on a private network (Docker bridge / host loopback). Trusting "everything"
    // (the old KnownNetworks/Proxies.Clear()) let any client spoof X-Forwarded-For
    // and bypass rate limiting / poison logs. Override the trusted ranges with
    // ForwardedHeaders__TrustedNetworks (comma-separated CIDRs) if needed.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = null; // bounded by KnownNetworks/KnownProxies instead
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        var configured = builder.Configuration["ForwardedHeaders:TrustedNetworks"];
        var cidrs = string.IsNullOrWhiteSpace(configured)
            ? new[] { "127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var cidr in cidrs)
        {
            var parts = cidr.Split('/');
            if (parts.Length == 2 &&
                IPAddress.TryParse(parts[0], out var prefix) &&
                int.TryParse(parts[1], out var len))
            {
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, len));
            }
        }
    });

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
    builder.Services.AddHttpClient();
    builder.Services.AddMemoryCache();
    builder.Services.AddScoped<CurrencyService>();
    builder.Services.AddSingleton<AuthTokenService>();
    builder.Services.AddScoped<EmailService>();
    builder.Services.AddScoped<VerificationService>();

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
                IssuerSigningKey = jwtKey,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            // Access token now lives in an httpOnly cookie, not the Authorization
            // header. Pull it from the cookie when no bearer header is present.
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (string.IsNullOrEmpty(context.Token))
                    {
                        var cookie = context.Request.Cookies[AuthTokenService.AccessCookie];
                        if (!string.IsNullOrEmpty(cookie))
                            context.Token = cookie;
                    }
                    return Task.CompletedTask;
                }
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

    // ── Rate limiting (keyed on the real client IP, see ClientIp) ─────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Global policy: 100 req/min per IP
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp.Partition(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

        // Stricter policy for writes: 20 req/min per IP
        options.AddPolicy("writes", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp.Partition(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

        // Strict policy for auth endpoints (brute-force / credential stuffing): 10 req/min per IP
        options.AddPolicy("auth", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp.Partition(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
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
    app.UseForwardedHeaders();

    // ── Security headers (defense in depth; applies to API responses) ─────────
    // The static SPA (served by Nginx) additionally carries a CSP <meta> tag and
    // should get frame-ancestors/HSTS from Nginx — see deploy.md.
    app.Use(async (ctx, next) =>
    {
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), payment=(), usb=()";
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        if (ctx.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        await next();
    });

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

    // ── CSRF: double-submit cookie ────────────────────────────────────────────
    // Cookies are sent automatically by the browser, so we require that unsafe
    // /api requests echo the readable csrf cookie back in an X-CSRF-Token header
    // (a value a cross-site attacker cannot read). A csrf cookie is (re)issued on
    // any request that lacks one, so the SPA always has a token to send.
    app.Use(async (ctx, next) =>
    {
        var cookieToken = ctx.Request.Cookies[AuthTokenService.CsrfCookie];
        var isApi = ctx.Request.Path.StartsWithSegments("/api");
        // /api/auth (login/register/refresh/logout) is exempt: those flows don't
        // act on a pre-existing authenticated cookie session and are already
        // protected by SameSite=Strict cookies + the strict 'auth' rate limiter.
        // Exempting them also avoids the first-request bootstrap problem.
        var isAuthPath = ctx.Request.Path.StartsWithSegments("/api/auth");
        var method = ctx.Request.Method;
        var isUnsafe = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
                    || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

        if (isApi && isUnsafe && !isAuthPath)
        {
            var headerToken = ctx.Request.Headers[AuthTokenService.CsrfHeader].FirstOrDefault();
            if (string.IsNullOrEmpty(cookieToken) || string.IsNullOrEmpty(headerToken) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(cookieToken), Encoding.UTF8.GetBytes(headerToken)))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "application/problem+json";
                await ctx.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = 403,
                    Title = "CSRF validation failed."
                });
                return;
            }
        }

        if (string.IsNullOrEmpty(cookieToken))
        {
            ctx.Response.Cookies.Append(AuthTokenService.CsrfCookie, AuthTokenService.CreateCsrfToken(),
                new CookieOptions
                {
                    HttpOnly = false, // readable so the SPA can echo it in the header
                    Secure = ctx.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    Path = "/"
                });
        }

        await next();
    });

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

