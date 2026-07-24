namespace PNCPKing.Infrastructure.Services;

internal static class DocumentExceptionDiagnostics
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AggregateException aggregate)
        {
            var aggregateMessages = aggregate
                .Flatten()
                .InnerExceptions
                .Select(Describe)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (aggregateMessages.Length > 0)
            {
                return string.Join(" | ", aggregateMessages);
            }
        }

        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = string.IsNullOrWhiteSpace(current.Message)
                ? current.GetType().Name
                : $"{current.GetType().Name}: {current.Message}";
            if (!messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }
        }

        return string.Join(" → ", messages);
    }
}
