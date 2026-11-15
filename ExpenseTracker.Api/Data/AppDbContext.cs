using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<SubscriptionItem> Subscriptions => Set<SubscriptionItem>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<MonthlyBudget> Budgets => Set<MonthlyBudget>();

    public Task<User?> FindUserByUsernameAsync(string username)
    {
        var trimmed = username.Trim();
        // PostgreSQL applies the same case mapping/collation to both operands and
        // to the unique expression index. Do not mix database and thread cultures.
        return Database.IsRelational()
            ? Users.FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE lower(\"Username\") = lower({trimmed})").SingleOrDefaultAsync()
            : Users.SingleOrDefaultAsync(u => u.Username.ToLowerInvariant() == trimmed.ToLowerInvariant());
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<SubscriptionItem>())
        {
            if (entry.State == EntityState.Added)
                entry.Entity.CreatedAt = now;
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }
        foreach (var entry in ChangeTracker.Entries<MonthlyBudget>())
        {
            if (entry.State == EntityState.Added) entry.Entity.CreatedAt = now;
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Entity.Version = Guid.NewGuid();
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MonthlyBudget>(entity =>
        {
            entity.ToTable(table => table.HasCheckConstraint("CK_Budgets_MonthlyLimit", "\"MonthlyLimit\" > 0 AND \"MonthlyLimit\" <= 1000000"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(100).IsRequired();
            entity.Property(e => e.CategoryKey).HasMaxLength(100).IsRequired();
            entity.Property(e => e.MonthlyLimit).HasColumnType("numeric(18,2)");
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.HasIndex(e => new { e.UserId, e.Currency, e.CategoryKey }).IsUnique();
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubscriptionItem>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                  .IsRequired()
                  .HasMaxLength(100);

            entity.Property(e => e.Cost)
                  .HasColumnType("numeric(18,2)");

            entity.Property(e => e.Currency)
                  .IsRequired()
                  .HasMaxLength(10);

            entity.Property(e => e.Category)
                  .HasMaxLength(100);

            entity.Property(e => e.BillingPeriod)
                  .HasConversion<string>();

            entity.Property(e => e.NextBillingDate)
                  .HasColumnType("date");

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Username)
                  .IsRequired()
                  .HasMaxLength(50);

            entity.HasIndex(e => e.Username)
                  .IsUnique();
            // The migration additionally owns an expression index on lower(Username).
            // EF cannot model expression indexes; retain it in future migrations.

            entity.Property(e => e.Email)
                  .IsConcurrencyToken()
                  .HasMaxLength(256);

            entity.Property(e => e.EmailVerified).IsConcurrencyToken();
            entity.Property(e => e.PendingEmail).HasMaxLength(256).IsConcurrencyToken();
            entity.Property(e => e.VerificationEmailSentAt).IsConcurrencyToken();
            entity.Property(e => e.LoginFailureCount).IsConcurrencyToken().HasDefaultValue(0);
            entity.Property(e => e.LastLoginFailureAt).IsConcurrencyToken();
            entity.Property(e => e.LoginBlockedUntil).IsConcurrencyToken();

            // Unique per-email; PostgreSQL treats NULLs as distinct, so legacy
            // accounts without an email don't collide.
            entity.HasIndex(e => e.Email)
                  .IsUnique();

            entity.Property(e => e.PasswordHash)
                  .IsConcurrencyToken()
                  .IsRequired();
        });

        modelBuilder.Entity<VerificationCode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Attempts).IsConcurrencyToken();
            entity.Property(e => e.ConsumedAt).IsConcurrencyToken();

            entity.Property(e => e.CodeHash)
                  .IsRequired()
                  .HasMaxLength(120);

            entity.Property(e => e.Purpose)
                  .HasConversion<string>()
                  .HasMaxLength(40);

            entity.HasIndex(e => new { e.UserId, e.Purpose });

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RevokedAt).IsConcurrencyToken();
            entity.Property(e => e.WasRotated).IsConcurrencyToken().HasDefaultValue(false);

            entity.Property(e => e.TokenHash)
                  .IsRequired()
                  .HasMaxLength(120);

            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Ignore(e => e.IsActive);
        });
    }
}

