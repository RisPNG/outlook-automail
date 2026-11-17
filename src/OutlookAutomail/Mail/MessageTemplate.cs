using System.Globalization;
using System.Net;
using System.Text;

namespace OutlookAutomail;

public static class MessageTemplate
{
    public static (string Subject, string Body) Render(FolderConfiguration configuration, DateTimeOffset timestamp, string? installationId = null)
    {
        var machineIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var variableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var machine in configuration.MachineVariables)
        {
            if (!Guid.TryParseExact(machine.InstallationId, "N", out _) || !machineIds.Add(machine.InstallationId))
                throw new InvalidDataException("Each computer's message variables need a unique installation ID.");
            foreach (var (name, value) in machine.Values)
            {
                if (name.Length == 0 || name == "date" || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
                    || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
                    throw new InvalidDataException($"The variable name '{name}' is invalid. Use letters, numbers, and underscores, start with a letter or underscore, and keep date for dates.");
                if (value is null || value.IndexOfAny(['\r', '\n']) >= 0)
                    throw new InvalidDataException($"The value for '{{{name}}}' must be text without line breaks.");
                variableNames.Add(name);
            }
        }
        var variables = configuration.MachineVariables.FirstOrDefault(machine => string.Equals(machine.InstallationId, installationId, StringComparison.OrdinalIgnoreCase))?.Values;
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
        return (ExpandPlaceholders(configuration.Subject, "subject", localTime, configuration.DateFormat, false, variableNames, variables, installationId is not null),
            ExpandPlaceholders(configuration.Body, "body", localTime, configuration.DateFormat, configuration.BodyIsHtml, variableNames, variables, installationId is not null));
    }

    public static string ConvertBodyToHtml(string template)
    {
        var result = new StringBuilder(template.Length);
        foreach (var part in ParseTemplate(template, "body"))
            result.Append(part.Name is not null ? part.Text : WebUtility.HtmlEncode(part.Text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "<br>", StringComparison.Ordinal));
        return result.ToString();
    }

    public static string PrepareBodyForEditor(string body, bool isHtml)
    {
        if (!isHtml)
            return WebUtility.HtmlEncode(body).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "<br>", StringComparison.Ordinal);

        var result = new StringBuilder(body.Length);
        foreach (var part in ParseTemplate(body, "body", false))
            result.Append(part.Name is not null ? WebUtility.HtmlEncode(part.Text) : part.Text);
        return result.ToString();
    }

    public static string ReadBodyFromEditor(string html)
    {
        var result = new StringBuilder(html.Length);
        foreach (var part in ParseTemplate(html, "body", false))
            result.Append(part.Name is not null && part.Complete ? WebUtility.HtmlDecode(part.Text) : part.Text);
        return result.ToString();
    }

    private static string ExpandPlaceholders(string template, string field, DateTimeOffset timestamp, string defaultFormat, bool html,
        IReadOnlySet<string> variableNames, IReadOnlyDictionary<string, string>? variables, bool resolveVariables)
    {
        var result = new StringBuilder(template.Length);
        foreach (var part in ParseTemplate(template, field, variableNames: variableNames))
        {
            if (part.Name is null || part.Name != "date" && !resolveVariables)
            {
                result.Append(part.Text);
                continue;
            }
            string replacement;
            if (part.Escaped)
            {
                replacement = part.Text[1..^1];
            }
            else if (part.Name != "date")
            {
                if (variables is null || !variables.TryGetValue(part.Name, out replacement!))
                    throw new InvalidDataException($"The {field} uses '{{{part.Name}}}', but this computer has no value. Set it in the folder's Variables tab.");
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

    private static IEnumerable<(string Text, string? Format, string? Name, bool Escaped, bool Complete)> ParseTemplate(string template, string field, bool validate = true,
        IReadOnlySet<string>? variableNames = null)
    {
        var position = 0;
        var searchPosition = 0;
        while (searchPosition < template.Length)
        {
            var start = template.IndexOf('{', searchPosition);
            if (start < 0)
                break;
            var isDate = template.AsSpan(start).StartsWith("{date", StringComparison.Ordinal);
            var suffix = isDate ? start + 5 : start + 1;
            var invalidSpacing = isDate && suffix < template.Length && char.IsWhiteSpace(template[suffix]);
            if (isDate && suffix < template.Length && template[suffix] is not (':' or '}') && !invalidSpacing)
                isDate = false;
            var end = template.IndexOf('}', isDate ? suffix : start + 1);
            var name = isDate ? "date" : end >= 0 ? template[(start + 1)..end] : null;
            if (!isDate && (name is null || variableNames is null || !variableNames.Contains(name)))
            {
                searchPosition = start + 1;
                continue;
            }
            if (invalidSpacing && validate)
                throw new InvalidDataException($"The {field} contains an invalid date placeholder. Use {{date}} or {{date:yyyy-MM-dd}} without spaces around date.");
            if (end < 0 && validate)
                throw new InvalidDataException($"The {field} contains an unfinished date placeholder. Use {{date}} or {{date:yyyy-MM-dd}}.");
            var format = isDate && end >= 0 && template[suffix] == ':' ? template[(suffix + 1)..end] : null;
            if (validate && format is not null && (string.IsNullOrWhiteSpace(format) || format.IndexOfAny(['{', '}', '\r', '\n']) >= 0))
                throw new InvalidDataException($"The {field} contains an invalid date placeholder format. Use {{date}} or {{date:yyyy-MM-dd}}.");
            var escaped = end >= 0 && start > position && template[start - 1] == '{' && end + 1 < template.Length && template[end + 1] == '}';
            var tokenStart = escaped ? start - 1 : start;
            if (tokenStart > position)
                yield return (template[position..tokenStart], null, null, false, false);
            position = end < 0 ? template.Length : escaped ? end + 2 : end + 1;
            yield return (template[tokenStart..position], format, name, escaped, end >= 0 && !invalidSpacing);
            searchPosition = position;
        }
        if (position < template.Length)
            yield return (template[position..], null, null, false, false);
    }
}
