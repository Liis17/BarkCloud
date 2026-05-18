using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BarkCloud.Users.Persistence.Contexts;

public class UsersContextFactory : IDesignTimeDbContextFactory<UsersContext>
{
    public UsersContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsersContext>();

        // Используем тестовую строку подключения для миграций
        optionsBuilder.UseNpgsql("Host=localhost;Database=barkcloud_users;Username=postgres;Password=password");

        return new UsersContext(optionsBuilder.Options);
    }
}