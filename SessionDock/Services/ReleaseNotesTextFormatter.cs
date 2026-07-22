using System.Text;
using SessionDock.ReleaseTrust;

namespace SessionDock.Services;

internal static class ReleaseNotesTextFormatter
{
    public static string Format(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Length > ReleaseDescriptorPolicy.MaximumReleaseNotesLength)
        {
            throw new ArgumentException(
                "Release notes exceed the supported display length.",
                nameof(markdown));
        }

        if (markdown.Any(character =>
                char.IsControl(character) &&
                character is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException(
                "Release notes contain unsupported control characters.",
                nameof(markdown));
        }

        var normalized = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
            return string.Empty;

        var lines = normalized.Split('\n');
        var blocks = new List<DisplayBlock>();
        for (var index = 0; index < lines.Length;)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            if (TryReadHeading(lines[index], out var heading))
            {
                blocks.Add(new DisplayBlock(
                    DisplayBlockKind.Text,
                    FormatInline(heading)));
                index++;
                continue;
            }

            if (TryReadBullet(lines[index], out var bullet))
            {
                var parts = new List<string> { bullet };
                index++;
                while (index < lines.Length &&
                       !string.IsNullOrWhiteSpace(lines[index]) &&
                       !TryReadHeading(lines[index], out _) &&
                       !TryReadBullet(lines[index], out _))
                {
                    parts.Add(lines[index].Trim());
                    index++;
                }

                blocks.Add(new DisplayBlock(
                    DisplayBlockKind.ListItem,
                    $"• {FormatInline(string.Join(' ', parts))}"));
                continue;
            }

            var paragraph = new List<string>();
            while (index < lines.Length &&
                   !string.IsNullOrWhiteSpace(lines[index]) &&
                   !TryReadHeading(lines[index], out _) &&
                   !TryReadBullet(lines[index], out _))
            {
                paragraph.Add(lines[index].Trim());
                index++;
            }

            blocks.Add(new DisplayBlock(
                DisplayBlockKind.Text,
                FormatInline(string.Join(' ', paragraph))));
        }

        var output = new StringBuilder(normalized.Length);
        for (var index = 0; index < blocks.Count; index++)
        {
            if (index > 0)
            {
                output.Append(
                    blocks[index - 1].Kind == DisplayBlockKind.ListItem &&
                    blocks[index].Kind == DisplayBlockKind.ListItem
                        ? '\n'
                        : "\n\n");
            }

            output.Append(blocks[index].Text);
        }

        return output.ToString();
    }

    private static bool TryReadHeading(string line, out string heading)
    {
        var trimmed = line.TrimStart();
        var markerCount = 0;
        while (markerCount < trimmed.Length && trimmed[markerCount] == '#')
            markerCount++;

        if (markerCount is < 1 or > 6 ||
            markerCount >= trimmed.Length ||
            !char.IsWhiteSpace(trimmed[markerCount]))
        {
            heading = string.Empty;
            return false;
        }

        heading = trimmed[(markerCount + 1)..].Trim();
        return heading.Length > 0;
    }

    private static bool TryReadBullet(string line, out string bullet)
    {
        var indentation = 0;
        while (indentation < line.Length && line[indentation] == ' ')
            indentation++;

        if (indentation > 3 ||
            indentation + 1 >= line.Length ||
            line[indentation] is not ('-' or '*' or '+') ||
            !char.IsWhiteSpace(line[indentation + 1]))
        {
            bullet = string.Empty;
            return false;
        }

        bullet = line[(indentation + 2)..].Trim();
        return bullet.Length > 0;
    }

    private static string FormatInline(string value)
    {
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            if (value[index] == '`')
            {
                var closing = value.IndexOf('`', index + 1);
                if (closing > index + 1)
                {
                    output.Append(value, index + 1, closing - index - 1);
                    index = closing + 1;
                    continue;
                }
            }

            if (index + 1 < value.Length &&
                ((value[index] == '*' && value[index + 1] == '*') ||
                 (value[index] == '_' && value[index + 1] == '_')))
            {
                var delimiter = value.Substring(index, 2);
                var closing = value.IndexOf(
                    delimiter,
                    index + delimiter.Length,
                    StringComparison.Ordinal);
                if (closing > index + delimiter.Length)
                {
                    output.Append(
                        value,
                        index + delimiter.Length,
                        closing - index - delimiter.Length);
                    index = closing + delimiter.Length;
                    continue;
                }
            }

            output.Append(value[index]);
            index++;
        }

        return output.ToString();
    }

    private enum DisplayBlockKind
    {
        Text,
        ListItem
    }

    private sealed record DisplayBlock(DisplayBlockKind Kind, string Text);
}
