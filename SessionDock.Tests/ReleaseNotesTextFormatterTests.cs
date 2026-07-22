using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ReleaseNotesTextFormatterTests
{
    [Fact]
    public void Format_SupportedMarkdown_BecomesReadablePlainText()
    {
        const string markdown = """
            # SessionDock 2.3.1

            Introductory text wraps in the source
            but belongs to one paragraph.

            ## Reliability

            - The **first improvement** uses `%LOCALAPPDATA%` and
              continues on another source line.
            - The second improvement remains separate.
            """;

        var result = ReleaseNotesTextFormatter.Format(markdown);

        Assert.Equal(
            """
            SessionDock 2.3.1

            Introductory text wraps in the source but belongs to one paragraph.

            Reliability

            • The first improvement uses %LOCALAPPDATA% and continues on another source line.
            • The second improvement remains separate.
            """,
            result);
    }

    [Fact]
    public void Format_CrlfAndExtraBlankLines_AreNormalized()
    {
        const string markdown = "# Title\r\n\r\n\r\nParagraph one.\r\n\r\nParagraph two.\r\n";

        var result = ReleaseNotesTextFormatter.Format(markdown);

        Assert.Equal("Title\n\nParagraph one.\n\nParagraph two.", result);
    }

    [Fact]
    public void Format_MalformedOrUnsupportedMarkup_RemainsLiteral()
    {
        const string markdown = """
            Not # a heading

            **unclosed emphasis

            `unclosed code

            <notice data-kind="literal">Keep this text</notice>
            """;

        var result = ReleaseNotesTextFormatter.Format(markdown);

        Assert.Equal(markdown, result);
    }

    [Theory]
    [InlineData("###No heading space")]
    [InlineData("####### Too many heading marks")]
    [InlineData("-No list space")]
    [InlineData("---")]
    public void Format_UnsupportedBlockSyntax_IsNotRemoved(string value)
    {
        Assert.Equal(value, ReleaseNotesTextFormatter.Format(value));
    }

    [Fact]
    public void Format_MaximumLengthInput_RemainsBounded()
    {
        var markdown = new string(
            'a',
            SessionDock.ReleaseTrust.ReleaseDescriptorPolicy.MaximumReleaseNotesLength);

        var result = ReleaseNotesTextFormatter.Format(markdown);

        Assert.True(result.Length <= markdown.Length);
    }

    [Fact]
    public void Format_OversizedInput_IsRejected()
    {
        var markdown = new string(
            'a',
            SessionDock.ReleaseTrust.ReleaseDescriptorPolicy.MaximumReleaseNotesLength + 1);

        Assert.Throws<ArgumentException>(() =>
            ReleaseNotesTextFormatter.Format(markdown));
    }

    [Fact]
    public void Format_NullInput_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ReleaseNotesTextFormatter.Format(null!));
    }
}
