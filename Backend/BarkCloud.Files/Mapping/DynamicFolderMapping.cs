using BarkCloud.Files.Domain;

using Google.Protobuf.WellKnownTypes;

using PInfo = BarkCloud.Proto.Files.DynamicFolderInfo;
using PRule = BarkCloud.Proto.Files.DfRule;
using PField = BarkCloud.Proto.Files.DfField;
using POperator = BarkCloud.Proto.Files.DfOperator;
using PCombinator = BarkCloud.Proto.Files.DfCombinator;

namespace BarkCloud.Files.Mapping;

public static class DynamicFolderMapping
{
    /// <summary>
    /// Мапит умную папку в gRPC-DTO. itemsCount и coverUrl вычисляются вызывающим кодом.
    /// Для системных папок id = SystemKey, для пользовательских — Guid.
    /// </summary>
    public static PInfo ToGrpc(this DynamicFolder folder, int itemsCount, string? coverUrl = null)
    {
        var info = new PInfo
        {
            Id = folder.IsSystem ? folder.SystemKey ?? string.Empty : folder.Id.ToString(),
            Name = folder.Name,
            IsSystem = folder.IsSystem,
            Combinator = (PCombinator)(int)folder.Criteria.Combinator,
            IconKey = folder.IconKey ?? string.Empty,
            CoverColor = folder.CoverColor ?? string.Empty,
            CoverPreviewUrl = coverUrl ?? string.Empty,
            ItemsCount = itemsCount,
            SortOrder = folder.SortOrder,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(folder.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(folder.UpdatedAt, DateTimeKind.Utc))
        };

        foreach (var rule in folder.Criteria.Rules)
        {
            info.Rules.Add(new PRule
            {
                Field = (PField)(int)rule.Field,
                Operator = (POperator)(int)rule.Operator,
                Value = rule.Value ?? string.Empty
            });
        }

        return info;
    }

    /// <summary>
    /// Собирает доменные критерии из proto-комбинатора и набора правил (используется Host-слоем
    /// при маппинге Create/Update запросов).
    /// </summary>
    public static DynamicFolderCriteria ToDomainCriteria(PCombinator combinator, IEnumerable<PRule> rules)
    {
        return new DynamicFolderCriteria
        {
            Combinator = (DfCombinator)(int)combinator,
            Rules = rules.Select(r => new DynamicFolderRule
            {
                Field = (DfField)(int)r.Field,
                Operator = (DfOperator)(int)r.Operator,
                Value = r.Value ?? string.Empty
            }).ToList()
        };
    }
}
