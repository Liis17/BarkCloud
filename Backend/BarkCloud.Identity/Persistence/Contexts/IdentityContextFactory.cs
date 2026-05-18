using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BarkCloud.Identity.Persistence.Contexts;

/// <summary>
/// Design-time factory нужна для запуска `dotnet ef migrations add`,
/// когда сервис конфигурации недоступен.
/// </summary>
public class IdentityContextFactory : IDesignTimeDbContextFactory<IdentityContext>
{
    public IdentityContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<IdentityContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=barkcloud_identity;Username=postgres;Password=postgres");
        return new IdentityContext(optionsBuilder.Options);
    }
}
