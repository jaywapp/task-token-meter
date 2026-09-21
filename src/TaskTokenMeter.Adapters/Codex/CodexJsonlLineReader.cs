using System.Buffers;

namespace TaskTokenMeter.Adapters.Codex;

/// <summary>
/// Streams a JSONL file as raw UTF-8 line spans.
/// </summary>
/// <remarks>
/// A rollout is read once per projection, so the per-line <see cref="string"/> that
/// <see cref="File.ReadLines(string)"/> produces dominated the read path: every byte was decoded to
/// UTF-16 and then transcoded back to UTF-8 by the JSON parser. Handing the caller the original
/// bytes removes both conversions. Line breaks follow <see cref="StreamReader.ReadLine"/> exactly
/// (LF, CR and CRLF all terminate a line) so the set of lines is unchanged.
/// </remarks>
internal sealed class CodexJsonlLineReader : IDisposable
{
    private const int BufferSize = 1 << 20;

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private readonly FileStream stream;
    private byte[] buffer;
    private int dataStart;
    private int dataEnd;
    private int lineStart;
    private int lineLength;
    private bool endOfFile;
    private bool byteOrderMarkChecked;

    public CodexJsonlLineReader(string path)
    {
        // FileShare.Read mirrors the sharing mode File.ReadLines opens the source with.
        stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    public ReadOnlySpan<byte> Current => buffer.AsSpan(lineStart, lineLength);

    public bool MoveNext()
    {
        while (true)
        {
            var available = buffer.AsSpan(dataStart, dataEnd - dataStart);
            var breakIndex = available.IndexOfAny((byte)'\n', (byte)'\r');
            if (breakIndex >= 0)
            {
                if (available[breakIndex] == (byte)'\n')
                {
                    Emit(breakIndex, breakIndex + 1);
                    return true;
                }

                // A carriage return only terminates the line once the next byte is known, because
                // CRLF is a single break while a lone CR is a break of its own.
                if (breakIndex + 1 < available.Length)
                {
                    Emit(breakIndex, available[breakIndex + 1] == (byte)'\n' ? breakIndex + 2 : breakIndex + 1);
                    return true;
                }

                if (endOfFile)
                {
                    Emit(breakIndex, breakIndex + 1);
                    return true;
                }
            }
            else if (endOfFile)
            {
                if (available.IsEmpty)
                {
                    return false;
                }

                Emit(available.Length, available.Length);
                return true;
            }

            Fill();
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(buffer);
        stream.Dispose();
    }

    private void Emit(int length, int consumed)
    {
        lineStart = dataStart;
        lineLength = length;
        dataStart += consumed;
    }

    private void Fill()
    {
        if (dataStart > 0)
        {
            Buffer.BlockCopy(buffer, dataStart, buffer, 0, dataEnd - dataStart);
            dataEnd -= dataStart;
            dataStart = 0;
        }

        if (dataEnd == buffer.Length)
        {
            var grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
            Buffer.BlockCopy(buffer, 0, grown, 0, dataEnd);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = grown;
        }

        var read = stream.Read(buffer, dataEnd, buffer.Length - dataEnd);
        if (read == 0)
        {
            endOfFile = true;
        }
        else
        {
            dataEnd += read;
        }

        if (byteOrderMarkChecked || (dataEnd < Utf8ByteOrderMark.Length && !endOfFile))
        {
            return;
        }

        byteOrderMarkChecked = true;
        if (buffer.AsSpan(0, dataEnd).StartsWith(Utf8ByteOrderMark))
        {
            dataStart = Utf8ByteOrderMark.Length;
        }
    }
}
