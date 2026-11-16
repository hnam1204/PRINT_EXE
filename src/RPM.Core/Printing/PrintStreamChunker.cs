using System.Runtime.CompilerServices;

namespace RPM.Core.Printing;

public sealed record PrintStreamChunk(long Sequence, long Offset, byte[] Data);

public static class PrintStreamChunker
{
    public static async IAsyncEnumerable<PrintStreamChunk> ReadAsync(
        Stream source,
        int chunkBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(source));
        if (chunkBytes is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(chunkBytes));

        long sequence = 0;
        long offset = 0;
        byte[] buffer = new byte[chunkBytes];
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) yield break;

            byte[] chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            yield return new PrintStreamChunk(sequence++, offset, chunk);
            offset += read;
        }
    }
}
