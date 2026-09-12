using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Unicode;

namespace MMCA.Common.Application.Interfaces.Infrastructure.Storage;

/// <summary>The document formats a caller is willing to accept from an upload.</summary>
[Flags]
public enum DocumentFormats
{
    /// <summary>Nothing is accepted.</summary>
    None = 0,

    /// <summary>Portable Document Format (<c>.pdf</c>).</summary>
    Pdf = 1,

    /// <summary>Office Open XML presentation (<c>.pptx</c>).</summary>
    Presentation = 2,

    /// <summary>Office Open XML word-processing document (<c>.docx</c>).</summary>
    WordDocument = 4,

    /// <summary>Office Open XML spreadsheet (<c>.xlsx</c>).</summary>
    Spreadsheet = 8,

    /// <summary>All three Office Open XML payloads.</summary>
    OfficeOpenXml = Presentation | WordDocument | Spreadsheet,

    /// <summary>A plain zip archive (<c>.zip</c>).</summary>
    Zip = 16,

    /// <summary>UTF-8 plain text (<c>.txt</c>).</summary>
    PlainText = 32,

    /// <summary>UTF-8 markdown (<c>.md</c>, <c>.markdown</c>).</summary>
    Markdown = 64,

    /// <summary>Every format this sniffer knows.</summary>
    All = Pdf | OfficeOpenXml | Zip | PlainText | Markdown,
}

/// <summary>
/// Dependency-free sniffing for uploaded documents, the sibling of <see cref="ImageContentSniffer"/>
/// (ADR-045). A payload is accepted only when the actual bytes and the file-name extension agree on one
/// of the formats the caller allowed; the client-declared content type is never consulted, so a
/// <c>.pdf</c> name over zip bytes, or an executable renamed to <c>.docx</c>, is rejected. App-specific
/// size limits, storage naming and error codes stay in the calling handler.
/// </summary>
public static class DocumentContentSniffer
{
    /// <summary>
    /// The entry name every Office Open XML package carries at its root; its presence is what separates
    /// a real <c>.pptx</c>/<c>.docx</c>/<c>.xlsx</c> from an ordinary zip renamed to that extension.
    /// </summary>
    private const string ContentTypesEntry = "[Content_Types].xml";

    /// <summary>
    /// Zip-bomb guard: an archive declaring more entries than this is rejected without inspecting them.
    /// A genuine Office document holds tens of parts, never thousands.
    /// </summary>
    private const int MaxInspectedEntries = 4096;

