using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BarkCloud.Configuration.Infrastructure;

public class ConfigurationContextFactory : IDesignTimeDbContextFactory<ConfigurationContext>
{
    public ConfigurationContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ConfigurationContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=configuration;Username=postgres;Password=postgres");
        return new ConfigurationContext(optionsBuilder.Options);
    }
}
