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

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} FAILURES");
return failures;
