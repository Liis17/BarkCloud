using System.Linq.Expressions;

namespace BarkCloud.Files.Domain;

public static class UploadFileReadiness
{
    public static Expression<Func<UploadFile, bool>> ReadyExpression { get; } =
        file => file.UploadedAt != null && file.Etag != null && file.Etag != "";

    private static readonly Func<UploadFile, bool> Ready = ReadyExpression.Compile();

    public static bool IsReady(this UploadFile file) => Ready(file);

    public static IQueryable<UploadFile> WhereReady(this IQueryable<UploadFile> files) =>
        files.Where(ReadyExpression);
}
