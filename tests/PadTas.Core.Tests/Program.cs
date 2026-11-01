using PadTas.Core;

// 簡易テストランナー (xUnit 等を入れずに Core の要だけを検証する)
var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {name}");
    if (!ok) failures++;
}

// 1) ロスレス記録: スティック 1 LSB の変化も拾う (epsilon=0)
{
    var rec = new RecorderEngine(analogEpsilon: 0);
    var t0 = QpcClock.Now;
    rec.Start(new Recording { PollingRateHz = 1000 });
    rec.Feed(t0 + 100, new GamepadState(0, 100, 0, 0, 0, 0, 0), force: true);
    rec.Feed(t0 + 200, new GamepadState(0, 100, 0, 0, 0, 0, 0));       // 変化なし → 捨てる
    rec.Feed(t0 + 300, new GamepadState(0, 101, 0, 0, 0, 0, 0));       // 1 LSB 変化 → 拾う
    rec.Feed(t0 + 400, new GamepadState((ushort)GamepadButtons.A, 101, 0, 0, 0, 255, 0));
    var r = rec.Stop();
    Check("変化フレームのみ記録 (先頭+3)", r.Frames.Count == 4);
    Check("1 LSB の変化を保持", r.Frames[2].State.LeftX == 101);
    Check("トリガー生値 255 を保持", r.Frames[3].State.LeftTrigger == 255);
}

