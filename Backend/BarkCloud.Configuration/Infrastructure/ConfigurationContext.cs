using BarkCloud.Configuration.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Configuration.Infrastructure;

public class ConfigurationContext : DbContext
{
    public ConfigurationContext(DbContextOptions<ConfigurationContext> options) : base(options) { }

    public DbSet<ConfigurationItem> Configurations { get; set; }
}