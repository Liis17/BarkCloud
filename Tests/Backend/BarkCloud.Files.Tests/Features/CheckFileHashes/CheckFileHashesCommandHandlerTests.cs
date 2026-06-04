using BarkCloud.Files.Features.CheckFileHashes;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.CheckFileHashes;

public class CheckFileHashesCommandHandlerTests
{
    private readonly Mock<IFileHashesStorage> _hashes = new();

    private CheckFileHashesCommandHandler CreateSut(long userId = 42) => new(
        _hashes.Object,
        UserContextFactory.Create(userId),
        NullLogger<CheckFileHashesCommandHandler>.Instance);

    [Fact]
    public async Task Handle_EmptyInput_ReturnsEmptyResults()
    {
        _hashes.Setup(s => s.GetExistingHashesForOwner(It.IsAny<long>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        var response = await CreateSut().Handle(new CheckFileHashesCommand { FileHashes = [] }, default);

        response.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_FiltersInvalidHashes()
    {
        var valid = new string('a', 64);
        _hashes.Setup(s => s.GetExistingHashesForOwner(It.IsAny<long>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        var response = await CreateSut().Handle(
            new CheckFileHashesCommand { FileHashes = ["", "not-hex", valid, "short"] }, default);

        response.Results.Should().HaveCount(1);
        response.Results[0].FileHash.Should().Be(valid);
    }

    [Fact]
    public async Task Handle_DeduplicatesValidHashes()
    {
        var hash = new string('b', 64);
        _hashes.Setup(s => s.GetExistingHashesForOwner(It.IsAny<long>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        var response = await CreateSut().Handle(
            new CheckFileHashesCommand { FileHashes = [hash, hash.ToUpperInvariant(), hash] }, default);

        response.Results.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_ReportsExistenceForEachHash()
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        _hashes.Setup(s => s.GetExistingHashesForOwner(It.IsAny<long>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { hashA });

        var response = await CreateSut().Handle(
            new CheckFileHashesCommand { FileHashes = [hashA, hashB] }, default);

        response.Results.Should().HaveCount(2);
        response.Results.Single(r => r.FileHash == hashA).Exists.Should().BeTrue();
        response.Results.Single(r => r.FileHash == hashB).Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_CapsBatchAt500()
    {
        var input = Enumerable.Range(0, 600)
            .Select(i => i.ToString("x").PadLeft(64, '0'))
            .ToList();
        _hashes.Setup(s => s.GetExistingHashesForOwner(It.IsAny<long>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        var response = await CreateSut().Handle(new CheckFileHashesCommand { FileHashes = input }, default);

        response.Results.Should().HaveCount(500);
    }
}
