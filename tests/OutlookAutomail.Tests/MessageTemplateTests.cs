using System.Globalization;
using Xunit;

namespace OutlookAutomail.Tests;

public sealed class MessageTemplateTests
{
    [Fact]
    public void ConvertingPlainBodyToHtmlPreservesDateFormatsAndEncodesLiteralText()
    {
        var template = "A < B & C\r\nGenerated {date:'<Document &>' yyyy}\n{{date}} / {dates}";
        var html = MessageTemplate.ConvertBodyToHtml(template);

        Assert.Equal("A &lt; B &amp; C<br>Generated {date:'<Document &>' yyyy}<br>{{date}} / {dates}", html);
        var configuration = new FolderConfiguration { Body = html, BodyIsHtml = true };
        var rendered = MessageTemplate.Render(configuration, DateTimeOffset.Parse("2026-11-16T00:00:00Z"));
        Assert.Equal("A &lt; B &amp; C<br>Generated &lt;Document &amp;&gt; 2026<br>{date} / {dates}", rendered.Body);
    }

    [Fact]
    public void PlainBodyCanBecomeAnEditedHtmlMessageWithItsFirstPhoto()
    {
        const string body = "A < B & C\r\nGenerated {date:'<Document &>' yyyy}\r{{date}} / {dates}";
        const string photo = "<img src=\"cid:signature\" alt=\"Signature\">";

        var editorHtml = MessageTemplate.PrepareBodyForEditor(body, false);

        Assert.Equal("A &lt; B &amp; C<br>Generated {date:&#39;&lt;Document &amp;&gt;&#39; yyyy}<br>{{date}} / {dates}", editorHtml);
        var savedBody = MessageTemplate.ReadBodyFromEditor(editorHtml + photo);
        Assert.Equal(MessageTemplate.ConvertBodyToHtml(body) + photo, savedBody);
        var configuration = new FolderConfiguration { Body = savedBody, BodyIsHtml = true };
        Assert.Equal("A &lt; B &amp; C<br>Generated &lt;Document &amp;&gt; 2026<br>{date} / {dates}" + photo,
            MessageTemplate.Render(configuration, DateTimeOffset.Parse("2026-11-16T00:00:00Z")).Body);
    }

    [Fact]
    public void HtmlDateFormatsAreTextInTheEditorAndReturnToTheirOriginalConfiguration()
    {
        const string body = "<p>{date:'<Document & \"quoted\">' yyyy} / {date:\"It's <Other &>\" yyyy} / {date}</p>";

        var editorHtml = MessageTemplate.PrepareBodyForEditor(body, true);

        Assert.Equal("<p>{date:&#39;&lt;Document &amp; &quot;quoted&quot;&gt;&#39; yyyy} / {date:&quot;It&#39;s &lt;Other &amp;&gt;&quot; yyyy} / {date}</p>", editorHtml);
        var savedBody = MessageTemplate.ReadBodyFromEditor(editorHtml);
        Assert.Equal(body, savedBody);
        var configuration = new FolderConfiguration { Body = savedBody, BodyIsHtml = true };
        Assert.Equal("<p>&lt;Document &amp; &quot;quoted&quot;&gt; 2026 / It&#39;s &lt;Other &amp;&gt; 2026 / 16/11/2026</p>",
            MessageTemplate.Render(configuration, DateTimeOffset.Parse("2026-11-16T00:00:00Z")).Body);
    }

    [Fact]
    public void EditorRoundTripPreservesLiteralEntitiesEscapesCssAndJson()
    {
        const string body = "<p title=\"A &amp; B\">&amp;amp; {{date:'&amp; &lt; &quot;' yyyy}} {date:'&lt;' yyyy} {{date:Q}} {dates:&#39;&amp;&#39;}</p><style>.notice { color: red; }</style><pre>{\"entity\": \"&amp;\", \"amount\": 1}</pre>";

        var editorHtml = MessageTemplate.PrepareBodyForEditor(body, true);

        Assert.Contains("{{date:&#39;&amp;amp; &amp;lt; &amp;quot;&#39; yyyy}}", editorHtml, StringComparison.Ordinal);
        Assert.Equal(body, MessageTemplate.ReadBodyFromEditor(editorHtml));
    }

    [Fact]
    public void ReadingSerializedEditorHtmlDecodesOneLayerOnlyInsideCompleteDateExpressions()
    {
        const string html = "<p>&amp;amp; {date:'&amp;amp;' yyyy} / {{date:\"&lt;tag &amp; value&gt;\"}} / {dates:'&amp;'} / {date:'&lt;unfinished</p>";

        Assert.Equal("<p>&amp;amp; {date:'&amp;' yyyy} / {{date:\"<tag & value>\"}} / {dates:'&amp;'} / {date:'&lt;unfinished</p>",
            MessageTemplate.ReadBodyFromEditor(html));
    }

