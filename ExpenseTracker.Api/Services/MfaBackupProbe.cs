using ExpenseTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Services;

public static class MfaBackupProbe
{
    // Operator-only CLI, no HTTP endpoint or hosted worker. Never prints secrets.
    public static async Task<int> Run(IConfiguration config)
    {
        try
        {
            var options=new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(config.GetConnectionString("DefaultConnection")).Options;
            await using var db=new AppDbContext(options);var crypto=new MfaSecrets(config);var checkedCount=0;
            if(!crypto.KeyConfigured)throw new System.Security.Cryptography.CryptographicException("MFA key required");
            await foreach(var user in db.Users.AsNoTracking().Where(u=>u.MfaEnabled||u.MfaSecret!=null||u.MfaPendingSecret!=null).AsAsyncEnumerable())
            {
                if(user.MfaEnabled&&user.MfaSecret is null)throw new InvalidOperationException("Missing MFA secret");
                foreach(var envelope in new[]{user.MfaSecret,user.MfaPendingSecret}.Where(s=>s is not null))
                {
                    var secret=crypto.Unprotect(user.Id,envelope!);
                    if(secret.Length!=32||secret.Any(c=>!"ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(c)))
                        throw new InvalidOperationException("Invalid MFA secret");
                    checkedCount++;
                }
            }
            Console.WriteLine("MFA encrypted backup records verified: "+checkedCount);
            return 0;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine("MFA backup verification failed: "+ex.GetType().Name);
            return 1;
        }
    }
}
