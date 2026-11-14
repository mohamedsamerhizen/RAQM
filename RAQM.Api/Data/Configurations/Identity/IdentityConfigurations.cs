using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Entities.Identity;
namespace RAQM.Api.Data.Configurations.Identity;

public sealed class SessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        b.ToTable("Sessions", "identity"); b.HasKey(x => x.Id);
        b.HasIndex(x => x.UserId);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens", "identity"); b.HasKey(x => x.Id);
        b.Property(x => x.Hash).HasMaxLength(64); b.HasIndex(x => x.Hash).IsUnique();
        b.HasOne<UserSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
    }
}


