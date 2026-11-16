using System.Globalization;
using System.Net;
using System.Text;

namespace OutlookAutomail;

public static class MessageTemplate
{
    public static (string Subject, string Body) Render(FolderConfiguration configuration, DateTimeOffset timestamp)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(configuration.DateTimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new InvalidDataException("Choose a valid message date time zone.", exception);
        }
        var localTime = TimeZoneInfo.ConvertTime(timestamp, zone);
        if (string.IsNullOrWhiteSpace(configuration.DateFormat)
            || configuration.DateFormat.IndexOfAny(['{', '}', '\r', '\n']) >= 0)
            throw new InvalidDataException("Enter a date format without braces or line breaks, for example dd/MM/yyyy.");
        try
        {
            _ = localTime.ToString(configuration.DateFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The default date format is invalid. Use a .NET date format, for example dd/MM/yyyy.", exception);
        }
        return (ExpandDatePlaceholders(configuration.Subject, "subject", localTime, configuration.DateFormat, false),
            ExpandDatePlaceholders(configuration.Body, "body", localTime, configuration.DateFormat, configuration.BodyIsHtml));
    }

    public static string ConvertBodyToHtml(string template)
    {
        var result = new StringBuilder(template.Length);
        foreach (var part in ParseDateTemplate(template, "body"))
            result.Append(part.IsDate ? part.Text : WebUtility.HtmlEncode(part.Text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "<br>", StringComparison.Ordinal));
        return result.ToString();
    }

    public static string PrepareBodyForEditor(string body, bool isHtml)
    {
        if (!isHtml)
            return WebUtility.HtmlEncode(body).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "<br>", StringComparison.Ordinal);

        var result = new StringBuilder(body.Length);
        foreach (var part in ParseDateTemplate(body, "body", false))
            result.Append(part.IsDate ? WebUtility.HtmlEncode(part.Text) : part.Text);
        return result.ToString();
    }

    public static string ReadBodyFromEditor(string html)
    {
        var result = new StringBuilder(html.Length);
        foreach (var part in ParseDateTemplate(html, "body", false))
            result.Append(part.IsDate && part.Complete ? WebUtility.HtmlDecode(part.Text) : part.Text);
        return result.ToString();
    }

    private static string ExpandDatePlaceholders(string template, string field, DateTimeOffset timestamp, string defaultFormat, bool html)
    {
        var result = new StringBuilder(template.Length);
        foreach (var part in ParseDateTemplate(template, field))
        {
            if (!part.IsDate)
            {
                result.Append(part.Text);
                continue;
            }
            string replacement;
            if (part.Escaped)
            {
                replacement = part.Text[1..^1];
            }
            else
            {
                var format = part.Format ?? defaultFormat;
                try
                {
                    replacement = timestamp.ToString(format, CultureInfo.InvariantCulture);
                }
                catch (FormatException exception)
                {
                    throw new InvalidDataException($"The {field} contains an invalid date format '{format}'. Use a .NET date format, for example yyyy-MM-dd.", exception);
                }
                if (replacement.IndexOfAny(['\r', '\n']) >= 0)
                    throw new InvalidDataException($"The {field} date format cannot produce line breaks.");
            }
            result.Append(html ? WebUtility.HtmlEncode(replacement) : replacement);
        }
        return result.ToString();
    }

    private static IEnumerable<(string Text, string? Format, bool IsDate, bool Escaped, bool Complete)> ParseDateTemplate(string template, string field, bool validate = true)
    {
        var position = 0;
        var searchPosition = 0;
        while (searchPosition < template.Length)
        {
            var start = template.IndexOf("{date", searchPosition, StringComparison.Ordinal);
            if (start < 0)
                break;
            var suffix = start + 5;
            var invalidSpacing = suffix < template.Length && char.IsWhiteSpace(template[suffix]);
            if (invalidSpacing && validate)
                throw new InvalidDataException($"The {field} contains an invalid date placeholder. Use {{date}} or {{date:yyyy-MM-dd}} without spaces around date.");
            if (suffix < template.Length && template[suffix] is not (':' or '}') && !invalidSpacing)
            {
                searchPosition = suffix;
                continue;
            }
            var end = template.IndexOf('}', suffix);
            if (end < 0 && validate)
                throw new InvalidDataException($"The {field} contains an unfinished date placeholder. Use {{date}} or {{date:yyyy-MM-dd}}.");
            var format = end >= 0 && template[suffix] == ':' ? template[(suffix + 1)..end] : null;
            if (validate && format is not null && (string.IsNullOrWhiteSpace(format) || format.IndexOfAny(['{', '}', '\r', '\n']) >= 0))
                throw new InvalidDataException($"The {field} contains an invalid date placeholder format. Use {{date}} or {{date:yyyy-MM-dd}}.");
            var escaped = end >= 0 && start > position && template[start - 1] == '{' && end + 1 < template.Length && template[end + 1] == '}';
            var tokenStart = escaped ? start - 1 : start;
            if (tokenStart > position)
                yield return (template[position..tokenStart], null, false, false, false);
            position = end < 0 ? template.Length : escaped ? end + 2 : end + 1;
            yield return (template[tokenStart..position], format, true, escaped, end >= 0 && !invalidSpacing);
            searchPosition = position;
        }
        if (position < template.Length)
            yield return (template[position..], null, false, false, false);
    }
}
