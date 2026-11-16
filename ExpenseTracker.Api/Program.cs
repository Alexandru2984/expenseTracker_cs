using System.Net;
using System.Security.Claims;
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
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = 1_100_000;
        options.AddServerHeader = false;
    });

    // ── Configure Forwarded Headers (for Cloudflare → Nginx → Docker) ─────────
    // Nginx supplies one verified client address and scheme. Trust only loopback
    // by default; Docker deployments explicitly configure their bridge gateway.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
        ProxyTrust.Configure(options, builder.Configuration["ForwardedHeaders:TrustedNetworks"]));

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
        if (string.IsNullOrWhiteSpace(builder.Configuration["Smtp:Host"]))
        {
            Log.Fatal("Smtp__Host is required in Production. Verification codes must never be logged.");
            return 1;
        }
    }

    // ── Services ──────────────────────────────────────────────────────────────
    builder.Services.AddHttpClient();
    builder.Services.AddHttpClient("CurrencyRates", client => client.MaxResponseContentBufferSize = 512_000)
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddMemoryCache();
    builder.Services.AddScoped<CurrencyService>();
    builder.Services.AddSingleton<AuthTokenService>();
    builder.Services.AddScoped<EmailService>();
    builder.Services.AddScoped<VerificationService>();
    builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
    builder.Services.AddSingleton<IReminderEmailSender, ReminderEmailSender>();
    builder.Services.AddScoped<NotificationContent>();
    builder.Services.AddScoped<NotificationEngine>();

    // Background housekeeping (skipped under the in-memory test provider, which
    // doesn't support the bulk ExecuteDelete used by the cleanup).
    if (!builder.Environment.IsEnvironment("Testing"))
    {
        builder.Services.AddHostedService<TokenCleanupService>();
        builder.Services.AddHostedService<NotificationWorker>();
    }

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false));
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
                },
                OnTokenValidated = async context =>
                {
                    var principal = context.Principal!;
                    if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                        || !Guid.TryParse(principal.FindFirstValue("session_id"), out var sessionId))
                    {
                        context.Fail("Session is missing.");
                        return;
                    }
                    var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                    var now = DateTime.UtcNow;
                    var session = await db.RefreshTokens.AsNoTracking().Include(t => t.User)
                        .SingleOrDefaultAsync(t => t.Id == sessionId && t.UserId == userId
                            && t.RevokedAt == null && t.ExpiresAt > now
                            && !t.TokenHash.StartsWith("finance:"), context.HttpContext.RequestAborted);
                    var tokens = context.HttpContext.RequestServices.GetRequiredService<AuthTokenService>();
                    if (session is null || principal.FindFirstValue("credential_stamp") != tokens.CredentialStamp(session.User))
                        context.Fail("Session has been revoked or credentials changed.");
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
                policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        });
    });

    // ── Rate limiting (keyed on the real client IP, see ClientIp) ─────────────
    // Limits are effectively disabled under the "Testing" environment so the
    // integration suite doesn't trip the fixed window across tests.
    var isTesting = builder.Environment.IsEnvironment("Testing");
    int Permit(int normal) => isTesting ? 1_000_000 : normal;

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Global policy: 100 req/min per IP
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp.Partition(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Permit(100),
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

        // Stricter policy for writes: 20 req/min per IP
        options.AddPolicy("writes", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp.Partition(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Permit(20),
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

        // Strict policy for auth endpoints (brute-force / credential stuffing): 10 req/min per IP
        // Identity reads synchronize tabs without consuming the password/OTP bucket.
        options.AddPolicy("auth-status", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp.Partition(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Permit(60), Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));

        options.AddPolicy("auth", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ClientIp.Partition(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Permit(10),
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
    // The static SPA gets a separate HTTP CSP with a per-response nonce from
    // Nginx, including frame-ancestors/HSTS — see ops and deploy.md.
    app.Use(async (ctx, next) =>
    {
        var headers = ctx.Response.Headers;
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-store";
            headers["X-Robots-Tag"] = "noindex, nofollow";
        }
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
            var isConflict = feature?.Error is DbUpdateConcurrencyException;

            ctx.Response.StatusCode = isConflict ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/problem+json";

            var pd = new ProblemDetails
            {
                Status = ctx.Response.StatusCode,
                Title = isConflict ? "Datele au fost modificate între timp. Reîncearcă operația." : "An unexpected error occurred.",
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

    // SameSite does not isolate sibling subdomains. Validate browser origin on
    // every mutation, including anonymous login, refresh and logout endpoints.
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
            && !HttpMethods.IsOptions(ctx.Request.Method)
            && !RequestOrigin.IsAllowed(ctx.Request, allowedOrigins))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 403, Title = "Request origin rejected." });
            return;
        }
        await next();
    });

    app.UseRateLimiter();

    app.UseAuthentication();
    app.UseAuthorization();

    // Cookies are shared between tabs. Bind SPA requests to the account whose
    // form was opened, so a missed/delayed cross-tab notification cannot retarget it.
    app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.Request.Path.StartsWithSegments("/api")
            && !ctx.Request.Path.StartsWithSegments("/api/auth") && !ctx.Request.Path.StartsWithSegments("/api/integrations/finance")
            && Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && !ctx.RequestServices.GetRequiredService<AuthTokenService>().MatchesAccountContext(ctx.Request, userId))
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsJsonAsync(BrowserAccountContext.Changed());
            return;
        }
        await next();
    });

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

// Exposed so the integration test host (WebApplicationFactory) can reference it.
public partial class Program { }

