namespace cast.Core;

public enum SessionStateFilter
{
    All,
    Completed,
    Active,
    Error
}

public enum SessionDateFilter
{
    All,
    Today,
    LastSevenDays,
    LastThirtyDays
}

public sealed record SessionQuery(
    string WorkflowName = "",
    SessionStateFilter State = SessionStateFilter.All,
    SessionDateFilter Date = SessionDateFilter.All);

public static class SessionQueryMatcher
{
    public static bool Matches(SessionSummary summary, SessionQuery query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(query);

        var stateMatches = query.State switch
        {
            SessionStateFilter.Completed => !summary.IsActive && !summary.HasErrors,
            SessionStateFilter.Active => summary.IsActive,
            SessionStateFilter.Error => summary.HasErrors,
            _ => true
        };
        if (!stateMatches)
        {
            return false;
        }

        var localStartedAt = summary.StartedAt.ToLocalTime();
        var localNow = now.ToLocalTime();
        var dateMatches = query.Date switch
        {
            SessionDateFilter.Today => localStartedAt.Date == localNow.Date,
            SessionDateFilter.LastSevenDays => localStartedAt >= localNow.AddDays(-7),
            SessionDateFilter.LastThirtyDays => localStartedAt >= localNow.AddDays(-30),
            _ => true
        };
        if (!dateMatches)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(query.WorkflowName)
            || summary.WorkflowNames.Any(name => name.Contains(query.WorkflowName, StringComparison.OrdinalIgnoreCase));
    }
}

public static class SessionEventContent
{
    public static string GetText(SessionEvent sessionEvent, TextEncodingKind encoding)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        if (sessionEvent.DisplayText is not null)
        {
            return sessionEvent.DisplayText;
        }

        return sessionEvent.RawBytes.Length == 0
            ? string.Empty
            : TextCodec.Decode(sessionEvent.RawBytes, encoding);
    }

    public static bool Matches(SessionEvent sessionEvent, string? query, TextEncodingKind encoding)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return GetText(sessionEvent, encoding).Contains(query, StringComparison.OrdinalIgnoreCase)
            || HexCodec.Format(sessionEvent.RawBytes).Contains(query, StringComparison.OrdinalIgnoreCase)
            || (sessionEvent.ErrorCode?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (sessionEvent.Metadata?.Any(pair =>
                pair.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pair.Value.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? false);
    }
}
