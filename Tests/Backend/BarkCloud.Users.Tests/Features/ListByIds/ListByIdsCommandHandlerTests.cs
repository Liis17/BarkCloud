using BarkCloud.Users.Domain;
using BarkCloud.Users.Features.ListByIds;
using BarkCloud.Users.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Users.Tests.Features.ListByIds;

public class ListByIdsCommandHandlerTests
{
    private readonly Mock<IUsersStorage> _users = new();

    private ListByIdsCommandHandler CreateSut() => new(
        _users.Object,
        NullLogger<ListByIdsCommandHandler>.Instance);

    private static User MakeUser(long id) => new()
    {
        Id = id,
        Username = $"u{id}",
        FirstName = "F",
        LastName = "L",
        RegistrationDate = DateTime.UtcNow
    };

    [Fact]
    public async Task Handle_ReturnsMappedUsers()
    {
        _users.Setup(s => s.GetByIds(It.Is<List<long>>(l => l.SequenceEqual(new[] { 1L, 2L }))))
            .ReturnsAsync(new List<User> { MakeUser(1), MakeUser(2) });

        var response = await CreateSut().Handle(new ListByIdsCommand { Ids = new List<long> { 1, 2 } }, default);

        response.Users.Should().HaveCount(2);
        response.Users.Select(u => u.Id).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Handle_NoMatches_ReturnsEmpty()
    {
        _users.Setup(s => s.GetByIds(It.IsAny<List<long>>())).ReturnsAsync(new List<User>());

        var response = await CreateSut().Handle(new ListByIdsCommand { Ids = new List<long> { 9 } }, default);

        response.Users.Should().BeEmpty();
    }
}
