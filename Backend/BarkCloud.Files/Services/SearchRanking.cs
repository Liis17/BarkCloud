using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

using Grpc.Core;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Services;

/// <summary>
/// Нормализованный запрос и SQL-выражения ранжирования одного поля. Правила совпадают с
/// правилами поиска: 4 — полное совпадение, 3 — префикс, 2 — подстрока, 1 — опечатка
/// (запрос от 4 символов и word_similarity ≥ 0.45). Сходство равно 1 для рангов 2–4, иначе word_similarity.
/// </summary>
internal sealed class SearchTerms
{
    private const double TrigramThreshold = .45d;
    private const int TrigramMinLength = 4;

    public SearchTerms(string query)
    {
        Query = query;
        if (query.Length == 0)
            return;

        var pattern = "%" + Escape(query) + "%";
        var prefix = Escape(query) + "%";
        var trigram = query.Length >= TrigramMinLength;

        Match = trigram
            ? v => EF.Functions.ILike(v!, pattern, "\\") || EF.Functions.TrigramsWordSimilarity(v!, query) >= TrigramThreshold
            : v => EF.Functions.ILike(v!, pattern, "\\");
        Rank = trigram
            ? v => v == null ? 0
                : v.ToLower() == query ? 4
                : EF.Functions.ILike(v, prefix, "\\") ? 3
                : EF.Functions.ILike(v, pattern, "\\") ? 2
                : EF.Functions.TrigramsWordSimilarity(v, query) >= TrigramThreshold ? 1 : 0
            : v => v == null ? 0
                : v.ToLower() == query ? 4
                : EF.Functions.ILike(v, prefix, "\\") ? 3
                : EF.Functions.ILike(v, pattern, "\\") ? 2 : 0;
        Similarity = trigram
            ? v => v == null ? 0d : EF.Functions.ILike(v, pattern, "\\") ? 1d : EF.Functions.TrigramsWordSimilarity(v, query)
            : v => v == null ? 0d : EF.Functions.ILike(v, pattern, "\\") ? 1d : 0d;
    }

    /// <summary>Нормализованный запрос; пустой — разрешение хита по id, без ранжирования.</summary>
    public string Query { get; }

    public bool HasQuery => Query.Length > 0;

    /// <summary>Поле подходит под запрос (ILIKE или trigram) — условие для GIN-индексов.</summary>
    public Expression<Func<string?, bool>> Match { get; } = _ => false;

    public Expression<Func<string?, int>> Rank { get; } = _ => 0;

    public Expression<Func<string?, double>> Similarity { get; } = _ => 0d;

    private static string Escape(string query)
        => query.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}

/// <summary>Элемент выборки с рангом и сходством, посчитанными в БД.</summary>
internal sealed class Ranked<T>
{
    public T Item { get; set; } = default!;

    public int Rank { get; set; }

    public double Sim { get; set; }
}

/// <summary>Одно совпавшее поле источника, включая SQL-оценку и отображаемое значение.</summary>
internal sealed class SearchCandidate<TKey>
{
    public TKey Key { get; set; } = default!;

    public int Rank { get; set; }

    public double Sim { get; set; }

    public int Order { get; set; }

    public string Field { get; set; } = string.Empty;

    public string? Value { get; set; }
}

/// <summary>Строка секции в порядке выдачи: ранг↓, сходство↓, время↓, id↓.</summary>
internal sealed class SearchRow<T>
{
    public T Item { get; set; } = default!;

    public int Rank { get; set; }

    public double Sim { get; set; }

    public DateTime SortAt { get; set; }

    public Guid Id { get; set; }

    public string MatchField { get; set; } = string.Empty;

    public string MatchValue { get; set; } = string.Empty;
}

