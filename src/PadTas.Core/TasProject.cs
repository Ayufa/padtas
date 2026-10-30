using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadTas.Core;

/// <summary>
/// TAS プロジェクト = セグメント (テイク) の並び。
///
/// 「続きからレコード」への対応:
/// - 各セグメントは独立した .padtas ファイルとして project フォルダに保存される。
///   記録中にアプリが落ちても、終わったセグメントは残る。
/// - 再生は全セグメントを 1 本のタイムラインに連結して途切れなく行う。
/// - 途中から録り直す場合は、その位置で分割して後ろを別ブランチとして残せる
///   (元のテイクを消さない = TAS の分岐リレコード)。
/// </summary>
public sealed class TasProject
{
    public string Name { get; set; } = "新規プロジェクト";
    public List<TasSegment> Segments { get; set; } = new();

    [JsonIgnore]
    public string? FolderPath { get; set; }

    public const string ManifestFileName = "project.json";

    // ---------- 保存 / 読込 ----------

    public void SaveManifest()
    {
        if (FolderPath is null) throw new InvalidOperationException("FolderPath が未設定です。");
        Directory.CreateDirectory(FolderPath);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(FolderPath, ManifestFileName), json);
    }

    public static TasProject Load(string folderPath)
    {
        var json = File.ReadAllText(Path.Combine(folderPath, ManifestFileName));
        var project = JsonSerializer.Deserialize<TasProject>(json)
                      ?? throw new InvalidDataException("project.json を読めませんでした。");
        project.FolderPath = folderPath;
        return project;
    }

    // ---------- セグメント操作 ----------

    /// <summary>記録済みセグメントを末尾に追加する (= 続きからレコードした結果)。</summary>
    public TasSegment AppendSegment(Recording recording, string name)
    {
        if (FolderPath is null) throw new InvalidOperationException("FolderPath が未設定です。");
        Directory.CreateDirectory(FolderPath);
        var segment = new TasSegment
        {
            Name = name,
            FileName = $"seg-{Segments.Count + 1:D3}.padtas",
            DurationTicks = recording.EffectiveDurationTicks,
            QpcFrequency = recording.QpcFrequency,
        };
        var suffix = 1;
        while (File.Exists(Path.Combine(FolderPath, segment.FileName)))
            segment.FileName = $"seg-{Segments.Count + 1:D3}-{suffix++}.padtas";

        PadTasFile.Save(recording, Path.Combine(FolderPath, segment.FileName));
        Segments.Add(segment);
        SaveManifest();
        return segment;
    }

    public Recording LoadSegment(TasSegment segment)
    {
        if (FolderPath is null) throw new InvalidOperationException("FolderPath が未設定です。");
        return PadTasFile.Load(Path.Combine(FolderPath, segment.FileName));
    }

    /// <summary>全セグメントの合計時間 (QPC tick は最初のセグメントの周波数に統一する前提)。</summary>
    public long TotalDurationTicks
    {
        get
        {
            long total = 0;
            foreach (var s in Segments) total += s.DurationTicks;
            return total;
        }
    }

    /// <summary>
    /// 全セグメントを 1 本の Recording に連結する。
    /// セグメント境界では、直前セグメントの最終状態から次セグメントの先頭状態へ
    /// 切れ目なくつながる (次セグメント先頭フレームは境界時刻に置く)。
    /// </summary>
    public Recording MergeAll()
    {
        var merged = new Recording();
        long offset = 0;
        foreach (var segment in Segments)
        {
            var rec = LoadSegment(segment);
            if (merged.Frames.Count == 0)
            {
                merged.ControllerName = rec.ControllerName;
                merged.PollingRateHz = rec.PollingRateHz;
                merged.QpcFrequency = rec.QpcFrequency;
            }
            foreach (var frame in rec.Frames)
                merged.Frames.Add(new InputFrame(offset + frame.TimestampTicks, frame.State));
            offset += rec.EffectiveDurationTicks;
        }
        merged.DurationTicks = offset;
        return merged;
    }

    /// <summary>
    /// 続き記録の開始点における状態。全セグメント終了時点の最終状態を返す。
    /// 再生しながらこの状態まで進めてから実コントローラーを引き継ぐ、などの使い方をする。
    /// </summary>
    public GamepadState StateAtEnd()
    {
        if (Segments.Count == 0) return GamepadState.Neutral;
        var last = LoadSegment(Segments[^1]);
        return last.Frames.Count > 0 ? last.Frames[^1].State : GamepadState.Neutral;
    }
}

public sealed class TasSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public long DurationTicks { get; set; }
    public long QpcFrequency { get; set; } = QpcClock.Frequency;
    /// <summary>
    /// 任意: このセグメント開始時にエミュレータ等のクイックロードを叩くための
    /// ホットキー名 (例 "F1")。ゲーム側セーブステートと入力記録を同期させるための連携用。
    /// </summary>
    public string? LoadStateHotkey { get; set; }
    public string? Note { get; set; }

    [JsonIgnore]
    public double DurationSeconds => QpcFrequency > 0 ? (double)DurationTicks / QpcFrequency : 0;
}
