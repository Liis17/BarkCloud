using BarkCloud.Files.Domain;
using BarkCloud.Proto.Files;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BarkCloud.Files.Services;

public partial class UnifiedSearchService
{
    public async Task<UserTagList> ListUserTags(CancellationToken cancellationToken)
    {
        var tags = await _context.UserTags.AsNoTracking().Where(x => x.OwnerId == _userContext.UserId)
            .OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var result = new UserTagList();
        result.Items.AddRange(tags.Select(ToTagData));
        return result;
    }

    public async Task<UserTagData> SaveUserTag(SaveUserTagRequest request, CancellationToken cancellationToken)
    {
        var name = SearchText.CollapseWhitespace(request.Name);
        var normalized = SearchText.Normalize(name);
        if (name.Length is 0 or > MaxTagLength || normalized.Length > MaxTagLength)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Название тега — от 1 до 50 символов"));
        if (request.Color.Length != 7 || request.Color[0] != '#' || !request.Color.Skip(1).All(Uri.IsHexDigit))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Цвет должен быть в формате #RRGGBB"));

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await LockUserTags(cancellationToken);
        UserTag tag;
        if (string.IsNullOrEmpty(request.Id))
        {
            tag = new UserTag { Id = Guid.NewGuid(), OwnerId = _userContext.UserId };
            _context.UserTags.Add(tag);
        }
        else
        {
            if (!Guid.TryParse(request.Id, out var id))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректный id тега"));
            tag = await _context.UserTags.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == _userContext.UserId, cancellationToken)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "Тег не найден"));
        }
        if (await _context.UserTags.AnyAsync(x => x.OwnerId == tag.OwnerId && x.NormalizedName == normalized && x.Id != tag.Id, cancellationToken))
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Тег с таким названием уже существует"));

        if (!string.IsNullOrEmpty(request.Id))
            await _context.FileTags.Where(x => x.OwnerId == tag.OwnerId && x.NormalizedValue == tag.NormalizedName)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, name).SetProperty(x => x.NormalizedValue, normalized), cancellationToken);
        tag.Name = name;
        tag.NormalizedName = normalized;
        tag.Color = request.Color.ToUpperInvariant();
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Тег с таким названием уже существует"));
        }
        await transaction.CommitAsync(cancellationToken);
        return ToTagData(tag);
    }

    public async Task<Empty> DeleteUserTag(string rawId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(rawId, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректный id тега"));
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await LockUserTags(cancellationToken);
        var tag = await _context.UserTags.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == _userContext.UserId, cancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Тег не найден"));
        await _context.FileTags.Where(x => x.OwnerId == tag.OwnerId && x.NormalizedValue == tag.NormalizedName).ExecuteDeleteAsync(cancellationToken);
        _context.UserTags.Remove(tag);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Empty();
    }

    private static UserTagData ToTagData(UserTag tag) => new() { Id = tag.Id.ToString(), Name = tag.Name, Color = tag.Color };

    // Каталог и строковые назначения изменяются согласованно, в том числе между репликами Files.
    private async Task LockUserTags(CancellationToken cancellationToken)
    {
        if (_context.Database.IsNpgsql())
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended('barkcloud.user-tags:' || {_userContext.UserId}::text, 0))", cancellationToken);
    }
}
