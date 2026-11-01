using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace PadTas.Core;

/// <summary>
/// .padtas ファイル形式 (バイナリ)。
///
/// 設計方針:
/// - 時刻は QPC tick の「差分」を 7bit 可変長整数で保存 → 長時間記録でも小さい
/// - アナログ値は生の int16 / byte のまま → ロスレス
/// - ヘッダーは JSON → 後から項目を増やしても互換を保ちやすい
///
/// レイアウト:
///   magic "PADTAS" (6 byte) | formatVersion uint16 | headerLen int32 | header JSON
///   | frameCount int32 | frame*
///   frame: deltaTicks (7bit encoded int64) | buttons uint16
///          | leftX,leftY,rightX,rightY int16 x4 | leftTrigger,rightTrigger byte x2  = 12 byte + 可変長
///
/// 7bit 整数は <see cref="System.IO.BinaryWriter.Write7BitEncodedInt64"/> と同じ符号化。
/// </summary>
public static class PadTasFile
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PADTAS");
    public const ushort CurrentFormatVersion = 1;
    private const int StateBytes = 12;
    private const int MaxFrameBytes = 10 + StateBytes; // 7bit int64 は最大 10 バイト

    private sealed class Header
    {
        public string ControllerName { get; set; } = "";
        public int PollingRateHz { get; set; }
        public long QpcFrequency { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public long DurationTicks { get; set; }
    }

    internal readonly struct SegmentInfo
    {
        public string ControllerName { get; init; }
        public int PollingRateHz { get; init; }
        public long QpcFrequency { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public long DurationTicks { get; init; }
        public int FramesRead { get; init; }
        public long LastFrameTimestamp { get; init; }

        public long EffectiveDurationTicks =>
            DurationTicks > 0 ? DurationTicks
            : FramesRead > 0 ? LastFrameTimestamp
            : 0;
    }

    public static void Save(Recording recording, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.SequentialScan);
        using var w = new BinaryWriter(fs);
        w.Write(Magic);
        w.Write(CurrentFormatVersion);

        var header = new Header
        {
            ControllerName = recording.ControllerName,
            PollingRateHz = recording.PollingRateHz,
            QpcFrequency = recording.QpcFrequency,
            CreatedAt = recording.CreatedAt,
            DurationTicks = recording.EffectiveDurationTicks,
        };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
        w.Write(headerBytes.Length);
        w.Write(headerBytes);

        w.Write(recording.Frames.Count);
        WriteFrameBodies(fs, recording.Frames);
    }

    public static Recording Load(string path)
    {
        var recording = new Recording();
        var info = ReadInto(path, recording.Frames, 0);
        recording.ControllerName = info.ControllerName;
        recording.PollingRateHz = info.PollingRateHz;
        recording.QpcFrequency = info.QpcFrequency;
        recording.CreatedAt = info.CreatedAt;
        recording.DurationTicks = info.DurationTicks;
        return recording;
    }

    /// <summary>
    /// フレームを <paramref name="destination"/> の末尾へ、時刻に <paramref name="timestampOffset"/> を足して追加する。
    /// 呼び出し元のリストは失敗時に途中まで伸びることがある (例外は従来の BinaryReader と同じ種類)。
    /// </summary>
    internal static SegmentInfo ReadInto(string path, List<InputFrame> destination, long timestampOffset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var length = fs.Length;
        if (length == 0)
            throw new InvalidDataException("PADTAS ファイルではありません。");
        if (length > int.MaxValue)
            throw new InvalidDataException("PADTAS ファイルが大きすぎます。");

        var byteLength = (int)length;
        var rented = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            var read = 0;
            while (read < byteLength)
            {
                var n = fs.Read(rented, read, byteLength - read);
                if (n == 0) throw new EndOfStreamException();
                read += n;
            }
            return Parse(rented.AsSpan(0, byteLength), destination, timestampOffset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteFrameBodies(Stream stream, List<InputFrame> frames)
    {
        if (frames.Count == 0) return;
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var buffer = rented.AsSpan();
            var pos = 0;
            long previous = 0;
            for (var i = 0; i < frames.Count; i++)
            {
                if (pos + MaxFrameBytes > buffer.Length)
                {
                    stream.Write(rented, 0, pos);
                    pos = 0;
                }
                var frame = frames[i];
                var delta = frame.TimestampTicks - previous;
                previous = frame.TimestampTicks;
                pos += Write7Bit(buffer[pos..], delta);
                WriteState(buffer[pos..], frame.State);
                pos += StateBytes;
            }
            if (pos > 0) stream.Write(rented, 0, pos);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static SegmentInfo Parse(ReadOnlySpan<byte> data, List<InputFrame> destination, long timestampOffset)
    {
        if (data.Length < Magic.Length || !data[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("PADTAS ファイルではありません。");

        var pos = Magic.Length;
        if (pos + 2 > data.Length) throw new EndOfStreamException();
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        pos += 2;
        if (version > CurrentFormatVersion)
            throw new InvalidDataException($"未対応の形式バージョンです: {version}");

        if (pos + 4 > data.Length) throw new EndOfStreamException();
        var headerLen = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        if (headerLen < 0) throw new ArgumentOutOfRangeException(nameof(headerLen));

        var available = data.Length - pos;
        var toRead = headerLen <= available ? headerLen : available;
        var header = JsonSerializer.Deserialize<Header>(data.Slice(pos, toRead))
                     ?? throw new InvalidDataException("ヘッダーを読めませんでした。");
        pos += toRead;

        if (pos + 4 > data.Length) throw new EndOfStreamException();
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;

        if (count > 0)
            destination.EnsureCapacity(destination.Count + count);

        long ticks = 0;
        var last = 0L;
        var framesRead = 0;
        for (var i = 0; i < count; i++)
        {
            ticks += Read7Bit(data, ref pos);
            last = ticks;
            destination.Add(new InputFrame(timestampOffset + ticks, ReadState(data, ref pos)));
            framesRead++;
        }

        return new SegmentInfo
        {
            ControllerName = header.ControllerName,
            PollingRateHz = header.PollingRateHz,
            QpcFrequency = header.QpcFrequency,
            CreatedAt = header.CreatedAt,
            DurationTicks = header.DurationTicks,
            FramesRead = framesRead,
            LastFrameTimestamp = last,
        };
    }

    private static int Write7Bit(Span<byte> dest, long value)
    {
        var u = (ulong)value;
        var i = 0;
        while (u > 0x7Fu)
        {
            dest[i++] = (byte)(u | 0x80);
            u >>= 7;
        }
        dest[i++] = (byte)u;
        return i;
    }

    private static long Read7Bit(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 63; shift += 7)
        {
            if ((uint)pos >= (uint)data.Length) throw new EndOfStreamException();
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b <= 0x7F) return (long)result;
        }

        if ((uint)pos >= (uint)data.Length) throw new EndOfStreamException();
        var last = data[pos++];
        if (last > 0b_1)
            throw new FormatException("Too many bytes in what should have been a 7-bit encoded integer.");
        result |= (ulong)last << 63;
        return (long)result;
    }

    private static void WriteState(Span<byte> dest, GamepadState state)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest, state.Buttons);
        BinaryPrimitives.WriteInt16LittleEndian(dest[2..], state.LeftX);
        BinaryPrimitives.WriteInt16LittleEndian(dest[4..], state.LeftY);
        BinaryPrimitives.WriteInt16LittleEndian(dest[6..], state.RightX);
        BinaryPrimitives.WriteInt16LittleEndian(dest[8..], state.RightY);
        dest[10] = state.LeftTrigger;
        dest[11] = state.RightTrigger;
    }

    private static GamepadState ReadState(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + StateBytes > data.Length) throw new EndOfStreamException();
        var state = new GamepadState(
            BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[(pos + 2)..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[(pos + 4)..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[(pos + 6)..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[(pos + 8)..]),
            data[pos + 10],
            data[pos + 11]);
        pos += StateBytes;
        return state;
    }
}
