using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Api.Data;

namespace ExpenseTracker.Api.Services;

/// <summary>
/// Periodically purges expired refresh tokens and expired verification codes so
/// those tables don't grow unbounded. Revoked/consumed rows are kept for a short
/// retention window (for auditing) before they too are removed. Runs on startup
/// and then once a day.
/// </summary>
public class TokenCleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TokenCleanupService> _logger;

    public TokenCleanupService(IServiceScopeFactory scopeFactory, ILogger<TokenCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Token cleanup failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var cutoff = now - Retention;

        var tokens = await db.RefreshTokens
            .Where(t => t.ExpiresAt < now || (t.RevokedAt != null && t.RevokedAt < cutoff))
            .ExecuteDeleteAsync(ct);

        var codes = await db.VerificationCodes
            .Where(c => c.ExpiresAt < now || (c.ConsumedAt != null && c.ConsumedAt < cutoff))
            .ExecuteDeleteAsync(ct);

        if (tokens > 0 || codes > 0)
            _logger.LogInformation(
                "Token cleanup removed {Tokens} refresh tokens and {Codes} verification codes.",
                tokens, codes);
    }
}
