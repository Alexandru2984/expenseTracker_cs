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
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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

            entity.Property(e => e.Email)
                  .HasMaxLength(256);

            // Unique per-email; PostgreSQL treats NULLs as distinct, so legacy
            // accounts without an email don't collide.
            entity.HasIndex(e => e.Email)
                  .IsUnique();

            entity.Property(e => e.PasswordHash)
                  .IsRequired();
        });

        modelBuilder.Entity<VerificationCode>(entity =>
        {
            entity.HasKey(e => e.Id);

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