    /// <summary>
    /// The one place extension, format and canonical MIME type are tied together. Compared with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>, so <c>Deck.PPTX</c> resolves exactly like
    /// <c>deck.pptx</c> without lower-casing the caller's name.
    /// </summary>
    private static readonly Dictionary<string, (DocumentFormats Format, string MimeType)> KnownExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = (DocumentFormats.Pdf, "application/pdf"),
            [".pptx"] = (DocumentFormats.Presentation, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            [".docx"] = (DocumentFormats.WordDocument, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            [".xlsx"] = (DocumentFormats.Spreadsheet, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            [".zip"] = (DocumentFormats.Zip, "application/zip"),
            [".txt"] = (DocumentFormats.PlainText, "text/plain"),
            [".md"] = (DocumentFormats.Markdown, "text/markdown"),
            [".markdown"] = (DocumentFormats.Markdown, "text/markdown"),
        };

    /// <summary>
    /// Detects the canonical MIME type of an uploaded document.
    /// </summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <param name="fileName">The user-supplied file name; only its extension is read, case-insensitively.</param>
    /// <param name="allowed">The formats the caller accepts.</param>
    /// <returns>
    /// The canonical MIME type when the bytes and the extension agree on an allowed format;
    /// otherwise <see langword="null"/>.
    /// </returns>
    public static string? Detect(ReadOnlySpan<byte> content, string fileName, DocumentFormats allowed)
    {
        if (content.IsEmpty || !TryResolve(fileName, allowed, out DocumentFormats format, out string mimeType))
        {
            return null;
        }

        if (DocumentFormats.OfficeOpenXml.HasFlag(format))
        {
            // The package check reads the zip central directory, which needs a seekable stream: copy
            // only once the cheap signature test has already passed.
            return IsZip(content) && IsOfficeOpenXml(content.ToArray()) ? mimeType : null;
        }

        return MatchesSignature(format, content) ? mimeType : null;
    }

    /// <summary>
    /// Detects the canonical MIME type of an uploaded document, avoiding the defensive copy the
    /// span overload makes for Office Open XML payloads.
    /// </summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <param name="fileName">The user-supplied file name; only its extension is read, case-insensitively.</param>
    /// <param name="allowed">The formats the caller accepts.</param>
    /// <returns>
    /// The canonical MIME type when the bytes and the extension agree on an allowed format;
    /// otherwise <see langword="null"/>.
    /// </returns>
    public static string? Detect(ReadOnlyMemory<byte> content, string fileName, DocumentFormats allowed)
    {
        if (content.IsEmpty || !TryResolve(fileName, allowed, out DocumentFormats format, out string mimeType))
        {
            return null;
        }

        bool matches = DocumentFormats.OfficeOpenXml.HasFlag(format)
            ? IsOfficeOpenXml(content)
            : MatchesSignature(format, content.Span);

        return matches ? mimeType : null;
    }

    /// <summary>Whether the content starts with the <c>%PDF-</c> header prefix.</summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <returns><see langword="true"/> when the leading bytes match the PDF signature.</returns>
    public static bool IsPdf(ReadOnlySpan<byte> content) =>
        content.Length >= 5 && content[..5].SequenceEqual("%PDF-"u8);

    /// <summary>Whether the content starts with the local-file-header signature of a zip archive (<c>PK 03 04</c>).</summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <returns><see langword="true"/> when the leading bytes match the zip signature.</returns>
    public static bool IsZip(ReadOnlySpan<byte> content) =>
        content.Length >= 4 && content[..4].SequenceEqual((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]);

    /// <summary>
    /// Whether the content is a zip archive that carries an Office Open XML package root
    /// (<c>[Content_Types].xml</c>). Only entry names are read, never entry streams, and an archive
    /// declaring more than 4096 entries is rejected outright, so a zip bomb costs nothing to refuse.
    /// A malformed or truncated archive answers <see langword="false"/> rather than throwing.
    /// </summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <returns><see langword="true"/> when the archive looks like an Office Open XML package.</returns>
    public static bool IsOfficeOpenXml(ReadOnlyMemory<byte> content)
    {
        if (!IsZip(content.Span))
        {
            return false;
        }

        try
        {
            ArraySegment<byte> buffer =
                MemoryMarshal.TryGetArray(content, out ArraySegment<byte> segment) && segment.Array is not null
                    ? segment
                    : new ArraySegment<byte>(content.ToArray());

            using var stream = new MemoryStream(buffer.Array!, buffer.Offset, buffer.Count, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            if (archive.Entries.Count > MaxInspectedEntries)
            {
                return false;
            }

            // Entry NAMES only: opening an entry stream is what a zip bomb is waiting for.
            return archive.Entries.Any(entry =>
                string.Equals(entry.FullName, ContentTypesEntry, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            // A truncated archive reports the missing bytes rather than malformed ones.
            return false;
        }
    }

    /// <summary>
    /// Whether the content decodes as UTF-8 text: a byte-order mark is allowed, a NUL byte anywhere
    /// disqualifies the payload (that is how a binary file gives itself away), and empty content is
    /// never text.
    /// </summary>
    /// <param name="content">The uploaded bytes, from the start of the payload.</param>
    /// <returns><see langword="true"/> when every byte forms valid, NUL-free UTF-8.</returns>
    public static bool IsPlainUtf8Text(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> text = content;
        if (text.Length >= 3 && text[..3].SequenceEqual((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            text = text[3..];
        }

        return !text.IsEmpty && !text.Contains((byte)0) && Utf8.IsValid(text);
    }

    /// <summary>
    /// Resolves the file name's extension to a known format the caller allows.
    /// </summary>
    private static bool TryResolve(string fileName, DocumentFormats allowed, out DocumentFormats format, out string mimeType)
    {
        format = DocumentFormats.None;
        mimeType = string.Empty;

        string? extension = Extension(fileName);
        if (extension is null
            || !KnownExtensions.TryGetValue(extension, out (DocumentFormats Format, string MimeType) known)
            || !allowed.HasFlag(known.Format))
        {
            return false;
        }

        format = known.Format;
        mimeType = known.MimeType;
        return true;
    }

    /// <summary>
    /// Whether the leading bytes match the signature of a format that a signature alone can decide.
    /// Office Open XML is not one of them: its packages are decided by inspecting the archive.
    /// </summary>
    private static bool MatchesSignature(DocumentFormats format, ReadOnlySpan<byte> content)
    {
        if (format == DocumentFormats.Pdf)
        {
            return IsPdf(content);
        }

        if (format == DocumentFormats.Zip)
        {
            return IsZip(content);
        }

        return format is DocumentFormats.PlainText or DocumentFormats.Markdown
            && IsPlainUtf8Text(content);
    }

    /// <summary>
    /// The extension including its leading dot, or <see langword="null"/> when the name is blank, has
    /// no extension, or is nothing but an extension (a dot-file such as <c>.pdf</c>).
    /// </summary>
    private static string? Extension(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        int dot = fileName.LastIndexOf('.');
        return dot <= 0 || dot == fileName.Length - 1 ? null : fileName[dot..];
    }
}
