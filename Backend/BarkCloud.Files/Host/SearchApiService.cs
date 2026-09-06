using BarkCloud.Files.Services;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Files.Host;

[Authorize(Policy = nameof(TokenType.User))]
public class SearchApiService(UnifiedSearchService search) : SearchApi.SearchApiBase
{
    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
        => search.Search(request, context.CancellationToken);

    public override Task<SearchHit> ResolveHit(SearchHitReference request, ServerCallContext context)
        => search.ResolveHit(request, context.CancellationToken);

    public override Task<FileSearchMetadata> GetFileSearchMetadata(GetFileSearchMetadataRequest request, ServerCallContext context)
        => search.GetFileSearchMetadata(ParseFileId(request.FileId), context.CancellationToken);

    public override Task<FileSearchMetadata> ReplaceFileSearchMetadata(ReplaceFileSearchMetadataRequest request, ServerCallContext context)
        => search.ReplaceFileSearchMetadata(ParseFileId(request.FileId), request.Alias, request.Tags, context.CancellationToken);

    private static Guid ParseFileId(string value)
        => Guid.TryParse(value, out var fileId)
            ? fileId
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректный id файла"));
}
