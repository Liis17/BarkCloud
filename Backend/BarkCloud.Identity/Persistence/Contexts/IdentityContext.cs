using BarkCloud.Identity.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Persistence.Contexts;

public class IdentityContext : DbContext
{
    public IdentityContext(DbContextOptions<IdentityContext> options) : base(options) { }

    public DbSet<RefreshToken> RefreshTokens { get; set; }

    public DbSet<RevokedSession> RevokedSessions { get; set; }

    public DbSet<ConfirmationCode> ConfirmationCodes { get; set; }

    public DbSet<AuthUserProperty> AuthUserProperties { get; set; }

    public DbSet<ResetPassword> ResetPasswords { get; set; }

    public DbSet<UserPassword> UserPasswords { get; set; }

    public DbSet<WebAuthnCredential> WebAuthnCredentials { get; set; }

    public DbSet<WebAuthnChallenge> WebAuthnChallenges { get; set; }

    public DbSet<AuthAttemptCounter> AuthAttemptCounters { get; set; }

    public DbSet<PendingNotification> PendingNotifications { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RefreshToken>()
            .HasIndex(x => x.Value)
            .IsUnique();

        modelBuilder.Entity<RevokedSession>()
            .HasIndex(x => x.ExpiresAt);

        modelBuilder.Entity<RevokedSession>()
            .HasIndex(x => x.RevokedAt);

        modelBuilder.Entity<WebAuthnCredential>()
            .HasIndex(x => x.CredentialId)
            .IsUnique();

        modelBuilder.Entity<WebAuthnCredential>()
            .HasIndex(x => x.UserId);

        modelBuilder.Entity<AuthAttemptCounter>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(200);
        });

        modelBuilder.Entity<PendingNotification>()
            .HasIndex(x => x.NextAttemptAt);
    }
}