    [Theory]
    [InlineData("{date:Q}")]
    [InlineData("{date:'<unfinished &}")]
    [InlineData("{date:}")]
    [InlineData("{date:yyyy{MM}")]
    [InlineData("{date:yyyy\nMM}")]
    [InlineData("{date }")]
    public void InvalidCompleteDateExpressionsCanBeEditedButStillCannotRender(string body)
    {
        var editorHtml = MessageTemplate.PrepareBodyForEditor(body, true);
        var savedBody = MessageTemplate.ReadBodyFromEditor(editorHtml);

        Assert.Equal(body, savedBody);
        var configuration = new FolderConfiguration { Body = savedBody, BodyIsHtml = true };
        Assert.Throws<InvalidDataException>(() => MessageTemplate.Render(configuration, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("{date", "{date")]
    [InlineData("{date:", "{date:")]
    [InlineData("{date ", "{date ")]
    [InlineData("Draft {date:'<unfinished &", "Draft {date:&#39;&lt;unfinished &amp;")]
    [InlineData("Draft {date <unfinished &", "Draft {date &lt;unfinished &amp;")]
    public void UnfinishedDateExpressionsStayVisibleWithoutDecodingUnfinishedText(string body, string expectedHtml)
    {
        var editorHtml = MessageTemplate.PrepareBodyForEditor(body, true);

        Assert.Equal(expectedHtml, editorHtml);
        var savedBody = MessageTemplate.ReadBodyFromEditor(editorHtml);
        Assert.Equal(expectedHtml, savedBody);
        var configuration = new FolderConfiguration { Body = savedBody, BodyIsHtml = true };
        Assert.Throws<InvalidDataException>(() => MessageTemplate.Render(configuration, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EscapedDatePlaceholdersAreDisplayedAsTextInAnHtmlBody()
    {
        var configuration = new FolderConfiguration { Body = "<p>{{date:'<Document &>' yyyy}}</p>", BodyIsHtml = true };

        Assert.Equal("<p>{date:&#39;&lt;Document &amp;&gt;&#39; yyyy}</p>", MessageTemplate.Render(configuration, DateTimeOffset.UtcNow).Body);
    }

    [Fact]
    public void SubjectAndBodyUseTheChosenFormatAndIndependentInlineFormats()
    {
        var configuration = new FolderConfiguration
        {
            Subject = "Documents - {date}",
            Body = "As of {date:dd MMMM yyyy} at {date:HH:mm zzz}. Reference {date:yyyy-MM-dd}.",
            DateFormat = "dd/MM/yyyy",
            DateTimeZoneId = "Singapore Standard Time"
        };
        var rendered = MessageTemplate.Render(configuration, DateTimeOffset.Parse("2026-11-15T17:25:00Z"));

        Assert.Equal("Documents - 16/11/2026", rendered.Subject);
        Assert.Equal("As of 16 November 2026 at 01:25 +08:00. Reference 2026-11-16.", rendered.Body);
    }

    [Fact]
    public void MonthNamesAndDateSeparatorsDoNotDependOnTheWorkersCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var configuration = new FolderConfiguration { Subject = "{date:dddd, dd MMMM yyyy}", Body = "{date}" };
            var rendered = MessageTemplate.Render(configuration, DateTimeOffset.Parse("2026-11-16T00:00:00Z"));

            Assert.Equal("Monday, 16 November 2026", rendered.Subject);
            Assert.Equal("16/11/2026", rendered.Body);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("2026-03-08T06:30:00Z", "2026-03-08 01:30 -05:00")]
    [InlineData("2026-03-08T07:30:00Z", "2026-03-08 03:30 -04:00")]
    [InlineData("2026-11-01T05:30:00Z", "2026-11-01 01:30 -04:00")]
    [InlineData("2026-11-01T06:30:00Z", "2026-11-01 01:30 -05:00")]
    public void TimeZoneConversionUsesTheOffsetAtTheCapturedInstant(string instant, string expected)
    {
        var configuration = new FolderConfiguration
        {
            Subject = "{date:yyyy-MM-dd HH:mm zzz}",
            DateTimeZoneId = "Eastern Standard Time"
        };

        Assert.Equal(expected, MessageTemplate.Render(configuration, DateTimeOffset.Parse(instant)).Subject);
    }

    [Fact]
    public void HtmlMarkupStaysLiteralWhileDateReplacementsAreEncoded()
    {
        var configuration = new FolderConfiguration
        {
            Subject = "{date:'<Document &>' yyyy}",
            Body = "<p>{date:'<Document &>' yyyy}</p><style>.notice { color: red; }</style>",
            BodyIsHtml = true
        };

        var rendered = MessageTemplate.Render(configuration, DateTimeOffset.Parse("2026-11-16T00:00:00Z"));

        Assert.Equal("<Document &> 2026", rendered.Subject);
        Assert.Equal("<p>&lt;Document &amp;&gt; 2026</p><style>.notice { color: red; }</style>", rendered.Body);
    }

    [Fact]
    public void DateEscapesAndOtherBracesRemainLiteral()
    {
        var configuration = new FolderConfiguration
        {
            Subject = "{{date}} / {{date:Q}} / {date:yyyy}",
            Body = "{\"amount\": 1} {dates} {name} {{other}} {date_created}"
        };

        var rendered = MessageTemplate.Render(configuration, DateTimeOffset.Parse("2026-11-16T00:00:00Z"));

        Assert.Equal("{date} / {date:Q} / 2026", rendered.Subject);
        Assert.Equal(configuration.Body, rendered.Body);
    }

    [Theory]
    [InlineData("{date:")]
    [InlineData("{date")]
    [InlineData("{date ")]
    [InlineData("{date }")]
    [InlineData("{date:}")]
    [InlineData("{date:Q}")]
    [InlineData("{date:'unfinished}")]
    [InlineData("{date:yyyy{MM}")]
    [InlineData("{date:yyyy\nMM}")]
    public void InvalidSubjectDateExpressionsAreRejected(string subject)
    {
        var configuration = new FolderConfiguration { Subject = subject };

        var error = Assert.Throws<InvalidDataException>(() => MessageTemplate.Render(configuration, DateTimeOffset.UtcNow));

        Assert.Contains("subject", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidBodyExpressionIdentifiesTheBody()
    {
        var configuration = new FolderConfiguration { Body = "As of {date:Q}" };

        var error = Assert.Throws<InvalidDataException>(() => MessageTemplate.Render(configuration, DateTimeOffset.UtcNow));

        Assert.Contains("body", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("yyyy{MM}")]
    [InlineData("yyyy\nMM")]
    [InlineData("'unfinished")]
    public void InvalidDefaultFormatsAreRejectedBeforeSaving(string format)
    {
        using var folder = new TestFolder();
        folder.Configuration.DateFormat = format;

        Assert.Throws<InvalidDataException>(folder.Save);
    }

    [Fact]
    public void InvalidTimeZoneIsRejectedBeforeSaving()
    {
        using var folder = new TestFolder();
        folder.Configuration.DateTimeZoneId = "Not a time zone";

        Assert.Throws<InvalidDataException>(folder.Save);
    }

    [Fact]
    public void SharedYamlPreservesDateSettingsAndMessageTemplates()
    {
        using var folder = new TestFolder();
        folder.Configuration.Subject = "Orders - {date}";
        folder.Configuration.Body = "Generated {date:dddd, dd MMMM yyyy}.";
        folder.Configuration.DateFormat = "yyyy-MM-dd";
        folder.Configuration.DateTimeZoneId = "Singapore Standard Time";
        folder.Save();

        var loaded = FolderStore.LoadConfiguration(folder.Root);

        Assert.Equal(folder.Configuration.DateFormat, loaded.DateFormat);
        Assert.Equal(folder.Configuration.DateTimeZoneId, loaded.DateTimeZoneId);
        Assert.Equal(folder.Configuration.Subject, loaded.Subject);
        Assert.Equal(folder.Configuration.Body, loaded.Body);
        Assert.Equal("Orders - 2030-01-04", MessageTemplate.Render(loaded, DateTimeOffset.Parse("2030-01-03T18:00:00Z")).Subject);
    }

    [Fact]
    public void ExistingYamlWithoutDateSettingsKeepsLiteralContentAndUsesDeterministicDefaults()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Root, "config.yml");
        File.WriteAllLines(path, File.ReadAllLines(path).Where(line => !line.StartsWith("date_format:", StringComparison.Ordinal)
            && !line.StartsWith("date_time_zone_id:", StringComparison.Ordinal)));

        var loaded = FolderStore.LoadConfiguration(folder.Root);
        var rendered = MessageTemplate.Render(loaded, DateTimeOffset.Parse("2030-01-03T18:00:00Z"));

        Assert.Equal("dd/MM/yyyy", loaded.DateFormat);
        Assert.Equal("UTC", loaded.DateTimeZoneId);
        Assert.Equal(folder.Configuration.Subject, rendered.Subject);
        Assert.Equal(folder.Configuration.Body, rendered.Body);
    }
}
