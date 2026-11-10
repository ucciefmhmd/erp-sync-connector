namespace ErpSync.Domain.Common;

internal static class Guard
{
    public static string NotEmpty(string? value, string name, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static decimal NotNegative(decimal value, string name) =>
        value < 0 ? throw new DomainException($"{name} cannot be negative.") : value;
}
