namespace BarkCloud.Files.Domain;

/// <summary>
/// Способ объединения правил умной папки. Совпадает с proto-enum <c>DfCombinator</c>.
/// </summary>
public enum DfCombinator
{
    All = 0, // все правила (И)
    Any = 1  // любое правило (ИЛИ)
}