internal static class SearchQueryExtensions
{
    private static readonly System.Reflection.MethodInfo MaxInt = typeof(Math).GetMethod(nameof(Math.Max), [typeof(int), typeof(int)])!;
    private static readonly System.Reflection.MethodInfo MaxDouble = typeof(Math).GetMethod(nameof(Math.Max), [typeof(double), typeof(double)])!;
    private static readonly MethodInfo QueryableWhere = QueryableLambdaMethod(nameof(Queryable.Where), 1, 2);
    private static readonly MethodInfo QueryableOrderBy = QueryableLambdaMethod(nameof(Queryable.OrderBy), 2, 2);
    private static readonly MethodInfo QueryableThenBy = QueryableLambdaMethod(nameof(Queryable.ThenBy), 2, 2);
    private static readonly MethodInfo QueryableSelect = QueryableLambdaMethod(nameof(Queryable.Select), 2, 2);
    private static readonly MethodInfo QueryableFirstOrDefault = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.FirstOrDefault) && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1);
    private static readonly MethodInfo CollateString = ((MethodCallExpression)((Expression<Func<string, string>>)
        (value => EF.Functions.Collate(value, "C"))).Body).Method;

    /// <summary>Оставляет строки, у которых хотя бы одно поле подходит под запрос (условие, пригодное для trigram-индекса).</summary>
    public static IQueryable<T> WhereMatchesAny<T>(this IQueryable<T> source, SearchTerms terms, params Expression<Func<T, string?>>[] fields)
    {
        if (!terms.HasQuery)
            return source;

        var x = Expression.Parameter(typeof(T), "x");
        var any = fields.Select(f => Apply(terms.Match, f, x)).Aggregate(Expression.OrElse);
        return source.Where(Expression.Lambda<Func<T, bool>>(any, x));
    }

    /// <summary>
    /// Выбирает <paramref name="item"/> вместе с рангом и сходством лучшего из <paramref name="fields"/>.
    /// Без запроса ранг и сходство нулевые, фильтрации нет. <paramref name="prefilter"/> = false, если
    /// условие по индексированным колонкам уже наложено отдельно (<see cref="WhereMatchesAny{T}"/>).
    /// </summary>
    public static IQueryable<Ranked<TItem>> RankedBy<T, TItem>(
        this IQueryable<T> source, SearchTerms terms, Expression<Func<T, TItem>> item, bool prefilter,
        params Expression<Func<T, string?>>[] fields)
    {
        var x = Expression.Parameter(typeof(T), "x");
        Expression rank = Expression.Constant(0);
        Expression similarity = Expression.Constant(0d);
        if (terms.HasQuery)
        {
            if (prefilter)
                source = source.WhereMatchesAny(terms, fields);
            rank = fields.Select(f => Apply(terms.Rank, f, x)).Aggregate((a, b) => Expression.Call(MaxInt, a, b));
            similarity = fields.Select(f => Apply(terms.Similarity, f, x)).Aggregate((a, b) => Expression.Call(MaxDouble, a, b));
        }

        var init = Expression.MemberInit(
            Expression.New(typeof(Ranked<TItem>)),
            Expression.Bind(typeof(Ranked<TItem>).GetProperty(nameof(Ranked<TItem>.Item))!, Replace(item.Body, item.Parameters[0], x)),
            Expression.Bind(typeof(Ranked<TItem>).GetProperty(nameof(Ranked<TItem>.Rank))!, rank),
            Expression.Bind(typeof(Ranked<TItem>).GetProperty(nameof(Ranked<TItem>.Sim))!, similarity));
        return source.Select(Expression.Lambda<Func<T, Ranked<TItem>>>(init, x));
    }

    /// <summary>Создаёт строку-кандидат для одного поля, используя одно правило SQL для отбора и ранга.</summary>
    public static IQueryable<SearchCandidate<TKey>> CandidatesBy<T, TKey>(
        this IQueryable<T> source, SearchTerms terms, Expression<Func<T, TKey>> key, string field, int order,
        Expression<Func<T, string?>> ranked, Expression<Func<T, string?>>? display = null)
    {
        if (!terms.HasQuery)
            throw new InvalidOperationException("Кандидатов поиска нельзя строить без запроса.");

        source = source.WhereMatchesAny(terms, ranked);
        display ??= ranked;

        var x = Expression.Parameter(typeof(T), "x");
        var init = Expression.MemberInit(
            Expression.New(typeof(SearchCandidate<TKey>)),
            Expression.Bind(typeof(SearchCandidate<TKey>).GetProperty(nameof(SearchCandidate<TKey>.Key))!, Apply(key, x)),
            Expression.Bind(typeof(SearchCandidate<TKey>).GetProperty(nameof(SearchCandidate<TKey>.Rank))!, Apply(terms.Rank, ranked, x)),
            Expression.Bind(typeof(SearchCandidate<TKey>).GetProperty(nameof(SearchCandidate<TKey>.Sim))!, Apply(terms.Similarity, ranked, x)),
            Expression.Bind(typeof(SearchCandidate<TKey>).GetProperty(nameof(SearchCandidate<TKey>.Order))!, Expression.Constant(order)),
            Expression.Bind(typeof(SearchCandidate<TKey>).GetProperty(nameof(SearchCandidate<TKey>.Field))!, Expression.Constant(field)),
            Expression.Bind(typeof(SearchCandidate<TKey>).GetProperty(nameof(SearchCandidate<TKey>.Value))!, Apply(display, x)));
        return source.Select(Expression.Lambda<Func<T, SearchCandidate<TKey>>>(init, x));
    }

    /// <summary>Сворачивает кандидатов по ключу, сохраняя текущую семантику максимума ранга и сходства.</summary>
    public static IQueryable<Ranked<TKey>> BestMatches<TKey>(this IQueryable<SearchCandidate<TKey>> candidates)
        => candidates.GroupBy(candidate => candidate.Key)
            .Select(group => new Ranked<TKey>
            {
                Item = group.Key,
                Rank = group.Max(candidate => candidate.Rank),
                Sim = group.Max(candidate => candidate.Sim)
            });

    /// <summary>Выбирает подпись страницы из тех же кандидатов после курсора, порядка и LIMIT.</summary>
    public static IQueryable<SearchRow<T>> WithMatchCandidates<T, TKey>(
        this IQueryable<SearchRow<T>> rows, IQueryable<SearchCandidate<TKey>> candidates, Expression<Func<SearchRow<T>, TKey>> key)
    {
        var row = Expression.Parameter(typeof(SearchRow<T>), "row");
        var keyBody = Replace(key.Body, key.Parameters[0], row);
        var init = Expression.MemberInit(
            Expression.New(typeof(SearchRow<T>)),
            Expression.Bind(typeof(SearchRow<T>).GetProperty(nameof(SearchRow<T>.Item))!, Expression.Property(row, nameof(SearchRow<T>.Item))),
            Expression.Bind(typeof(SearchRow<T>).GetProperty(nameof(SearchRow<T>.Rank))!, Expression.Property(row, nameof(SearchRow<T>.Rank))),
            Expression.Bind(typeof(SearchRow<T>).GetProperty(nameof(SearchRow<T>.Sim))!, Expression.Property(row, nameof(SearchRow<T>.Sim))),
            Expression.Bind(typeof(SearchRow<T>).GetProperty(nameof(SearchRow<T>.SortAt))!, Expression.Property(row, nameof(SearchRow<T>.SortAt))),
            Expression.Bind(typeof(SearchRow<T>).GetProperty(nameof(SearchRow<T>.Id))!, Expression.Property(row, nameof(SearchRow<T>.Id))),
            Expression.Bind(typeof(SearchRow<T>).GetProperty(nameof(SearchRow<T>.MatchField))!, FirstMatchValue<TKey, T>(candidates, row, keyBody, nameof(SearchCandidate<TKey>.Field))),
            Expression.Bind(typeof(SearchRow<T>).GetProperty(nameof(SearchRow<T>.MatchValue))!, FirstMatchValue<TKey, T>(candidates, row, keyBody, nameof(SearchCandidate<TKey>.Value))));
        return rows.Select(Expression.Lambda<Func<SearchRow<T>, SearchRow<T>>>(init, row));
    }

    /// <summary>Строки после курсора (в порядке ранг↓, сходство↓, время↓, id↓).</summary>
    public static IQueryable<SearchRow<T>> After<T>(this IQueryable<SearchRow<T>> rows, SearchCursor? cursor)
    {
        if (cursor is null)
            return rows;

        var rank = cursor.Rank;
        var similarity = cursor.Similarity;
        var sortAt = cursor.SortAt;
        // id системной папки (не Guid) при Ordinal-сравнении больше любого Guid: все строки с теми же ранг/сходство/время идут после курсора.
        if (cursor.IdGuid is not { } id)
            return rows.Where(r => r.Rank < rank
                                   || r.Rank == rank && (r.Sim < similarity
                                                         || r.Sim == similarity && r.SortAt <= sortAt));

        return rows.Where(r => r.Rank < rank
                               || r.Rank == rank && (r.Sim < similarity
                                                     || r.Sim == similarity && (r.SortAt < sortAt
                                                                                || r.SortAt == sortAt && r.Id.CompareTo(id) < 0)));
    }

    public static IQueryable<SearchRow<T>> InSearchOrder<T>(this IQueryable<SearchRow<T>> rows)
        => rows.OrderByDescending(r => r.Rank)
            .ThenByDescending(r => r.Sim)
            .ThenByDescending(r => r.SortAt)
            .ThenByDescending(r => r.Id);

    private static Expression Apply<TResult, T>(Expression<Func<string?, TResult>> leaf, Expression<Func<T, string?>> field, ParameterExpression x)
        => Replace(leaf.Body, leaf.Parameters[0], Replace(field.Body, field.Parameters[0], x));

    private static Expression Apply<T, TValue>(Expression<Func<T, TValue>> selector, ParameterExpression x)
        => Replace(selector.Body, selector.Parameters[0], x);

    private static Expression FirstMatchValue<TKey, T>(IQueryable<SearchCandidate<TKey>> candidates, ParameterExpression row,
        Expression key, string propertyName)
    {
        var candidate = Expression.Parameter(typeof(SearchCandidate<TKey>), "candidate");
        var predicate = Expression.AndAlso(
            Expression.AndAlso(
                Expression.Equal(Expression.Property(candidate, nameof(SearchCandidate<TKey>.Key)), key),
                Expression.Equal(Expression.Property(candidate, nameof(SearchCandidate<TKey>.Rank)), Expression.Property(row, nameof(SearchRow<T>.Rank)))),
            Expression.Equal(Expression.Property(candidate, nameof(SearchCandidate<TKey>.Sim)), Expression.Property(row, nameof(SearchRow<T>.Sim))));
        var filtered = Expression.Call(QueryableWhere.MakeGenericMethod(typeof(SearchCandidate<TKey>)), candidates.Expression,
            Expression.Lambda(predicate, candidate));
        var order = Expression.Lambda(Expression.Property(candidate, nameof(SearchCandidate<TKey>.Order)), candidate);
        var ordered = Expression.Call(QueryableOrderBy.MakeGenericMethod(typeof(SearchCandidate<TKey>), typeof(int)), filtered, order);
        var display = Expression.Property(candidate, nameof(SearchCandidate<TKey>.Value));
        var collated = Expression.Call(CollateString, Expression.Constant(EF.Functions), display, Expression.Constant("C"));
        var tieBreak = Expression.Lambda(collated, candidate);
        var orderedByValue = Expression.Call(QueryableThenBy.MakeGenericMethod(typeof(SearchCandidate<TKey>), typeof(string)), ordered, tieBreak);
        var value = Expression.Lambda(Expression.Property(candidate, propertyName), candidate);
        var selected = Expression.Call(QueryableSelect.MakeGenericMethod(typeof(SearchCandidate<TKey>), typeof(string)), orderedByValue, value);
        var first = Expression.Call(QueryableFirstOrDefault.MakeGenericMethod(typeof(string)), selected);
        return Expression.Coalesce(first, Expression.Constant(string.Empty));
    }

    private static MethodInfo QueryableLambdaMethod(string name, int genericArgumentCount, int parameterCount)
        => typeof(Queryable).GetMethods()
            .Single(method => method.Name == name
                              && method.IsGenericMethodDefinition
                              && method.GetGenericArguments().Length == genericArgumentCount
                              && method.GetParameters().Length == parameterCount
                              && method.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static Expression Replace(Expression body, ParameterExpression from, Expression to)
        => new ParameterReplacer(from, to).Visit(body);

    private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>
/// Непрозрачный курсор страницы: позиция последнего хита в порядке (ранг, сходство, время, id).
/// Формат — base64url от <c>rank|similarity|ticks|kind|id</c>.
/// </summary>
internal sealed record SearchCursor(int Rank, double Similarity, long Ticks, string Kind, string Id)
{
    public DateTime SortAt => new(Ticks, DateTimeKind.Utc);

    public Guid? IdGuid => Guid.TryParse(Id, out var id) ? id : null;

    public static SearchCursor? Parse(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight((cursor.Length + 3) / 4 * 4, '=')));
            var parts = raw.Split('|');
            if (parts.Length != 5
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var similarity)
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                throw new FormatException();
            return new SearchCursor(rank, similarity, ticks, parts[3], parts[4]);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректный курсор поиска"));
        }
    }

    public static string Encode(int rank, double similarity, DateTime sortAt, string kind, string id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('|',
                rank.ToString(CultureInfo.InvariantCulture),
                similarity.ToString("R", CultureInfo.InvariantCulture),
                sortAt.Ticks.ToString(CultureInfo.InvariantCulture),
                kind,
                id)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
