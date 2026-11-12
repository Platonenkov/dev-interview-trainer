using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class RedirectConfiguration : IEntityTypeConfiguration<RedirectEntity>
{
    public void Configure(EntityTypeBuilder<RedirectEntity> builder)
    {
        builder.ToTable("redirects");
        builder.HasKey(r => new { r.StackKey, r.FromSlug });
        builder.HasOne<StackEntity>().WithMany().HasForeignKey(r => r.StackKey).OnDelete(DeleteBehavior.Cascade);
    }
}