// 2) .padtas 保存/読込のラウンドトリップが完全一致
var tmp = Path.Combine(Path.GetTempPath(), "padtas-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
Recording segA, segB;
{
    segA = new Recording { PollingRateHz = 1000, QpcFrequency = QpcClock.Frequency, DurationTicks = 10_000 };
    segA.Frames.Add(new InputFrame(0, GamepadState.Neutral));
    segA.Frames.Add(new InputFrame(5_000, new GamepadState((ushort)GamepadButtons.B, -32768, 32767, 123, -456, 7, 250)));
    var path = Path.Combine(tmp, "a.padtas");
    PadTasFile.Save(segA, path);
    var loaded = PadTasFile.Load(path);
    Check("保存/読込でフレーム一致", loaded.Frames.Count == 2 && loaded.Frames[1].State == segA.Frames[1].State);
    Check("保存/読込で時刻一致", loaded.Frames[1].TimestampTicks == 5_000 && loaded.DurationTicks == 10_000);
}

// 3) 続き記録: 2 セグメント連結で境界が途切れない
{
    segB = new Recording { PollingRateHz = 1000, QpcFrequency = QpcClock.Frequency, DurationTicks = 8_000 };
    segB.Frames.Add(new InputFrame(0, new GamepadState((ushort)GamepadButtons.X, 0, 0, 0, 0, 0, 0)));
    segB.Frames.Add(new InputFrame(8_000, GamepadState.Neutral));

    var project = new TasProject { Name = "test", FolderPath = tmp };
    project.AppendSegment(segA, "前半");
    project.AppendSegment(segB, "続き");
    Check("セグメント数 2", project.Segments.Count == 2);
    Check("合計時間 = 10000+8000", project.TotalDurationTicks == 18_000);

    var merged = project.MergeAll();
    Check("連結後のフレーム数 4", merged.Frames.Count == 4);
    Check("第2セグメント先頭が境界 10000 に配置", merged.Frames[2].TimestampTicks == 10_000);
    Check("連結後の終了状態が Neutral", project.StateAtEnd() == GamepadState.Neutral);

    var reloaded = TasProject.Load(tmp);
    Check("project.json ラウンドトリップ", reloaded.Segments.Count == 2 && reloaded.Segments[1].Name == "続き");
}

// 3.5) セグメント削除: ファイルも消え、マニフェストにも残らない
{
    var folder = Path.Combine(tmp, "del-test");
    var project = new TasProject { Name = "del", FolderPath = folder };
    var s1 = project.AppendSegment(segA, "消す方");
    var s2 = project.AppendSegment(segB, "残す方");
    var file1 = Path.Combine(folder, s1.FileName);
    Check("削除前にファイルあり", File.Exists(file1));
    project.RemoveSegment(s1);
    Check("選択削除で 1 件に", project.Segments.Count == 1 && project.Segments[0].Name == "残す方");
    Check("削除したファイルが消えた", !File.Exists(file1));
    Check("残したファイルはある", File.Exists(Path.Combine(folder, s2.FileName)));
    Check("再読込でも 1 件", TasProject.Load(folder).Segments.Count == 1);
    project.RemoveAllSegments();
    Check("全削除で 0 件＆ファイルなし", project.Segments.Count == 0 && !File.Exists(Path.Combine(folder, s2.FileName)));
}

// 4) 再生スケジュールは絶対時刻 (ドリフトしない)
{
    var rec = new Recording();
    rec.Frames.Add(new InputFrame(123_456, GamepadState.Neutral));
    Check("期限 = 開始 + 相対時刻", PlaybackPlanner.DueTimestamp(rec, 1_000_000, 0) == 1_123_456);
}

// 5) 同時入力: 複数ボタン+両スティック+両トリガーが 1 フレームに丸ごと残り、
//    保存・読込・連結を経ても欠けたり分解されたりしない (スナップショット方式の保証)
{
    var allButtons = (ushort)(
        GamepadButtons.A | GamepadButtons.B | GamepadButtons.X | GamepadButtons.Y |
        GamepadButtons.Start | GamepadButtons.Back |
        GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder |
        GamepadButtons.LeftThumb | GamepadButtons.RightThumb |
        GamepadButtons.DPadUp | GamepadButtons.DPadDown |
        GamepadButtons.DPadLeft | GamepadButtons.DPadRight);
    var combo = new GamepadState(allButtons, -32768, 32767, 12345, -23456, 255, 128);

    // 5a) 1回のサンプルで同時押しがそのまま 1 フレームになる
    var engine = new RecorderEngine(analogEpsilon: 0);
    var t0 = QpcClock.Now;
    engine.Start(new Recording());
    engine.Feed(t0 + 1000, combo, force: true);
    var rec = engine.Stop();
    Check("同時入力が 1 フレームに全て入る", rec.Frames.Count == 2 && rec.Frames[1].State == combo);

    // 5b) 時間差で押した場合も「押している状態の合成」が残る (A を保持したまま B を追加)
    var engine2 = new RecorderEngine(analogEpsilon: 0);
    engine2.Start(new Recording());
    var onlyA = new GamepadState((ushort)GamepadButtons.A, 0, 0, 0, 0, 0, 0);
    var aAndB = new GamepadState((ushort)(GamepadButtons.A | GamepadButtons.B), 0, 0, 0, 0, 0, 0);
    engine2.Feed(t0 + 100, onlyA, force: true);
    engine2.Feed(t0 + 200, aAndB);
    engine2.Feed(t0 + 300, onlyA); // B だけ離す
    var rec2 = engine2.Stop();
    Check("保持中の A に B が重なった状態を記録", rec2.Frames[2].State == aAndB);
    Check("B を離すと A のみに戻る", rec2.Frames[3].State == onlyA);

    // 5c) 同時入力の保存/読込ラウンドトリップ
    var path = Path.Combine(tmp, "combo.padtas");
    var comboRec = new Recording { DurationTicks = 5000 };
    comboRec.Frames.Add(new InputFrame(0, GamepadState.Neutral));
    comboRec.Frames.Add(new InputFrame(2500, combo));
    PadTasFile.Save(comboRec, path);
    var loadedCombo = PadTasFile.Load(path);
    Check("同時入力が保存/読込で完全一致", loadedCombo.Frames[1].State == combo);

    // 5d) セグメント境界をまたいでも同時入力が欠けない
    var folder = Path.Combine(tmp, "combo-seg");
    var project = new TasProject { Name = "combo", FolderPath = folder };
    var seg1 = new Recording { DurationTicks = 4000 };
    seg1.Frames.Add(new InputFrame(0, combo));
    var seg2 = new Recording { DurationTicks = 4000 };
    seg2.Frames.Add(new InputFrame(0, combo));
    seg2.Frames.Add(new InputFrame(3999, GamepadState.Neutral));
    project.AppendSegment(seg1, "同時入力1");
    project.AppendSegment(seg2, "同時入力2");
    var merged = project.MergeAll();
    Check("連結後も同時入力が両セグメントに残る",
        merged.Frames[0].State == combo && merged.Frames[1].State == combo &&
        merged.Frames[1].TimestampTicks == 4000);
}

// 6) 大きい記録: バッファ境界・大きな差分・負の差分をまたいでも保存/読込と連結が一致する
{
    var big = new Recording
    {
        ControllerName = "テスト🎮",
        PollingRateHz = 8000,
        QpcFrequency = 10_000_000,
        CreatedAt = new DateTimeOffset(2024, 5, 6, 7, 8, 9, 123, TimeSpan.FromHours(9)),
        DurationTicks = 50_000_000,
    };
    long t = 0;
    big.Frames.Add(new InputFrame(0, GamepadState.Neutral));
    for (var i = 1; i <= 5000; i++)
    {
        var delta = i % 97 == 0 ? 1L << (i % 40) : i * 3L;
        t += delta;
        big.Frames.Add(new InputFrame(t, new GamepadState(
            (ushort)(i * 17), (short)(i - 2500), (short)-i, (short)(i % 32767), (short)(-i % 32767),
            (byte)(i % 256), (byte)((i * 3) % 256))));
    }
    big.Frames.Add(new InputFrame(t - 10, new GamepadState(1, -32768, 32767, 0, 0, 255, 0)));

    var path = Path.Combine(tmp, "big.padtas");
    PadTasFile.Save(big, path);
    var loaded = PadTasFile.Load(path);
    Check("大容量のフレーム数", loaded.Frames.Count == big.Frames.Count);
    Check("大容量の時刻", loaded.Frames[^1].TimestampTicks == big.Frames[^1].TimestampTicks
        && loaded.Frames[100].TimestampTicks == big.Frames[100].TimestampTicks
        && loaded.Frames[4000].TimestampTicks == big.Frames[4000].TimestampTicks);
    Check("大容量の状態", loaded.Frames[1234].State == big.Frames[1234].State
        && loaded.Frames[^1].State == big.Frames[^1].State);
    Check("ヘッダー保持", loaded.ControllerName == big.ControllerName
        && loaded.PollingRateHz == 8000 && loaded.QpcFrequency == 10_000_000
        && loaded.DurationTicks == big.DurationTicks
        && loaded.CreatedAt == big.CreatedAt);

    var folder = Path.Combine(tmp, "merge-many");
    var project = new TasProject { Name = "m", FolderPath = folder };
    project.AppendSegment(big, "a");
    project.AppendSegment(segA, "b");
    project.AppendSegment(big, "c");
    var merged = project.MergeAll();
    Check("多数連結のフレーム数", merged.Frames.Count == big.Frames.Count * 2 + segA.Frames.Count);
    Check("多数連結の先頭メタデータ", merged.ControllerName == big.ControllerName && merged.QpcFrequency == big.QpcFrequency);
    Check("多数連結のオフセット",
        merged.Frames[big.Frames.Count].TimestampTicks == big.EffectiveDurationTicks + segA.Frames[0].TimestampTicks);
    Check("多数連結の終端",
        merged.DurationTicks == big.EffectiveDurationTicks + segA.EffectiveDurationTicks + big.EffectiveDurationTicks);
    Check("多数連結の状態",
        merged.Frames[0].State == big.Frames[0].State
        && merged.Frames[big.Frames.Count + segA.Frames.Count].State == big.Frames[0].State);
}

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} FAILURES");
return failures;
