using System.Globalization;
using System.Linq.Expressions;

using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

/// <summary>
/// Транслирует критерии умной папки (<see cref="DynamicFolderCriteria"/>) в <see cref="IQueryable{UploadFile}"/>
/// поверх базового фильтра «живых» файлов владельца (как <c>UploadedFilesStorage.ListUserMediaPage</c>,
/// но без ограничения по <see cref="MediaKind"/> — умные папки видят все типы, включая документы и аудио).
/// Содержимое нигде не материализуется: запрос исполняется на лету при подсчёте/листинге.
/// </summary>
public static class DynamicFolderQueryBuilder
{
    /// <summary>
    /// Запрос файлов владельца, удовлетворяющих критериям. Сортировка не применяется — её добавляет вызывающий код.
    /// Невалидные правила (непарсимое число/дата) пропускаются, чтобы один битый ряд не ронял весь запрос.
    /// </summary>
    public static IQueryable<UploadFile> BuildQuery(FilesContext ctx, long ownerId, DynamicFolderCriteria criteria, DateTime now)
    {
        // Базовый фильтр идентичен ListUserMediaPage: файлы владельца, реальные блобы (не превью),
        // не «эффективно удалённые» (есть запись в корзине и нет живой записи).
        var query = ctx.UploadedFiles
            .AsNoTracking()
            .Where(f => f.Uploaders.Contains(ownerId)
                        && f.Type == UploadFileType.CloudFile
                        && !ctx.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                        && !(ctx.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && e.IsDeleted)
                             && !ctx.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && !e.IsDeleted)));

        var predicates = new List<Expression<Func<UploadFile, bool>>>();
        foreach (var rule in criteria.Rules)
            if (TryBuildRule(ctx, rule, now, out var predicate))
                predicates.Add(predicate);

        if (predicates.Count == 0)
            return query;

        var combined = predicates[0];
        for (var i = 1; i < predicates.Count; i++)
            combined = criteria.Combinator == DfCombinator.Any
                ? combined.Or(predicates[i])
                : combined.And(predicates[i]);

        return query.Where(combined);
    }

    /// <summary>
    /// Проверка валидности правила без обращения к БД (для валидации при создании/обновлении папки).
    /// Числовые поля требуют парсимого числа, строковые — непустого значения, даты — корректного формата.
    /// </summary>
    public static bool IsRuleValid(DynamicFolderRule rule)
    {
        var value = (rule.Value ?? string.Empty).Trim();
        switch (rule.Field)
        {
            case DfField.Date:
            case DfField.TakenAt:
                if (rule.Operator == DfOperator.WithinLastDays)
                    return int.TryParse(value, out _);
                if (rule.Operator is DfOperator.Before or DfOperator.After)
                    return TryParseDate(value, out _);
                return false;
            case DfField.Size:
                return long.TryParse(value, out _);
            case DfField.ImageWidth:
            case DfField.ImageHeight:
                return int.TryParse(value, out _);
            case DfField.MediaKind:
                return ParseMediaKinds(value).Count > 0;
            case DfField.Name:
            case DfField.Extension:
            case DfField.Device:
                return value.Length > 0;
            default:
                return false;
        }
    }

    private static bool TryBuildRule(FilesContext ctx, DynamicFolderRule rule, DateTime now, out Expression<Func<UploadFile, bool>> predicate)
    {
        predicate = _ => true;
        var value = (rule.Value ?? string.Empty).Trim();

        switch (rule.Field)
        {
            case DfField.Date:
                return TryBuildDate(rule.Operator, value, now, out predicate);

            case DfField.TakenAt:
                // Дата съёмки лежит в FileMetadata (1:1). Файлы без метаданных условие отсекает.
                if (rule.Operator == DfOperator.WithinLastDays && int.TryParse(value, out var takenDays))
                {
                    var from = DateTime.SpecifyKind(now.AddDays(-Math.Abs(takenDays)), DateTimeKind.Utc);
                    predicate = f => ctx.FileMetadata.Any(m => m.FileId == f.Id && m.TakenAt != null && m.TakenAt >= from);
                    return true;
                }
                if ((rule.Operator == DfOperator.Before || rule.Operator == DfOperator.After) && TryParseDate(value, out var takenAt))
                {
                    predicate = rule.Operator == DfOperator.Before
                        ? f => ctx.FileMetadata.Any(m => m.FileId == f.Id && m.TakenAt != null && m.TakenAt < takenAt)
                        : f => ctx.FileMetadata.Any(m => m.FileId == f.Id && m.TakenAt != null && m.TakenAt >= takenAt);
                    return true;
                }
                return false;

            case DfField.Size:
                if (!long.TryParse(value, out var size))
                    return false;
                predicate = rule.Operator switch
                {
                    DfOperator.LessThan => f => f.Size < size,
                    DfOperator.Equals => f => f.Size == size,
                    _ => f => f.Size > size,
                };
                return true;

            case DfField.ImageWidth:
                if (!int.TryParse(value, out var width))
                    return false;
                predicate = rule.Operator switch
                {
                    DfOperator.LessThan => f => f.ImageWidth < width,
                    DfOperator.Equals => f => f.ImageWidth == width,
                    _ => f => f.ImageWidth > width,
                };
                return true;

            case DfField.ImageHeight:
                if (!int.TryParse(value, out var height))
                    return false;
                predicate = rule.Operator switch
                {
                    DfOperator.LessThan => f => f.ImageHeight < height,
                    DfOperator.Equals => f => f.ImageHeight == height,
                    _ => f => f.ImageHeight > height,
                };
                return true;

            case DfField.MediaKind:
                // Значение — один код или набор через запятую («1,2» = фото или видео).
                var kinds = ParseMediaKinds(value);
                if (kinds.Count == 0)
                    return false;
                predicate = f => kinds.Contains(f.MediaKind);
                return true;

            case DfField.Name:
                if (value.Length == 0)
                    return false;
                var needle = value.ToLower();
                predicate = rule.Operator switch
                {
                    DfOperator.StartsWith => f => f.Filename != null && f.Filename.ToLower().StartsWith(needle),
                    DfOperator.EndsWith => f => f.Filename != null && f.Filename.ToLower().EndsWith(needle),
                    DfOperator.Equals => f => f.Filename != null && f.Filename.ToLower() == needle,
                    _ => f => f.Filename != null && f.Filename.ToLower().Contains(needle),
                };
                return true;

            case DfField.Extension:
                if (value.Length == 0)
                    return false;
                var ext = "." + value.TrimStart('.').ToLower();
                predicate = f => f.Filename != null && f.Filename.ToLower().EndsWith(ext);
                return true;

            case DfField.Device:
                if (value.Length == 0)
                    return false;
                var device = value.ToLower();
                predicate = rule.Operator == DfOperator.Equals
                    ? f => f.UploadDeviceName != null && f.UploadDeviceName.ToLower() == device
                    : f => f.UploadDeviceName != null && f.UploadDeviceName.ToLower().Contains(device);
                return true;

            default:
                return false;
        }
    }

    private static bool TryBuildDate(DfOperator op, string value, DateTime now, out Expression<Func<UploadFile, bool>> predicate)
    {
        predicate = f => true;
        switch (op)
        {
            case DfOperator.WithinLastDays:
                if (!int.TryParse(value, out var days))
                    return false;
                var from = DateTime.SpecifyKind(now.AddDays(-Math.Abs(days)), DateTimeKind.Utc);
                predicate = f => f.CreatedAt >= from;
                return true;
            case DfOperator.Before:
                if (!TryParseDate(value, out var before))
                    return false;
                predicate = f => f.CreatedAt < before;
                return true;
            case DfOperator.After:
                if (!TryParseDate(value, out var after))
                    return false;
                predicate = f => f.CreatedAt >= after;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Разбирает значение правила <see cref="DfField.MediaKind"/>: один код («3») или набор через запятую («1,2»).
    /// Невалидные/выходящие за диапазон коды отбрасываются. Дубликаты схлопываются.
    /// </summary>
    private static List<MediaKind> ParseMediaKinds(string value)
    {
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var k) && k is >= 0 and <= 4 ? (MediaKind?)(MediaKind)k : null)
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .Distinct()
            .ToList();
    }

    private static bool TryParseDate(string value, out DateTime utc)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }
        utc = default;
        return false;
    }
}

