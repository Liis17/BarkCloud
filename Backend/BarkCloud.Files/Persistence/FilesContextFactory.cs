using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Используется только инструментами EF Core для создания миграций без запуска приложения.
/// </summary>
public class FilesContextFactory : IDesignTimeDbContextFactory<FilesContext>
{
    public FilesContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FilesContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=barkcloud_files;Username=postgres;Password=postgres");
        return new FilesContext(optionsBuilder.Options);
    }
}
