using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<SubscriptionItem> Subscriptions => Set<SubscriptionItem>();

    public DbSet<AccountDeletionRecord> AccountDeletionRecords => Set<AccountDeletionRecord>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<MonthlyBudget> Budgets => Set<MonthlyBudget>();
    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();
    public DbSet<GoalContribution> GoalContributions => Set<GoalContribution>();
    public DbSet<SubscriptionPriceChange> SubscriptionPriceChanges => Set<SubscriptionPriceChange>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<NotificationOutbox> NotificationOutbox => Set<NotificationOutbox>();
    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();
    public DbSet<MfaLoginChallenge> MfaLoginChallenges => Set<MfaLoginChallenge>();

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
        var changes = new List<SubscriptionPriceChange>();
        foreach (var entry in ChangeTracker.Entries<SubscriptionItem>().ToList())
        {
            if (entry.State == EntityState.Modified
                && (entry.Property(s => s.Cost).OriginalValue != entry.Entity.Cost
                    || entry.Property(s => s.Currency).OriginalValue != entry.Entity.Currency
                    || entry.Property(s => s.BillingPeriod).OriginalValue != entry.Entity.BillingPeriod))
                changes.Add(new SubscriptionPriceChange
                {
                    SubscriptionId = entry.Entity.Id, ChangedAt = now,
                    PreviousCost = entry.Property(s => s.Cost).OriginalValue,
                    PreviousCurrency = entry.Property(s => s.Currency).OriginalValue,
                    PreviousBillingPeriod = entry.Property(s => s.BillingPeriod).OriginalValue,
                    Cost = entry.Entity.Cost, Currency = entry.Entity.Currency, BillingPeriod = entry.Entity.BillingPeriod
                });
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
        SubscriptionPriceChanges.AddRange(changes);
        foreach (var entry in ChangeTracker.Entries<SavingsGoal>())
        {
            if (entry.State == EntityState.Added) entry.Entity.CreatedAt = now;
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Entity.Version = Guid.NewGuid();
            }
        }
        foreach (var entry in ChangeTracker.Entries<NotificationOutbox>())
            if (entry.State is EntityState.Added or EntityState.Modified) entry.Entity.Version = Guid.NewGuid();
        return SaveWithHistoryAsync(changes, cancellationToken);
    }

    private async Task<int> SaveWithHistoryAsync(List<SubscriptionPriceChange> changes, CancellationToken ct)
    {
        try { return await base.SaveChangesAsync(ct); }
        catch
        {
            // Retrying a failed save in the same context must not duplicate events.
            foreach (var change in changes) Entry(change).State = EntityState.Detached;
            throw;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<MfaRecoveryCode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CodeHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ConsumedAt).IsConcurrencyToken();
            entity.HasIndex(e => new { e.UserId, e.CodeHash }).IsUnique();
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MfaLoginChallenge>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CredentialStamp).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Attempts).IsConcurrencyToken();
            entity.Property(e => e.ConsumedAt).IsConcurrencyToken();
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.ExpiresAt });
            entity.ToTable(table=>table.HasCheckConstraint("CK_MfaLoginChallenges_Attempts","\"Attempts\" BETWEEN 0 AND 5"));
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.TimeZone).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.HasIndex(e => new { e.Enabled, e.NextDigestAtUtc });
            entity.HasOne(e => e.User).WithOne().HasForeignKey<NotificationPreference>(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(table => table.HasCheckConstraint("CK_NotificationPreferences_Ranges",
                "\"SendHour\" BETWEEN 0 AND 23 AND \"LeadDays\" BETWEEN 0 AND 14"));
        });
        modelBuilder.Entity<NotificationOutbox>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TimeZone).HasMaxLength(100).IsRequired();
            entity.Property(e => e.RecipientHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.ErrorCode).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.HasIndex(e => new { e.UserId, e.LocalDate }).IsUnique();
            entity.HasIndex(e => new { e.State, e.NextAttemptAtUtc });
            entity.HasIndex(e => new { e.State, e.LeaseExpiresAtUtc });
            entity.HasIndex(e => e.CreatedAt);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(table => table.HasCheckConstraint("CK_NotificationOutbox_Attempts", "\"Attempts\" BETWEEN 0 AND 3"));
        });

        modelBuilder.Entity<SavingsGoal>(entity =>
        {
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_SavingsGoals_Amounts", "\"TargetAmount\" > 0 AND \"TargetAmount\" <= 1000000 AND \"MonthlyContribution\" >= 0 AND \"MonthlyContribution\" <= 1000000");
                table.HasCheckConstraint("CK_SavingsGoals_Currency", "\"Currency\" ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            entity.Property(e => e.TargetAmount).HasColumnType("numeric(18,2)");
            entity.Property(e => e.MonthlyContribution).HasColumnType("numeric(18,2)");
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.HasIndex(e => new { e.UserId, e.CreatedAt, e.Id });
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<GoalContribution>(entity =>
        {
            entity.ToTable(table => table.HasCheckConstraint("CK_GoalContributions_Amount", "\"Amount\" > 0 AND \"Amount\" <= 1000000"));
            entity.HasKey(e => new { e.GoalId, e.Id });
            entity.Property(e => e.Amount).HasColumnType("numeric(18,2)");
            entity.Property(e => e.Note).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => new { e.GoalId, e.ContributedOn, e.CreatedAt, e.Id });
            entity.HasOne(e => e.Goal).WithMany(e => e.Contributions).HasForeignKey(e => e.GoalId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubscriptionPriceChange>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Cost).HasColumnType("numeric(18,2)");
            entity.Property(e => e.PreviousCost).HasColumnType("numeric(18,2)");
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(10);
            entity.Property(e => e.PreviousCurrency).IsRequired().HasMaxLength(10);
            entity.Property(e => e.BillingPeriod).HasConversion<string>();
            entity.Property(e => e.PreviousBillingPeriod).HasConversion<string>();
            entity.HasIndex(e => new { e.SubscriptionId, e.ChangedAt, e.Id });
            entity.HasOne(e => e.Subscription).WithMany().HasForeignKey(e => e.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });

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
            entity.ToTable(table => table.HasCheckConstraint("CK_Subscriptions_TrialBillingDate", "\"TrialEndsOn\" IS NULL OR \"NextBillingDate\" >= \"TrialEndsOn\""));
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                  .IsRequired()
                  .HasMaxLength(100);

            entity.Property(e => e.Cost)
                  .IsConcurrencyToken()
                  .HasColumnType("numeric(18,2)");

            entity.Property(e => e.Currency)
                  .IsConcurrencyToken()
                  .IsRequired()
                  .HasMaxLength(10);

            entity.Property(e => e.Category)
                  .HasMaxLength(100);

            entity.Property(e => e.BillingPeriod)
                  .IsConcurrencyToken()
                  .HasConversion<string>();

            entity.Property(e => e.NextBillingDate)
                  .HasColumnType("date");
            entity.Property(e => e.UpdatedAt).IsConcurrencyToken();
            entity.Property(e => e.Notes).IsRequired().HasMaxLength(2000).HasDefaultValue("");
            entity.Property(e => e.ProviderUrl).IsRequired().HasMaxLength(2048).HasDefaultValue("");
            entity.Property(e => e.TrialEndsOn).HasColumnType("date");

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.ImportSourceId }).IsUnique();
        });

        modelBuilder.Entity<AccountDeletionRecord>(entity =>
        {
            entity.HasKey(e => e.UserId);
            entity.HasIndex(e => e.DeletedAt);
        });

        modelBuilder.Entity<User>(entity =>
        {
            // Old binaries cannot accept a password-only reset/login for MFA accounts.
            entity.ToTable(table => table.HasCheckConstraint("CK_Users_MfaPasswordMarker",
                "\"MfaEnabled\" = (left(\"PasswordHash\", 5) = '$mfa$')"));
            entity.ToTable(table => table.HasCheckConstraint("CK_Users_MfaVerifiedEmail","NOT \"MfaEnabled\" OR \"EmailVerified\""));
            entity.ToTable(table => table.HasCheckConstraint("CK_Users_MfaSetupAttempts","\"MfaSetupAttempts\" BETWEEN 0 AND 5"));
            entity.Property(e => e.DataExportRequestedAt).IsConcurrencyToken();
            entity.Property(e => e.MfaEnabled).IsConcurrencyToken().HasDefaultValue(false);
            entity.Property(e => e.MfaEpoch).IsConcurrencyToken().HasDefaultValue(Guid.Empty);
            entity.Property(e => e.MfaSecret).HasMaxLength(1024);
            entity.Property(e => e.MfaPendingSecret).HasMaxLength(1024);
            entity.Property(e => e.MfaSetupId).IsConcurrencyToken();
            entity.Property(e => e.MfaLastStep).IsConcurrencyToken();
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

