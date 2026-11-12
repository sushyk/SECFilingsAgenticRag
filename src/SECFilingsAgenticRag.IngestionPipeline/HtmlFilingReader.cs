using System.Text;
using Microsoft.Extensions.DataIngestion;
using ReverseMarkdown;

namespace SECFilingsAgenticRag.IngestionPipeline;

// MEDI's MarkdownReader and MarkItDownReader read Markdown or convert generic HTML; neither strips inline XBRL, flattens
// 10-K layout tables or promotes "Item 1A." lines to headings, so this reader prepares the HTML and delegates to MarkdownReader.
public sealed class HtmlFilingReader : IngestionDocumentReader
{
    private static readonly Config MarkdownConfig = new()
    {
        GithubFlavored = true,
        Tags = { Unknown = Config.UnknownTagsOption.Bypass },
        Formatting = { RemoveComments = true },
        Links = { SmartHref = true },
    };

    private readonly MarkdownReader _markdownReader = new();

    public override async Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);

        var html = HtmlPreparation.Prepare(Decode(buffer.ToArray()));
        var markdown = HeadingPromotion.Promote(new Converter(MarkdownConfig).Convert(html));

        using var markdownStream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        return await _markdownReader.ReadAsync(markdownStream, StagedFiling.DocumentIdOf(identifier), "text/markdown", cancellationToken);
    }

    // Some filings predate UTF-8; strict decoding detects that instead of silently producing U+FFFD.
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes);
        }
    }
}
