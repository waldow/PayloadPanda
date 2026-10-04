using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;

namespace PayloadPanda.Services;

public abstract record BodySegment(long Length);

public sealed record BytesSegment(byte[] Bytes) : BodySegment(Bytes.LongLength);

/// <summary>A file streamed into the body at send time; <see cref="BodySegment.Length"/> is its size when composed.</summary>
public sealed record FileSegment(string Path, long Length) : BodySegment(Length);

/// <summary>
/// A request body as a sequence of in-memory bytes and file references. The exact
/// length is known without reading any file, and files are only read while the body is
/// written to the connection — so uploading a large file neither freezes the UI nor
/// holds the whole file in memory.
/// </summary>
public sealed class WireBody
{
    private const int CopyBufferSize = 80 * 1024;

    public static WireBody Empty { get; } = new([]);

    public WireBody(IReadOnlyList<BodySegment> segments)
    {
        Segments = segments;
        Length = segments.Sum(s => s.Length);
    }

    public static WireBody FromBytes(byte[] bytes) => bytes.Length == 0 ? Empty : new([new BytesSegment(bytes)]);

    public IReadOnlyList<BodySegment> Segments { get; }
    public long Length { get; }

    public async Task WriteToAsync(Stream destination, CancellationToken ct)
    {
        foreach (var segment in Segments)
        {
            switch (segment)
            {
                case BytesSegment bytes:
                    await destination.WriteAsync(bytes.Bytes, ct).ConfigureAwait(false);
                    break;
                case FileSegment file:
                    await CopyFileAsync(file, destination, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>The whole body in memory. For tests and small bodies; reads every file.</summary>
    public byte[] ToArray()
    {
        using var buffer = new MemoryStream();
        WriteToAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static async Task CopyFileAsync(FileSegment file, Stream destination, CancellationToken ct)
    {
        // ReadWrite/Delete sharing: a file another program still has open can be uploaded.
        await using var source = new FileStream(file.Path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0
        });

        // Content-Length was sent from the composed size, so a changed file would corrupt
        // the request (HttpClient rejects it; a raw socket would leave the server waiting).
        if (source.Length != file.Length)
            throw ChangedSize(file, source.Length);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            var remaining = file.Length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw ChangedSize(file, file.Length - remaining);

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IOException ChangedSize(FileSegment file, long actual) =>
        new($"\"{Path.GetFileName(file.Path)}\" changed while it was being sent " +
            $"(expected {file.Length:N0} bytes, found {actual:N0}). Send the request again.");
}

/// <summary>Streams a <see cref="WireBody"/> as HttpClient content; re-serializable, so redirects can resend it.</summary>
internal sealed class WireBodyContent(WireBody body) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        body.WriteToAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        body.WriteToAsync(stream, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = body.Length;
        return true;
    }
}

/// <summary>A body file problem found while preparing a request (no file chosen, missing, too large).</summary>
public sealed class BodyFileException(string message, Exception? inner = null) : IOException(message, inner);