/// <summary>
/// Объединяет предикаты с заменой параметра — для сборки И/ИЛИ из инлайн-выражений
/// (каждое со своим параметром). Аналог LinqKit.PredicateBuilder, но без зависимости.
/// </summary>
internal static class PredicateBuilder
{
    public static Expression<Func<T, bool>> And<T>(this Expression<Func<T, bool>> a, Expression<Func<T, bool>> b)
        => Combine(a, b, Expression.AndAlso);

    public static Expression<Func<T, bool>> Or<T>(this Expression<Func<T, bool>> a, Expression<Func<T, bool>> b)
        => Combine(a, b, Expression.OrElse);

    private static Expression<Func<T, bool>> Combine<T>(
        Expression<Func<T, bool>> a, Expression<Func<T, bool>> b,
        Func<Expression, Expression, BinaryExpression> merge)
    {
        var param = Expression.Parameter(typeof(T), "f");
        var left = new ReplaceVisitor(a.Parameters[0], param).Visit(a.Body)!;
        var right = new ReplaceVisitor(b.Parameters[0], param).Visit(b.Body)!;
        return Expression.Lambda<Func<T, bool>>(merge(left, right), param);
    }

    private sealed class ReplaceVisitor : ExpressionVisitor
    {
        private readonly Expression _from;
        private readonly Expression _to;

        public ReplaceVisitor(Expression from, Expression to)
        {
            _from = from;
            _to = to;
        }

        public override Expression? Visit(Expression? node) => node == _from ? _to : base.Visit(node);
    }
}
