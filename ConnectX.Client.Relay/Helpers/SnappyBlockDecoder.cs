using System.Buffers;
using Hive.Common.Shared.Pooling;

namespace ConnectX.Client.Helpers;

/// <summary>
/// Allocation-free raw Snappy block decoding from segmented input. The existing
/// Snappier compressor remains unchanged. Format reference:
/// https://github.com/google/snappy/blob/main/format_description.txt
/// </summary>
public static class SnappyBlockDecoder
{
    public static void Decode(ReadOnlySequence<byte> input, PooledBufferStream output)
    {
        var reader = new SequenceReader<byte>(input);
        var length = ReadLength(ref reader);
        // The writer checks its limit before renting any storage.
        var destination = length == 0 ? Span<byte>.Empty : output.GetSpan(length)[..length];
        var written = 0;
        uint previousOffset = 0;
        var previousCopyLength = 0;
        while (written < length)
        {
            var tag = ReadByte(ref reader);
            var kind = tag & 3;
            if (kind == 0)
            {
                var code = tag >> 2;
                var count = code < 60 ? (uint)code + 1 : (ulong)ReadInteger(ref reader, code - 59) + 1;
                if (count > (uint)(length - written)) throw InvalidBlock();
                var literal = destination.Slice(written, (int)count);
                if (!reader.TryCopyTo(literal)) throw InvalidBlock();
                reader.Advance((int)count);
                written += (int)count;
                previousOffset = 0;
                continue;
            }

            var copyLength = kind == 1 ? 4 + ((tag >> 2) & 7) : 1 + (tag >> 2);
            var offset = kind == 1
                ? (uint)(((tag & 0xe0) << 3) | ReadByte(ref reader))
                : ReadInteger(ref reader, kind == 2 ? 2 : 4);
            if (offset == 0 || offset > (uint)written || copyLength > length - written)
                throw InvalidBlock();

            if (offset == 1)
            {
                destination.Slice(written, copyLength).Fill(destination[written - 1]);
                written += copyLength;
                previousOffset = offset;
                previousCopyLength = copyLength;
                continue;
            }

            // Consecutive copies at the same offset establish a periodic suffix
            // of offset + previousCopyLength bytes. Read an earlier whole period
            // to copy this tag in one operation, without reading unwritten bytes.
            var distance = ((copyLength + (long)offset - 1) / offset) * offset;
            if (previousOffset == offset && distance <= offset + (long)previousCopyLength && distance <= written)
            {
                destination.Slice(written - (int)distance, copyLength).CopyTo(destination.Slice(written, copyLength));
                written += copyLength;
                previousCopyLength = copyLength;
                continue;
            }

            // Expand overlapping backreferences by doubling the available prefix.
            // CopyTo alone has memmove semantics and cannot expand an RLE run.
            var from = written - (int)offset;
            var remaining = copyLength;
            var available = (int)offset;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, available);
                destination.Slice(from, count).CopyTo(destination.Slice(written, count));
                written += count;
                remaining -= count;
                available += count;
            }
            previousOffset = offset;
            previousCopyLength = copyLength;
        }
        if (!reader.End) throw InvalidBlock();
        output.Advance(length);
    }

    private static int ReadLength(ref SequenceReader<byte> reader)
    {
        uint length = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            var value = ReadByte(ref reader);
            if (shift == 28 && (value & 0xf0) != 0) throw InvalidBlock();
            length |= (uint)(value & 0x7f) << shift;
            if ((value & 0x80) == 0)
            {
                if (length > int.MaxValue) throw InvalidBlock();
                return (int)length;
            }
        }
        throw InvalidBlock();
    }

    private static uint ReadInteger(ref SequenceReader<byte> reader, int bytes)
    {
        // SequenceReader reads contiguous little-endian fields in one operation
        // and handles the fragmented fallback without an intermediate buffer.
        if (bytes == 1) return ReadByte(ref reader);
        if (bytes == 2)
            return reader.TryReadLittleEndian(out short value) ? unchecked((ushort)value) : throw InvalidBlock();
        if (bytes == 4)
            return reader.TryReadLittleEndian(out int value) ? unchecked((uint)value) : throw InvalidBlock();
        var low = ReadByte(ref reader);
        var middle = ReadByte(ref reader);
        return (uint)(low | (middle << 8) | (ReadByte(ref reader) << 16));
    }

    private static byte ReadByte(ref SequenceReader<byte> reader)
        => reader.TryRead(out var value) ? value : throw InvalidBlock();

    private static InvalidDataException InvalidBlock() => new("Invalid or truncated raw Snappy block.");
}
