using BarkCloud.Users.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BarkCloud.Users.Tests._Helpers;

internal static class UsersContextFactory
{
    // Юнит-тесты мокируют хранилище; реальные транзакции проверяются в IntegrationTests.
    public static UsersContext Create()
    {
        var context = new Mock<UsersContext>(new DbContextOptions<UsersContext>());
        var database = new Mock<DatabaseFacade>(context.Object);
        database.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDbContextTransaction>());
        context.SetupGet(c => c.Database).Returns(database.Object);
        return context.Object;
    }
}
