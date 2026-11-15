using System.Text;

namespace ExpenseTracker.Api.Services;

public static class PasswordRules
{
    public static bool Verify(string password, string hash) =>
        Encoding.UTF8.GetByteCount(password) <= 72 && BCrypt.Net.BCrypt.Verify(password, hash);
}
