using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Serialization;

namespace MMCA.Common.API.Uploads;

/// <summary>
/// An uploaded file read into memory by <c>ReadBoundedAsync</c>: the bytes, and the file name and
/// content type exactly as the client declared them.
/// </summary>
/// <param name="Content">The file's bytes.</param>
/// <param name="FileName">The file name the client declared, not normalized.</param>
/// <param name="ContentType">The content type the client declared.</param>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The bytes go to a command unchanged; a defensive copy would double the upload in memory.")]
public sealed record UploadedFileContent(byte[] Content, string FileName, string ContentType);

/// <summary>
/// A CSV upload read by <c>ReadCsvAsync</c>: the raw upload (for a command or an internal command that
/// stores or re-parses it) beside the parsed table (for the edge's own validation and dry run).
/// </summary>
/// <param name="File">The raw upload, bytes unchanged.</param>
/// <param name="Table">The parsed table.</param>
public sealed record CsvUpload(UploadedFileContent File, CsvTable Table);

/// <summary>
/// Bounded upload reads for a controller action that takes an <see cref="IFormFile"/>: the size check,
/// the single exact read and the refusal error in one call, instead of a hand-written check plus a
/// <see cref="MemoryStream"/> copy per action.
/// </summary>
public static class FormFileUploadExtensions
{
    extension(IFormFile? file)
    {
        /// <summary>
        /// Reads the upload into one buffer of its declared length, refusing a missing, empty, oversize or
        /// truncated file with the caller's own error.
        /// </summary>
        /// <param name="maxBytes">The largest accepted file, inclusive; 1 to <see cref="Array.MaxLength"/>.</param>
        /// <param name="invalidUpload">The error returned for every refused file, unchanged (code, message, source and target).</param>
        /// <param name="cancellationToken">Cancellation token; cancellation propagates as <see cref="OperationCanceledException"/>.</param>
        /// <returns>The bytes with the declared file name and content type, or a failure carrying exactly <paramref name="invalidUpload"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is below 1 or above <see cref="Array.MaxLength"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="invalidUpload"/> is null.</exception>
        /// <remarks>
        /// A declared length over <paramref name="maxBytes"/> is refused before any byte is read. Keep the
        /// action's <c>[RequestSizeLimit]</c>: that bounds the request body, this bounds the file.
        /// </remarks>
        public Task<Result<UploadedFileContent>> ReadBoundedAsync(long maxBytes, Error invalidUpload, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength);
            ArgumentNullException.ThrowIfNull(invalidUpload);

            return ReadBoundedCoreAsync(file, maxBytes, invalidUpload, cancellationToken);
        }

        /// <summary>
        /// Reads the upload as <c>ReadBoundedAsync</c> does, then parses it as CSV (a byte-order mark is
        /// honoured, UTF-8 otherwise).
        /// </summary>
        /// <param name="maxBytes">The largest accepted file, inclusive; 1 to <see cref="Array.MaxLength"/>.</param>
        /// <param name="invalidUpload">The error returned for a missing, empty, oversize or truncated file, unchanged.</param>
        /// <param name="cancellationToken">Cancellation token; cancellation propagates as <see cref="OperationCanceledException"/>.</param>
        /// <returns>
        /// The raw upload beside the parsed table; a failure carrying exactly <paramref name="invalidUpload"/>
        /// for a refused file; or the reader's own errors unchanged (<see cref="CsvReader.MalformedCode"/>
        /// with the line in <c>Target</c>, or <see cref="CsvReader.EmptyCode"/>) for text that does not parse.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is below 1 or above <see cref="Array.MaxLength"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="invalidUpload"/> is null.</exception>
        public Task<Result<CsvUpload>> ReadCsvAsync(long maxBytes, Error invalidUpload, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength);
            ArgumentNullException.ThrowIfNull(invalidUpload);

            return ReadCsvCoreAsync(file, maxBytes, invalidUpload, cancellationToken);
        }
    }

    private static async Task<Result<UploadedFileContent>> ReadBoundedCoreAsync(
        IFormFile? file,
        long maxBytes,
        Error invalidUpload,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0 || file.Length > maxBytes)
            return Result.Failure<UploadedFileContent>(invalidUpload);

        cancellationToken.ThrowIfCancellationRequested();

        var content = new byte[file.Length];
        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return Result.Failure<UploadedFileContent>(invalidUpload);
            }
        }

        return Result.Success(new UploadedFileContent(content, file.FileName, file.ContentType));
    }

    private static async Task<Result<CsvUpload>> ReadCsvCoreAsync(
        IFormFile? file,
        long maxBytes,
        Error invalidUpload,
        CancellationToken cancellationToken)
    {
        var upload = await ReadBoundedCoreAsync(file, maxBytes, invalidUpload, cancellationToken).ConfigureAwait(false);
        if (!upload.TryGetValue(out var content))
            return Result.Failure<CsvUpload>(upload.Errors);

        Result<CsvTable> table;
        var stream = new MemoryStream(content.Content, writable: false);
        await using (stream.ConfigureAwait(false))
        {
            table = await CsvReader.ReadTableAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        return table.TryGetValue(out var parsed)
            ? Result.Success(new CsvUpload(content, parsed))
            : Result.Failure<CsvUpload>(table.Errors);
    }
}
