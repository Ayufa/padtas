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
/// </summary>
public static class PadTasFile
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PADTAS");
    public const ushort CurrentFormatVersion = 1;

    private sealed class Header
    {
        public string ControllerName { get; set; } = "";
        public int PollingRateHz { get; set; }
        public long QpcFrequency { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public long DurationTicks { get; set; }
    }

    public static void Save(Recording recording, string path)
    {
        using var fs = File.Create(path);
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
        long previous = 0;
        foreach (var frame in recording.Frames)
        {
            w.Write7BitEncodedInt64(frame.TimestampTicks - previous);
            previous = frame.TimestampTicks;
            w.Write(frame.State.Buttons);
            w.Write(frame.State.LeftX);
            w.Write(frame.State.LeftY);
            w.Write(frame.State.RightX);
            w.Write(frame.State.RightY);
            w.Write(frame.State.LeftTrigger);
            w.Write(frame.State.RightTrigger);
        }
    }

    public static Recording Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);
        var magic = r.ReadBytes(Magic.Length);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("PADTAS ファイルではありません。");
        var version = r.ReadUInt16();
        if (version > CurrentFormatVersion)
            throw new InvalidDataException($"未対応の形式バージョンです: {version}");

        var headerLen = r.ReadInt32();
        var header = JsonSerializer.Deserialize<Header>(r.ReadBytes(headerLen))
                     ?? throw new InvalidDataException("ヘッダーを読めませんでした。");
        var recording = new Recording
        {
            ControllerName = header.ControllerName,
            PollingRateHz = header.PollingRateHz,
            QpcFrequency = header.QpcFrequency,
            CreatedAt = header.CreatedAt,
            DurationTicks = header.DurationTicks,
        };

        var count = r.ReadInt32();
        long ticks = 0;
        for (var i = 0; i < count; i++)
        {
            ticks += r.Read7BitEncodedInt64();
            var state = new GamepadState(
                r.ReadUInt16(), r.ReadInt16(), r.ReadInt16(), r.ReadInt16(), r.ReadInt16(),
                r.ReadByte(), r.ReadByte());
            recording.Frames.Add(new InputFrame(ticks, state));
        }
        return recording;
    }
}
