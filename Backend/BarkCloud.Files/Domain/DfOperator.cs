namespace BarkCloud.Files.Domain;

/// <summary>
/// Оператор правила умной папки. Числовые значения совпадают с proto-enum <c>DfOperator</c>.
/// </summary>
public enum DfOperator
{
    None = 0,
    WithinLastDays = 1, // даты: CreatedAt/TakenAt >= now - N дней
    Before = 2,         // дата раньше значения
    After = 3,          // дата позже значения
    GreaterThan = 4,    // число больше
    LessThan = 5,       // число меньше
    Contains = 6,       // строка содержит
    Equals = 7,         // равно
    EndsWith = 8,       // строка заканчивается на
    StartsWith = 9      // строка начинается с
}
