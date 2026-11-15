using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Services;

/// <summary>Account-wide backoff, persisted by the caller while holding the user row lock.</summary>
public static class LoginProtection
{
    public static bool IsBlocked(User user, DateTime now) => user.LoginBlockedUntil > now;

    public static void Fail(User user, DateTime now)
    {
        if (IsBlocked(user, now)) return; // Traffic during a block must not extend it.
        if (user.LastLoginFailureAt is null || now - user.LastLoginFailureAt >= TimeSpan.FromMinutes(15))
            Reset(user);
        user.LoginFailureCount = Math.Min(10, user.LoginFailureCount + 1);
        user.LastLoginFailureAt = now;
        user.LoginBlockedUntil = user.LoginFailureCount < 5 ? null
            : now.AddSeconds(Math.Min(300, 30 * (1 << (user.LoginFailureCount - 5))));
    }

    public static void Reset(User user)
    {
        user.LoginFailureCount = 0;
        user.LastLoginFailureAt = null;
        user.LoginBlockedUntil = null;
    }
}
