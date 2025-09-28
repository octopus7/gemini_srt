using GeminiSrtTranslator.Models;
using System.Globalization;
using System.IO;

namespace GeminiSrtTranslator.Services;

public static class SrtService
{
    private static readonly string[] TimeFormats =
    {
        @"hh\:mm\:ss\,fff",
        @"hh\:mm\:ss\;fff"
    };

    public static async Task<IReadOnlyList<SubtitleEntry>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var entries = new List<SubtitleEntry>();

        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var indexLine = await ReadNonEmptyLineAsync(reader, cancellationToken).ConfigureAwait(false);
            if (indexLine is null)
            {
                break;
            }

            if (!int.TryParse(indexLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                // Skip invalid blocks
                await SkipUntilBlankLineAsync(reader, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var timeLine = await reader.ReadLineAsync().ConfigureAwait(false);
            if (timeLine is null)
            {
                break;
            }

            var timeParts = timeLine.Split("-->", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (timeParts.Length != 2 || !TryParseTimestamp(timeParts[0], out var start) || !TryParseTimestamp(timeParts[1], out var end))
            {
                await SkipUntilBlankLineAsync(reader, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var textLines = new List<string>();
            while (true)
            {
                var nextLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (nextLine is null || string.IsNullOrWhiteSpace(nextLine))
                {
                    break;
                }

                textLines.Add(nextLine);
            }

            entries.Add(new SubtitleEntry
            {
                Index = index,
                StartTime = start,
                EndTime = end,
                Text = string.Join(Environment.NewLine, textLines)
            });
        }

        return entries;
    }

    public static async Task SaveAsync(string path, IEnumerable<SubtitleEntry> entries, bool useTranslatedText, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);

        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream);

        var ordered = entries.OrderBy(e => e.Index).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = ordered[i];
            await writer.WriteLineAsync(entry.Index.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await writer.WriteLineAsync($"{FormatTimestamp(entry.StartTime)} --> {FormatTimestamp(entry.EndTime)}").ConfigureAwait(false);

            var content = useTranslatedText && !string.IsNullOrWhiteSpace(entry.TranslatedText)
                ? entry.TranslatedText
                : entry.Text;

            foreach (var line in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }

            if (i < ordered.Count - 1)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
            }
        }
    }

    private static string FormatTimestamp(TimeSpan timeSpan)
        => timeSpan.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

    private static bool TryParseTimestamp(string value, out TimeSpan result)
        => TimeSpan.TryParseExact(value, TimeFormats, CultureInfo.InvariantCulture, out result);

    private static async Task<string?> ReadNonEmptyLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? line;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            line = await reader.ReadLineAsync().ConfigureAwait(false);
        }
        while (line is not null && string.IsNullOrWhiteSpace(line));

        return line;
    }

    private static async Task SkipUntilBlankLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null || string.IsNullOrWhiteSpace(line))
            {
                break;
            }
        }
    }
}
