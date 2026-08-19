namespace AppSupervisor;

/// <summary>Builds concise, specific text that is safe for the Windows notification-area tooltip.</summary>
internal static class TrayTooltipText
{
    internal const int MaximumLength = 127;
    private const string Prefix = "AppSupervisor - ";

    /// <summary>Combines an error category and its concrete detail into one normalized summary.</summary>
    public static string CreateErrorSummary(string category, string detail)
    {
        string normalizedCategory = Normalize(category);
        string normalizedDetail = Normalize(detail);

        if (normalizedCategory.Length == 0)
            return normalizedDetail.Length == 0 ? "Unknown error" : normalizedDetail;

        return normalizedDetail.Length == 0
            ? normalizedCategory
            : $"{normalizedCategory}: {normalizedDetail}";
    }

    /// <summary>Formats one active error, any additional-error count, and pending lifecycle activity.</summary>
    public static string FormatError(
        string summary,
        int additionalErrorCount,
        string? activity = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalErrorCount);

        string suffix = additionalErrorCount > 0
            ? $" (+{additionalErrorCount} more)"
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(activity))
            suffix += $"; {Normalize(activity)}";

        int summaryLimit = MaximumLength - Prefix.Length - suffix.Length;
        string normalizedSummary = Normalize(summary);

        if (normalizedSummary.Length == 0)
            normalizedSummary = "Unknown error";

        if (summaryLimit <= 1)
            return Truncate($"{Prefix}{normalizedSummary}{suffix}", MaximumLength);

        return $"{Prefix}{Truncate(normalizedSummary, summaryLimit)}{suffix}";
    }

    /// <summary>Collapses line breaks and repeated whitespace so details remain readable in one line.</summary>
    private static string Normalize(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Limits text without cutting past the native notification-area tooltip capacity.</summary>
    private static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
            return value;

        return maximumLength <= 1
            ? value[..maximumLength]
            : $"{value[..(maximumLength - 1)].TrimEnd()}…";
    }
}
