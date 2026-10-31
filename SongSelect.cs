using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

sealed class BeatmapSet(List<DbBeatmap> diffs)
{
    public readonly List<DbBeatmap> Diffs = diffs;
    public DbBeatmap First => Diffs[0];
}

sealed class SongSelect : Screen
{
    Task<List<BeatmapSet>>? loading;
    List<BeatmapSet> sets = [];
    readonly List<(BeatmapSet Set, List<DbBeatmap> Diffs)> view = []; // after search filter
    string filter = "", message = "";
    int setIndex, diffIndex;
    float scrollRow;
    Mods mods;
    bool modSelect;

    // Preview music: starts once the selection has rested briefly, like stable.
    Track? preview;
    (string Folder, string Audio, double Rate, bool Nightcore)? previewKey; // same song = no restart across difficulties
    double selectionChangedAt;
    bool themeFromStart;

    // Layout in stable's 768px-high units.
    static float S => GetScreenHeight() / 768f;
    static float RowH => 76.8f * S; // stable's panel spacing: 144px at 1440p (measured on p057/p059)
    static float TopH => 100 * S;
    static float BottomH => 80 * S;

    // Gameplay's sprites decode in the background while a map is picked (and upload a frame at a time in Update).
    public override void Entered() => Game.Skin.PrefetchGameplay();

    public SongSelect(string filter = "", Mods mods = Mods.None)
    {
        this.filter = filter;
        this.mods = mods;
        loading = Task.Run(LoadLibrary);
        scoresLoading = Task.Run(Replays.Index);
    }

    // Local leaderboard (saved replays), keyed by beatmap MD5.
    Task<Dictionary<string, List<ReplayInfo>>>? scoresLoading;
    Dictionary<string, List<ReplayInfo>> scores = [];
    static readonly List<ReplayInfo> NoScores = []; // read-only: returned when a map has no scores
    List<ReplayInfo> SelectedScores => Selected is { } b && scores.TryGetValue(b.Md5, out var l) ? l : NoScores;
    // osu!.db ranked status (4 ranked, 5 approved, 6 qualified, 7 loved, ...); null until the library has loaded.
    internal byte? StatusOf(string md5)
    {
        if (Selected is { } sel && sel.Md5 == md5) return sel.Status; // the usual case; the scan below is ~16ms
        var lib = loading == null ? sets : loading.IsCompleted ? loading.Result : null; // no side effects: Ambient() adopts it
        return lib?.SelectMany(s => s.Diffs).FirstOrDefault(d => d.Md5 == md5)?.Status ?? (lib == null ? null : (byte)0);
    }

    internal List<ReplayInfo> ScoresFor(string md5)
    {
        PollScores();
        return scores.TryGetValue(md5, out var l) ? l : [];
    }

    // The local leaderboard loads in the background; picked up whenever it's finished.
    void PollScores()
    {
        if (scoresLoading is not { IsCompleted: true }) return;
        foreach (var (md5, list) in scoresLoading.Result)
            foreach (var info in list) AddScore(info);
        scoresLoading = null;
    }

    public void AddScore(ReplayInfo info)
    {
        if (!scores.TryGetValue(info.BeatmapMd5, out var list)) scores[info.BeatmapMd5] = list = [];
        list.Add(info);
        list.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    void Watch(ReplayInfo info)
    {
        if (Selected is not { } b) return;
        try
        {
            var map = Beatmap.Load(b.FilePath, mods: info.Mods);
            StopPreview();
            Game.Screen = new Player(map, this, info.Mods, Replays.Frames(info), info);
        }
        catch (Exception e) when (e is not OutOfMemoryException) // a malformed map or replay mustn't take the game down
        {
            message = $"Can't play replay: {e.Message}";
        }
    }

    static List<BeatmapSet> LoadLibrary()
    {
        List<DbBeatmap> all;
        try { all = OsuDb.Load(Path.Combine(Config.OsuDir, "osu!.db")); }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            all = ScanFolders(); // TODO: slow fallback (minutes for big libraries) if osu!.db is missing/unreadable
        }
        TotalBeatmaps = all.Count;
        for (int i = 0; i < all.Count; i++) all[i].BuildSearchKey(i > 0 ? all[i - 1] : null); // here, not on the first keystroke
        return Sorted(all.Where(b => b.Mode == 0)
            .GroupBy(b => b.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BeatmapSet(g.OrderBy(b => b.StarsFor(Mods.None)).ToList())), SortMode);
    }

    // stable's sort modes (TreeSortMode2 in its cfg; ours is saved to remake.cfg).
    static readonly (string Key, string Label)[] SortModes =
        [("Artist", "By Artist"), ("Bpm", "By BPM"), ("Creator", "By Creator"), ("Date", "By Date Added"), ("Difficulty", "By Difficulty"), ("Length", "By Length"), ("Title", "By Title")];
    static string SortMode => Config.Get("TreeSortMode2", "Artist");
    internal static int TotalBeatmaps; // every mode, as stable's menu counts them

    static List<BeatmapSet> Sorted(IEnumerable<BeatmapSet> sets, string mode)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        return (mode switch
        {
            "Title" => sets.OrderBy(s => s.First.Title, cmp),
            "Creator" => sets.OrderBy(s => s.First.Creator, cmp),
            "Bpm" => sets.OrderBy(s => s.First.MainBpm),
            "Length" => sets.OrderBy(s => s.Diffs.Max(d => d.TotalMs)),
            "Difficulty" => sets.OrderBy(s => s.First.StarsFor(Mods.None)),
            "Date" => sets.OrderBy(s => s.Diffs.Max(d => d.LastModified)),
            _ => sets.OrderBy(s => s.First.Artist, cmp),
        }).ThenBy(s => s.First.Artist, cmp).ThenBy(s => s.First.Title, cmp).ToList();
    }

    void CycleSort(int dir)
    {
        int i = Array.FindIndex(SortModes, m => m.Key.Equals(SortMode, StringComparison.OrdinalIgnoreCase));
        Config.SetOwn("TreeSortMode2", SortModes[((i < 0 ? 0 : i) + dir + SortModes.Length) % SortModes.Length].Key);
        sets = Sorted(sets, SortMode);
        ApplyFilter();
        Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
    }

    internal static List<DbBeatmap> ScanFolders()
    {
        var list = new List<DbBeatmap>();
        var songs = Path.Combine(Config.OsuDir, "Songs");
        if (!Directory.Exists(songs)) return list;
        foreach (var dir in Directory.EnumerateDirectories(songs))
            foreach (var file in Directory.EnumerateFiles(dir, "*.osu"))
            {
                try
                {
                    var m = Beatmap.Load(file, metadataOnly: true);
                    list.Add(new DbBeatmap
                    {
                        Artist = m.Artist, Title = m.Title, Creator = m.Creator, Version = m.Version, AudioFile = m.AudioFilename,
                        OsuFile = Path.GetFileName(file), Folder = Path.GetFileName(dir), Mode = (byte)m.Mode, Md5 = Replays.Md5Of(file),
                        AR = (float)m.AR, CS = (float)m.CS, OD = (float)m.OD, HP = (float)m.HP, PreviewMs = (int)m.PreviewTime,
                    });
                }
                catch (IOException) { }
            }
        return list;
    }

    // ---------------------------------------------------------------- filtering

    // stable-style search: plain words must all appear; key<op>value terms filter numbers (stars ar cs od hp bpm length).
    bool Matches(DbBeatmap b, string[] words, List<(string Key, string Op, double Value)> terms)
    {
        foreach (var w in words)
            if (!b.SearchMatches(w)) return false;
        foreach (var (key, op, value) in terms)
        {
            double v = key switch
            {
                "stars" or "star" => b.StarsFor(mods), "ar" => b.AR, "cs" => b.CS, "od" => b.OD, "hp" => b.HP,
                "bpm" => b.MaxBpm, "length" => b.DrainSeconds, _ => double.NaN,
            };
            if (double.IsNaN(v)) continue;
            bool ok = op switch
            {
                "<" => v < value, ">" => v > value, "<=" => v <= value, ">=" => v >= value,
                _ => Math.Abs(v - value) < (key is "stars" or "star" ? 0.5 : 0.05),
            };
            if (!ok) return false;
        }
        return true;
    }

    // this difficulty's osu!.db entry (null before osu!.db has loaded)
    internal DbBeatmap? DbEntry(string md5)
    {
        foreach (var set in sets) foreach (var b in set.Diffs) if (b.Md5 == md5) return b;
        return null;
    }

    // stable's local offset for this difficulty (0 before osu!.db has loaded)
    internal short LocalOffsetOf(string md5) => DbEntry(md5)?.LocalOffset ?? 0;

    // stable's mod caption: the selected mods' names (in its order, "," between) in size 30 at (64.4, 397), 0.5 apart, white at
    // quarter alpha, under the panels
    static readonly (Mods Flag, string Name)[] CaptionOrder =
    [
        (Mods.Autoplay, "Autoplay"), (Mods.SpunOut, "SpunOut"), (Mods.Easy, "Easy"), (Mods.NoFail, "NoFail"),
        (Mods.Hidden, "Hidden"), (Mods.Nightcore, "Nightcore"), (Mods.DoubleTime, "DoubleTime"), (Mods.HalfTime, "HalfTime"),
        (Mods.HardRock, "HardRock"), (Mods.Relax, "Relax"), (Mods.Autopilot, "AutoPilot"), (Mods.Perfect, "Perfect"),
        (Mods.SuddenDeath, "SuddenDeath"), (Mods.Flashlight, "Flashlight"), (Mods.ScoreV2, "ScoreV2"),
    ];
    Mods captionMods = (Mods)(-1);
    string caption = "";

    void DrawModCaption()
    {
        if (mods != captionMods)
        {
            captionMods = mods;
            caption = string.Join(",", CaptionOrder.Where(c => mods.HasFlag(c.Flag)
                && !(c.Flag == Mods.DoubleTime && mods.HasFlag(Mods.Nightcore)) && !(c.Flag == Mods.SuddenDeath && mods.HasFlag(Mods.Perfect))).Select(c => c.Name));
        }
        if (caption.Length == 0) return;
        float u = GetScreenHeight() / 480f;
        Game.Text(caption, 64.4f * u, 397 * u, 30 * u, new Color(255, 255, 255, 64), 0.5f * u);
    }

    internal void ApplyFilter()
    {
        var keep = view.Count > 0 && setIndex < view.Count ? view[setIndex].Set : null;
        var all = new List<(BeatmapSet Set, List<DbBeatmap> Diffs)>();
        var words = new List<string>();
        var terms = new List<(string, string, double)>();
        foreach (var token in filter.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(token, @"^([a-z]+)(<=|>=|<|>|=)([\d.]+)$");
            if (m.Success && double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                terms.Add((m.Groups[1].Value, m.Groups[2].Value, v));
            else words.Add(token);
        }
        var wordArr = words.ToArray();
        // the options' "Display beatmaps from .. up to .. stars" (10 = no upper limit)
        double lo = Config.GetDouble("DisplayStarsMinimum", 0), hi = Config.GetDouble("DisplayStarsMaximum", 10);
        bool starRange = lo > 0 || hi < 10;
        bool InRange(DbBeatmap b) => !starRange || b.StarsFor(mods) is var st && st >= lo && (hi >= 10 || st <= hi);
        foreach (var set in sets)
        {
            var diffs = filter.Length == 0 && !starRange ? set.Diffs : set.Diffs.Where(b => InRange(b) && Matches(b, wordArr, terms)).ToList();
            if (diffs.Count > 0) all.Add((set, diffs));
        }
        BuildGroups(all);
        int g = keep == null ? -1 : groups.FindIndex(gr => gr.Entries.Any(e => e.Set == keep));
        groupIndex = g >= 0 ? g : Math.Clamp(groupIndex, 0, Math.Max(0, groups.Count - 1));
        view.Clear();
        if (groups.Count > 0) view.AddRange(groups[groupIndex].Entries);
        matchCount = view.Sum(v => v.Diffs.Count);
        int found = keep == null ? -1 : view.FindIndex(v => v.Set == keep);
        setIndex = found >= 0 ? found : 0;
        diffIndex = Math.Clamp(diffIndex, 0, Math.Max(0, (view.Count > 0 ? view[setIndex].Diffs.Count : 1) - 1));
        SelectionChanged();
    }

    DbBeatmap? Selected => view.Count == 0 ? null : view[setIndex].Diffs[diffIndex];
    readonly Stack<DbBeatmap> randomHistory = [];

    void SelectionChanged() => selectionChangedAt = Game.NowMs;

    void MoveSet(int delta)
    {
        if (view.Count == 0) return;
        if (Grouped && Math.Abs(delta) == 1 && (setIndex + delta < 0 && groupIndex > 0 || setIndex + delta >= view.Count && groupIndex < groups.Count - 1))
        {
            OpenGroup(groupIndex + delta, last: delta < 0);
            return;
        }
        int before = setIndex;
        setIndex = Math.Clamp(setIndex + delta, 0, view.Count - 1);
        diffIndex = 0;
        if (setIndex != before) Game.Skin.Sound("select-expand")?.Play(BeatmapSamples.EffectVolume);
        SelectionChanged();
    }

    void MoveDiff(int delta)
    {
        if (view.Count == 0) return;
        Game.Skin.Sound("select-difficulty")?.Play(BeatmapSamples.EffectVolume);
        diffIndex += delta;
        if (diffIndex < 0)
        {
            if (setIndex == 0) diffIndex = 0;
            else { setIndex--; diffIndex = view[setIndex].Diffs.Count - 1; }
        }
        else if (diffIndex >= view[setIndex].Diffs.Count)
        {
            if (setIndex == view.Count - 1) diffIndex = view[setIndex].Diffs.Count - 1;
            else { setIndex++; diffIndex = 0; }
        }
        SelectionChanged();
    }

    // ---------------------------------------------------------------- preview / play

    void UpdatePreview(bool menu)
    {
        var b = Selected;
        (string, string, double, bool)? key = b == null ? null : (b.Folder, b.AudioFile, mods.Rate(), mods.HasFlag(Mods.Nightcore));
        if (previewLoading is { IsCompleted: true } done)
        {
            previewLoading = null;
            var (forKey, track) = done.Result;
            if (forKey != previewKey || b == null) track?.Dispose(); // the selection moved on while it loaded
            else if (track != null)
            {
                preview = track;
                ApplyMusicVolume();
                preview.Seek(themeFromStart ? 0 : Math.Max(0, b.PreviewMs));
                themeFromStart = false;
                preview.Play();
            }
        }
        if (key != previewKey && Game.NowMs - selectionChangedAt > 150 && previewLoading == null)
        {
            StopPreview();
            previewKey = key;
            if (b == null) return;
            previewLoading = LoadPreview(key, Path.Combine(Config.OsuDir, "Songs", b.Folder, b.AudioFile), mods.Rate(), mods.HasFlag(Mods.Nightcore));
        }
        else if (preview != null && preview.Stopped && b != null)
        {
            if (menu) { MusicNext(); return; } // the main menu plays songs through, then another (stable)
            preview.Seek(Math.Max(0, b.PreviewMs));
            preview.Play();
        }
    }

    // Off the main thread: the prescan reads the whole file (100-170ms per song change measured). Its own method so
    // the closure is only made when a load starts.
    Task<((string, string, double, bool)?, Track?)>? previewLoading;
    static Task<((string, string, double, bool)?, Track?)> LoadPreview((string, string, double, bool)? key, string path, double rate, bool nightcore) =>
        Task.Run(() => (key, Track.Load(path, rate, nightcore)));

    // Beat source: timing points of the song that's playing, loaded off the main thread.
    string timingKey = "";
    Task<List<TimingPoint>>? timingLoading;
    List<TimingPoint> timing = [];

    void UpdateTiming()
    {
        if (timingLoading is { IsCompleted: true })
        {
            timing = timingLoading.Result;
            timingLoading = null;
        }
        if (Current is not { } b || timingLoading != null || b.FilePath == timingKey) return;
        timingKey = b.FilePath;
        timingLoading = LoadTiming(b.FilePath);
    }

    // Its own method so the closure is only made when a load starts (UpdateTiming runs every frame).
    static Task<List<TimingPoint>> LoadTiming(string file) => Task.Run(() =>
    {
        try { return Beatmap.Load(file).TimingPoints; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    });

    // Where the music is in its beat: index within the current red line, phase 0 (on the beat) .. 1.
    internal (double RedTime, long Index, double Phase, double Length, int Meter, bool Kiai)? Beat()
    {
        double ms = MusicMs;
        if (double.IsNaN(ms) || timing.Count == 0) return null;
        TimingPoint? red = null;
        bool kiai = false;
        foreach (var t in timing)
        {
            if (t.Time > ms) break;
            if (t.Uninherited) red = t;
            kiai = t.Kiai;
        }
        red ??= timing.FirstOrDefault(t => t.Uninherited);
        if (red == null) return null;
        double beats = (ms - red.Time) / red.BeatLength;
        long index = (long)Math.Floor(beats);
        return (red.Time, index, beats - index, red.BeatLength, red.Meter, kiai);
    }

    internal double BeatPhase() => Beat()?.Phase ?? double.NaN;

    // The main menu's music controls (stable's Now Playing buttons). Next takes the forward history when there is
    // one (after Previous), else a random song; Stop pauses at the start so nothing restarts it.
    readonly Stack<int> musicBack = [], musicForward = [];

    internal void MusicNext()
    {
        if (view.Count == 0) return;
        musicBack.Push(setIndex);
        setIndex = musicForward.Count > 0 ? Math.Min(musicForward.Pop(), view.Count - 1) : Random.Shared.Next(view.Count);
        diffIndex = 0;
        SelectionChanged();
    }

    internal void MusicPrevious()
    {
        if (musicBack.Count == 0 || view.Count == 0) return;
        musicForward.Push(setIndex);
        setIndex = Math.Min(musicBack.Pop(), view.Count - 1);
        diffIndex = 0;
        SelectionChanged();
    }

    internal void MusicPlay()
    {
        if (preview == null) return;
        if (!preview.Paused) preview.Seek(0);
        preview.Play();
    }

    // Returns true if it paused (false: it resumed).
    internal bool MusicPause()
    {
        if (preview == null) return false;
        if (preview.Paused) { preview.Play(); return false; }
        preview.Pause();
        return true;
    }

    internal void MusicStop()
    {
        preview?.Pause();
        preview?.Seek(0);
    }

    internal void MusicSeek(double fraction)
    {
        if (preview != null) preview.Seek(fraction * preview.LengthMs);
    }

    internal void ApplyMusicVolume()
    {
        if (preview != null) preview.Volume = Config.MusicVolume;
    }

    void StopPreview()
    {
        preview?.Dispose();
        preview = null;
        previewKey = null;
    }

    void Play(Mods mods)
    {
        if (Selected is not { } b) return;
        try
        {
            var map = Beatmap.Load(b.FilePath, mods: mods);
            if (map.Objects.Count == 0) { message = "Map has no hit objects"; return; }
            StopPreview();
            Game.Skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume);
            Game.Screen = new Player(map, this, mods);
        }
        catch (Exception e) when (e is not OutOfMemoryException) // a malformed map mustn't take the game down
        {
            message = $"Failed to load: {e.Message}";
        }
    }

    // ---------------------------------------------------------------- input

    static readonly KeyboardKey[] ModKeys =
    [
        KeyboardKey.Q, KeyboardKey.W, KeyboardKey.E, KeyboardKey.A, KeyboardKey.S, KeyboardKey.D,
        KeyboardKey.F, KeyboardKey.G, KeyboardKey.Z, KeyboardKey.X, KeyboardKey.C, KeyboardKey.V, KeyboardKey.B,
    ];
    static readonly string[] ModBindings = ["keyEasy", "keyNoFail", "keyHalfTime", "keyHardRock", "keySuddenDeath", "keyDoubleTime", "keyHidden", "keyFlashlight", "keyRelax", "keyAutopilot", "keySpunOut", "keyAuto", "keyScoreV2"];

    static bool Pressed(KeyboardKey k) => Input.IsKeyPressed(k) || Input.IsKeyPressedRepeat(k);

    // The main menu we return to on Esc/back (null: quit instead).
    internal Screen? Menu;
    internal DbBeatmap? Current => Selected;
    internal double MusicMs => preview?.PositionMs ?? double.NaN;
    internal Track? Music => preview;

    // Library/score loading, preview music and background. The main menu runs this too, so its music and
    // background are song select's. Returns false while the library is still loading.
    internal bool Ambient(bool menu = false)
    {
        if (loading != null)
        {
            if (!loading.IsCompleted) return false;
            sets = loading.Result;
            loading = null;
            ApplyFilter();
            if (filter.Length == 0 && view.Count > 0)
            {
                // stable's menu theme (MenuMusic): circles! from the start when it's in the library, else a random song.
                int theme = Menu != null && Config.GetBool("MenuMusic", true) ? view.FindIndex(v => v.Set.First.Artist == "nekodex" && v.Set.First.Title == "circles!") : -1;
                setIndex = theme >= 0 ? theme : Random.Shared.Next(view.Count);
                themeFromStart = theme >= 0;
            }
        }
        PollScores();
        UpdatePreview(menu);
        UpdateTiming();
        UpdateBackground();
        return true;
    }

    void Back()
    {
        Game.Skin.Sound("menuback")?.Play(BeatmapSamples.EffectVolume);
        if (Menu != null) Game.Screen = Menu;
        else Game.Quit = true;
    }

    public override void Update(double dtMs)
    {
        Game.Skin.UploadPrefetched();
        if (!Ambient()) return;

        if (Input.IsKeyDown(KeyboardKey.LeftControl) && Input.IsKeyPressed(KeyboardKey.O)) { Game.Screen = new Options(this, this); return; }

        // F1: stable's mod select, with stable's hotkeys.
        if (beatmapOptions) { while (Input.GetCharPressed() != 0) { } UpdateBeatmapOptions(); return; }
        if (Input.IsKeyPressed(KeyboardKey.F3) && !modSelect) { OpenBeatmapOptions(); return; }
        if (Input.IsKeyPressed(KeyboardKey.F1)) OpenModSelect(!modSelect);
        else if (modSelect)
        {
            while (Input.GetCharPressed() != 0) { } // keep mod hotkeys out of the search box
            UpdateModSelect();
            return;
        }
        if (modSelect) { while (Input.GetCharPressed() != 0) { } return; }

        int page = Math.Max(1, (int)((GetScreenHeight() - TopH - BottomH) / RowH) - 2);
        if (Pressed(KeyboardKey.Down)) MoveDiff(1);
        if (Pressed(KeyboardKey.Up)) MoveDiff(-1);
        if (Pressed(KeyboardKey.Right)) MoveSet(1);
        if (Pressed(KeyboardKey.Left)) MoveSet(-1);
        if (Pressed(KeyboardKey.PageDown)) MoveSet(page);
        if (Pressed(KeyboardKey.PageUp)) MoveSet(-page);
        if (Input.IsKeyPressed(KeyboardKey.Home)) MoveSet(-view.Count);
        if (Input.IsKeyPressed(KeyboardKey.End)) MoveSet(view.Count);
        // F2: random map; Shift+F2: back to the one before the last random pick (stable keeps the same history).
        if (Input.IsKeyPressed(KeyboardKey.F2) && view.Count > 0)
        {
            if (!Input.IsKeyDown(KeyboardKey.LeftShift)) { randomHistory.Push(Selected!); MoveSet(Random.Shared.Next(view.Count) - setIndex); }
            else while (randomHistory.TryPop(out var prev))
                if (view.FindIndex(v => v.Diffs.Contains(prev)) is var i and >= 0) { MoveSet(i - setIndex); diffIndex = view[i].Diffs.IndexOf(prev); break; }
        }
        float wheel = VolumeControl.Wheel;
        if (wheel != 0) MoveSet(-(int)Math.Round(wheel));

        var mouse = Input.GetMousePosition();
        if ((Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right)) && CheckCollisionPointRec(mouse, GroupBox(GetScreenWidth())))
        {
            int gi = Array.FindIndex(GroupModes, m => m.Key == GroupMode);
            SetGroupMode(GroupModes[((gi < 0 ? 0 : gi) + (Input.IsMouseButtonPressed(MouseButton.Right) ? -1 : 1) + GroupModes.Length) % GroupModes.Length].Key);
            return;
        }
        for (int t = 0; t < GroupModes.Length; t++)
            if (Input.IsMouseButtonPressed(MouseButton.Left) && CheckCollisionPointRec(mouse, TabRect(GetScreenWidth(), t))) { SetGroupMode(GroupModes[t].Key); return; }
        if ((Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right)) && CheckCollisionPointRec(mouse, SortBox(GetScreenWidth())))
        {
            CycleSort(Input.IsMouseButtonPressed(MouseButton.Right) ? -1 : 1);
            return;
        }
        if (Input.IsMouseButtonPressed(MouseButton.Left) && ClickBottomBar(mouse)) return;
        if (Input.IsMouseButtonPressed(MouseButton.Left) && mouse.Y > TopH && mouse.Y < GetScreenHeight() - BottomH)
        {
            if (mouse.X < GetScreenWidth() * 0.35f)
            {
                int n = ScoreRowAt(mouse.Y);
                if (n >= 0 && n < Math.Min(8, SelectedScores.Count)) { Watch(SelectedScores[n]); return; }
            }
            else if (ClickRow(mouse.Y)) return;
        }
        // Ctrl+1..9 watches that local score.
        if (Input.IsKeyDown(KeyboardKey.LeftControl))
            for (int n = 0; n < 9; n++)
                if (Input.IsKeyPressed(KeyboardKey.One + n) && n < SelectedScores.Count) { Watch(SelectedScores[n]); return; }
        // Ctrl+Enter: this one play with Auto, like stable (the selected mods stay as they are).
        if (Input.IsKeyPressed(KeyboardKey.Enter) && !Input.IsKeyDown(KeyboardKey.LeftAlt))
        {
            Play(Input.IsKeyDown(KeyboardKey.LeftControl) ? mods & ~(Mods.Relax | Mods.Autopilot) | Mods.Autoplay : mods);
            return;
        }

        if (Input.IsKeyPressed(KeyboardKey.Escape))
        {
            if (filter.Length > 0) { filter = ""; ApplyFilter(); }
            else Back();
            return;
        }

        bool changed = false;
        for (int ch = Input.GetCharPressed(); ch != 0; ch = Input.GetCharPressed())
        {
            filter += char.ConvertFromUtf32(ch);
            changed = true;
        }
        if (Pressed(KeyboardKey.Backspace) && filter.Length > 0)
        {
            filter = Input.IsKeyDown(KeyboardKey.LeftControl) ? "" : filter[..^1];
            changed = true;
        }
        if (changed) ApplyFilter();

        scrollRow += (SelectedRow - scrollRow) * (float)Math.Min(1, dtMs / 80);
        UpdateCarousel(dtMs, Input.GetMousePosition());
        UpdateSparkles();
    }

    // Rows: one per set, except the selected set, which is replaced by one row per difficulty (like stable).
    // Grouped: a header row per group, the open group's rows right under its header.
    int HeaderRowsBefore => Grouped ? groupIndex + 1 : 0;
    int EntryRows => view.Count == 0 ? 0 : view.Count - 1 + view[setIndex].Diffs.Count;
    int SelectedRow => HeaderRowsBefore + setIndex + diffIndex;
    int RowCount => (Grouped ? groups.Count : 0) + EntryRows;

    // Returns (set, diff, group) for a row: group >= 0 for a group header, diff = -1 for a collapsed set row.
    (int Set, int Diff, int Group) RowInfo(int row)
    {
        if (Grouped)
        {
            if (row <= groupIndex) return (-1, -1, row);
            row -= groupIndex + 1;
            if (row >= EntryRows) return (-1, -1, groupIndex + 1 + row - EntryRows);
        }
        if (row < setIndex) return (row, -1, -1);
        int diffs = view[setIndex].Diffs.Count;
        if (row < setIndex + diffs) return (setIndex, row - setIndex, -1);
        return (row - diffs + 1, -1, -1);
    }

    // ---- grouping (stable's tabs): Collections, Recently Played, By Artist, By Difficulty, No Grouping.
    static readonly (string Key, string Label)[] GroupModes =
        [("Collections", "Collections"), ("Recent", "Recently Played"), ("Artist", "By Artist"), ("Difficulty", "By Difficulty"), ("None", "No Grouping")];
    static readonly string[] GroupTabLabels = [.. GroupModes.Select(m => m.Label)];
    static string GroupMode => Config.Get("RemakeGroupMode", "None");
    // Header text and match count, rebuilt only when they change.
    (DbBeatmap?, Mods) headerFor;
    string[] header = [];
    // stable colours the length line by the speed mod and the stats line by HR / EZ (salmon harder, light
    // blue easier), and marks AR, OD and HP with a small arrow when a speed mod changes them in play
    static readonly Color Harder = new(246, 154, 161, 255), Easier = new(173, 216, 230, 255);
    Color lengthColour = Color.White, statsColour = Color.White;
    int statArrow;
    readonly CachedText<int> matchesText = new(v => v == 1 ? "1 match found!" : $"{v} matches found!");
    int matchCount; // difficulties left after the search filter
    // Per-row text for the leaderboard and grade icons, built once per replay / grade.
    static readonly Dictionary<string, string> smallGrades = [];
    static string SmallGrade(string grade) => smallGrades.TryGetValue(grade, out var s) ? s : smallGrades[grade] = $"ranking-{grade}-small";
    readonly Dictionary<ReplayInfo, (string Score, string Mods, string Acc)> rowText = [];
    (string Score, string Mods, string Acc) RowText(ReplayInfo r) => rowText.TryGetValue(r, out var t) ? t : rowText[r] =
        ($"Score: {r.Score:N0} ({r.MaxCombo}x)", r.Mods == Mods.None ? "" : string.Join(",", r.Mods.Short().Split(' ', StringSplitOptions.RemoveEmptyEntries)), $"{r.Accuracy:0.00}%");
    readonly string[] rowLines = new string[3];
    readonly NumberText leadText = new(v => $"+{v:N0}"), rankText = new(v => v.ToString());
    readonly Dictionary<(string, int), string> groupLabels = [];
    string GroupLabel(string name, int maps) => groupLabels.TryGetValue((name, maps), out var s) ? s
        : groupLabels[(name, maps)] = $"{name} ({maps} {(maps == 1 ? "map" : "maps")})";
    static bool Grouped => GroupMode != "None";
    readonly List<(string Name, List<(BeatmapSet Set, List<DbBeatmap> Diffs)> Entries)> groups = [];
    int groupIndex;
    static List<(string Name, HashSet<string> Md5s)>? collections;

    void SetGroupMode(string key)
    {
        if (key == GroupMode) return;
        Config.SetOwn("RemakeGroupMode", key);
        groupIndex = 0;
        ApplyFilter();
        Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
    }

    void OpenGroup(int g, bool last = false)
    {
        if (g < 0 || g >= groups.Count) return;
        groupIndex = g;
        view.Clear();
        view.AddRange(groups[g].Entries);
        setIndex = last ? Math.Max(0, view.Count - 1) : 0;
        diffIndex = 0;
        Game.Skin.Sound("select-expand")?.Play(BeatmapSamples.EffectVolume);
        SelectionChanged();
    }

    void BuildGroups(List<(BeatmapSet Set, List<DbBeatmap> Diffs)> all)
    {
        groups.Clear();
        // Groups keep each set's matching difficulties only (a set can sit in several difficulty/date groups).
        void ByDiff(Func<DbBeatmap, (int Order, string Name)?> key)
        {
            var map = new SortedDictionary<int, (string Name, List<(BeatmapSet, List<DbBeatmap>)> Entries)>();
            foreach (var (set, diffs) in all)
                foreach (var part in diffs.Select(d => (Key: key(d), Diff: d)).Where(x => x.Key != null).GroupBy(x => x.Key!.Value))
                {
                    if (!map.TryGetValue(part.Key.Order, out var gr)) map[part.Key.Order] = gr = (part.Key.Name, []);
                    gr.Entries.Add((set, part.Select(x => x.Diff).ToList()));
                }
            foreach (var gr in map.Values) groups.Add(gr);
        }
        switch (GroupMode)
        {
            case "Artist":
                ByDiff(d =>
                {
                    char c = d.Artist.Length > 0 ? char.ToUpperInvariant(d.Artist[0]) : '?';
                    return c is >= 'A' and <= 'Z' ? (c, c.ToString()) : char.IsDigit(c) ? (100, "0-9") : (101, "Other");
                });
                break;
            case "Difficulty":
                ByDiff(d =>
                {
                    float st = d.StarsFor(Mods.None);
                    int n = Math.Clamp((int)st, 0, 10);
                    return (n, n == 0 ? "Below 1 star" : n >= 10 ? "Over 10 stars" : n == 1 ? "1 star" : $"{n} stars");
                });
                break;
            case "Recent":
                var now = DateTime.UtcNow;
                ByDiff(d =>
                {
                    if (d.Unplayed || d.LastPlayed <= 0) return (9, "Never played");
                    double days = (now - new DateTime(d.LastPlayed, DateTimeKind.Utc)).TotalDays;
                    return days < 1 ? (0, "Today") : days < 2 ? (1, "Yesterday") : days < 7 ? (2, "Last week") : days < 30 ? (3, "This month")
                        : days < 60 ? (4, "Last month") : days < 365 ? (5, "Earlier this year") : (6, "Over a year ago");
                });
                break;
            case "Collections":
                collections ??= OsuDb.LoadCollections(Path.Combine(Config.OsuDir, "collection.db"));
                foreach (var (name, md5s) in collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var entries = all.Select(e => (e.Set, Diffs: e.Diffs.Where(d => md5s.Contains(d.Md5)).ToList())).Where(e => e.Diffs.Count > 0).ToList();
                    if (entries.Count > 0) groups.Add((name, entries));
                }
                break;
            default:
                groups.Add(("", all));
                break;
        }
    }

    // stable's carousel (units): rows sit at 200 + 48 per row, and stable adds 10 more around each difficulty of the
    // opened set: (n + 1) x 10 across the block, and 10 before a group header that follows a non-header (here: the
    // first header after the open group's entries). It scrolls so the selected row's centre sits at 220 (never
    // above the first row); scrollRow eases toward the selection. Hovering a panel spreads the rows around it by 10.
    float Extra(float row)
    {
        if (view.Count == 0) return 0;
        int n = view[setIndex].Diffs.Count;
        float start = (Grouped ? groupIndex + 1 : 0) + setIndex;
        int lead = start > 0 ? 1 : 0; // the gap before the first difficulty needs a panel above it
        float extra = Math.Clamp(row - start + lead, 0, n + lead);
        if (Grouped) extra += Math.Clamp(row - (groupIndex + EntryRows), 0, 1);
        return 10 * U * extra;
    }

    float Scroll => MathF.Max(0, scrollRow * RowH + Extra(scrollRow) - 20 * U);
    float RowCentre(int row) => 200 * U + row * RowH + Extra(row) - Scroll + (rowEase.TryGetValue(row, out var e) ? e.Y * U : 0);
    float RowY(int row) => RowCentre(row) - RowH / 2;

    // Panel x (units, from w/u - 340): the open set's difficulties and group headers 50 left, the hovered panel 45
    // more, plus min(200, |y/480 - 0.5| x 75) for the curve. Both x and the hover spread ease per row like stable's
    // (x by 0.95, y by 0.875 per 60Hz frame).
    readonly Dictionary<int, (float X, float Y)> rowEase = [];
    int hoveredRow = -1;
    double hoveredAt;

    float TargetX(int row)
    {
        var (_, d, gi) = RowInfo(row);
        return (gi >= 0 || d >= 0 ? -50 : 0) + (row == hoveredRow ? -45 : 0)
            + MathF.Min(200, MathF.Abs(RowCentre(row) / GetScreenHeight() - 0.5f) * 75);
    }

    float PanelX(int row) => (GetScreenWidth() / U - 340 + (rowEase.TryGetValue(row, out var e) ? e.X : TargetX(row))) * U;

    void UpdateCarousel(double dtMs, Vector2 mouse)
    {
        int h = GetScreenHeight();
        int under = mouse.Y > TopH && mouse.Y < h - BottomH && RowAt(mouse.Y) is var r && r >= 0 && r < RowCount && mouse.X >= PanelX(r) ? r : -1;
        if (under != hoveredRow)
        {
            hoveredRow = under;
            hoveredAt = Game.NowMs;
            if (under >= 0) Game.Skin.Sound("click-short")?.Play(BeatmapSamples.EffectVolume);
        }
        float fx = (float)Math.Pow(0.95, dtMs / (1000 / 60.0)), fy = (float)Math.Pow(0.875, dtMs / (1000 / 60.0));
        int first = Math.Max(0, RowAt(0) - 1), last = Math.Min(RowCount - 1, RowAt(h) + 1);
        foreach (int row in rowEase.Keys.Where(k => k < first || k > last).ToList()) rowEase.Remove(row);
        for (int row = first; row <= last; row++)
        {
            float spread = hoveredRow < 0 || row == hoveredRow ? 0 : row > hoveredRow ? 10 : -10;
            float tx = TargetX(row);
            rowEase[row] = rowEase.TryGetValue(row, out var e) ? (tx - (tx - e.X) * fx, spread - (spread - e.Y) * fy) : (tx, spread);
        }
    }

    int RowAt(float y)
    {
        int row = (int)MathF.Floor((y - 200 * U + Scroll) / RowH);
        while (RowY(row) > y) row--;
        while (RowY(row + 1) <= y) row++;
        return row;
    }

    bool ClickRow(float y)
    {
        int row = RowAt(y);
        if (row < 0 || row >= RowCount) return false;
        var (set, diff, group) = RowInfo(row);
        if (group >= 0)
        {
            if (group != groupIndex) OpenGroup(group);
            return true;
        }
        if (diff < 0)
        {
            if (set != setIndex) MoveSet(set - setIndex);
        }
        else if (diff == diffIndex) Play(mods);
        else { diffIndex = diff; SelectionChanged(); }
        return true;
    }

    // ---------------------------------------------------------------- drawing (stable's layout)

    static string Length(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    // stable's panel colours: other sets pink, the open set's difficulties blue, the selected one white.
    static readonly Color SetColour = new(235, 73, 153, 240), DiffColour = new(0, 150, 236, 240), SelectedColour = new(255, 255, 255, 240);

    public override void Draw()
    {
        int w = GetScreenWidth(), h = GetScreenHeight();
        float s = S;
        DrawBackground(w, h);
        DrawBackdrop(w, h, s);
        DrawModCaption();
        if (loading != null)
        {
            Game.TextBold("Loading osu!.db…", 40 * s, 40 * s, 36 * s, Color.White);
            DrawCursor();
            return;
        }

        int first = Math.Max(0, RowAt(0)), last = Math.Min(RowCount - 1, RowAt(h));
        for (int row = last; row >= first; row--) DrawRow(row, w, h, s);

        DrawTopBar(w, s);
        DrawScores(w, h, s); // over the top bar: stable's ranking box overlaps its lower edge
        DrawBottomBar(w, h, s);
        Snow.Update(this);
        Snow.Draw();
        DrawCursor();
        if (modSelect) DrawModSelect(w, h);
        if (beatmapOptions) DrawBeatmapOptions();
    }

    // Group header: stable's group panel colours (open group lighter), "Name (N maps)".
    // stable's song select backdrop, between the background and the carousel: the mode icon at the screen centre (additive,
    // alpha 80 -> 50 over each beat), and star bursts: on every update in a window just before each beat (300ms early,
    // min(100ms, beat/10) long; beats over 150ms) five star2s fly left from 100 units past the right edge along the
    // selected panel (+-20 units), 140-900 units over a second (Out), growing 1 -> 2, fading out and turning randomly.
    // Stable spawns once per update, so its density follows its frame rate; here at most one burst per 4ms.
    readonly List<(double At, float Y, float Dist, float R0, float R1)> sparkles = [];
    double lastSparkle;

    void UpdateSparkles()
    {
        double now = Game.NowMs;
        sparkles.RemoveAll(sp => now - sp.At >= 1000);
        if (modSelect || view.Count == 0 || now - lastSparkle < 4 || Beat() is not { } b || b.Length <= 150) return;
        if (Math.Abs(MusicMs + 300 - b.RedTime) % b.Length >= Math.Min(100, b.Length / 10)) return;
        lastSparkle = now;
        float y = RowCentre(SelectedRow);
        for (int i = 0; i < 5; i++)
            sparkles.Add((now, y + Random.Shared.Next(-20, 20) * U, Random.Shared.Next(140, 900), Random.Shared.NextSingle() * 4 - 2, Random.Shared.NextSingle() * 4 - 2));
    }

    void DrawBackdrop(int w, int h, float s)
    {
        float u = U;
        if (Game.Skin.Tex("mode-osu") is { } mode)
        {
            double phase = BeatPhase() is var p && !double.IsNaN(p) ? p : 0;
            BeginBlendMode(BlendMode.Additive);
            mode.Draw(new Vector2(w / 2f, 240 * u), s, ColorAlpha(Color.White, (float)((80 - phase * 30) / 255 * 70 / 255))); // its colour carries alpha 70 too
            EndBlendMode();
        }
        if (sparkles.Count == 0 || Game.Skin.Tex("star2") is not { } star) return;
        double now = Game.NowMs;
        BeginBlendMode(BlendMode.Additive);
        foreach (var (at, y, dist, r0, r1) in sparkles)
        {
            float e = (float)Easing.Apply(1, (now - at) / 1000);
            star.Draw(new Vector2(w + (100 - dist * e) * u, y), (1 + e) * s, ColorAlpha(Color.White, 1 - e), (r0 + (r1 - r0) * e) * 180 / MathF.PI);
        }
        EndBlendMode();
    }

    // The panel sprite at scale 1, its left edge at x and its centre line at cy, tinted. A hovered panel flashes 30%
    // lighter and fades back over a second.
    void DrawPanel(int row, float x, float cy, float s, Color colour)
    {
        if (row == hoveredRow) colour = ColorLerp(ColorLerp(colour, Color.White, 0.3f), colour, (float)Math.Clamp((Game.NowMs - hoveredAt) / 1000, 0, 1));
        if (Game.Skin.Tex("menu-button-background") is { } panel)
            DrawTexturePro(panel.Tex, new Rectangle(0, 0, panel.Tex.Width, panel.Tex.Height),
                new Rectangle(x, cy - panel.Height * s / 2, panel.Width * s, panel.Height * s), Vector2.Zero, 0, colour);
        else DrawRectangleRec(new Rectangle(x, cy - 27 * U, 430 * U, 54 * U), colour);
    }

    void DrawGroupHeader(int row, int g, float x, float cy, float s)
    {
        DrawPanel(row, x, cy, s, g == groupIndex ? new Color(35, 90, 193, 255) : new Color(35, 50, 143, 255));
        var (name, entries) = groups[g];
        int maps = entries.Sum(e => e.Diffs.Count);
        Game.Text(GroupLabel(name, maps), x + 15 * U, cy - 12 * U, 24 * U, Color.White); // stable: 24 at (15, 0), centred
    }

    void DrawRow(int row, int w, int h, float s)
    {
        var (si, d, gi) = RowInfo(row);
        float x = PanelX(row), cy = RowCentre(row), u = U;
        if (gi >= 0) { DrawGroupHeader(row, gi, x, cy, s); return; }
        var set = view[si];
        var b = d < 0 ? set.Set.First : set.Diffs[d];
        bool selected = d == diffIndex && si == setIndex && d >= 0;
        DrawPanel(row, x, cy, s, selected ? SelectedColour : d >= 0 ? DiffColour : SetColour);

        // stable's panel text (offsets from the panel's left edge and centre line, in 640x480 units): 75 in (room for
        // the online thumbnail) plus 8, or 25 beside a difficulty's grade icon; title 16 at -16, artist // creator 12
        // at -4, version 12 (bold) at +7 (one further in), stars at +18. The grade icon sits at 79.
        var grade = d >= 0 && BestGrade(b) is { } gr ? Game.Skin.Tex(SmallGrade(gr)) : null;
        var text = selected ? Game.Skin.SongSelectActiveText : Game.Skin.SongSelectInactiveText;
        // stable centres each line on its offset and its text bitmaps carry padding, so the glyphs sit 1.67 units right
        // of a plain top-left layout and a unit higher; its text also runs ~3% wider than ours at the same height
        // (measured on the default skin at 720p).
        float tx = x + (grade != null ? 100 : 83) * u, lx = tx + 1.67f * u, dy = -u;
        var faded = d >= 0 && !selected ? new Color(text.R, text.G, text.B, (byte)50) : text; // an open set's other difficulties: title and artist at alpha 50
        Game.Text(b.Title, lx, cy - 16 * u - 8 * u + dy, 16.55f * u, faded); // stable says 16; renders this wide (measured)
        Game.Text(b.PanelSubtitle, lx + u, cy - 4 * u - 6 * u + dy, 12.1f * u, faded);
        if (d < 0) return;
        Game.TextBold(b.Version, lx + u, cy + 7 * u - 6 * u + dy + u, 12.3f * u, text); // a further unit lower (measured)
        if (view[si].Set != starsSet) { starsSet = view[si].Set; starsAt = Game.NowMs; } // a set just opened: pop its stars in
        float step = (Game.Skin.Tex("star")?.Width ?? 46) * 0.6f * s;
        DrawStars(b.StarsFor(mods), tx + step / 2, cy + 18 * u, u, text, Game.NowMs - starsAt);
        if (grade is { } g) g.Draw(new Vector2(x + 79 * u + g.Width * s / 2, cy), s, Color.White);
    }

    // stable's star row: ten faint stars (alpha 30) under the filled ones, the last filled one scaled by the
    // fractional part; the sprite at scale 0.6, spaced by its own width. x is the first star's centre.
    BeatmapSet? starsSet;
    double starsAt;

    static void DrawStars(float stars, float x, float y, float u, Color tint, double age = double.MaxValue)
    {
        if (stars <= 0 || stars > 100) return;
        // stable: spaced by 0.6 of the sprite's width; the faint row at 0.6 x 0.35, white, additive, alpha 30;
        // filled stars at 0.6 x 0.6 x max(0.5, fill + 0.04 per index), so later stars are a touch bigger.
        // When a set opens they pop in from nothing: BackOut over 500ms, the first after 50ms, then 80ms apart.
        var star = Game.Skin.Tex("star");
        float sd = u * 480 / 768f, step = (star?.Width ?? 46) * 0.6f * sd;
        for (int i = 0; i < 10; i++)
        {
            var c = new Vector2(x + i * step, y);
            float f = Math.Clamp(stars - i, 0, 1);
            float filled = f > 0 ? 0.36f * MathF.Max(0.5f, f + 0.04f * i) * (float)Easing.Apply(30, (age - 50 - 80 * i) / 500) : 0;
            if (star is { } sp)
            {
                BeginBlendMode(BlendMode.Additive);
                sp.Draw(c, 0.21f * sd, new Color(255, 255, 255, 30)); // white on every panel (measured)
                EndBlendMode();
                if (filled > 0) sp.Draw(c, filled * sd, tint);
            }
            else if (filled > 0) DrawCircleV(c, step / 3 * filled / 0.6f, tint);
        }
    }

    string? BestGrade(DbBeatmap b)
    {
        if (!scores.TryGetValue(b.Md5, out var list)) return null;
        string me = Config.Get("Username", ""); // like stable: the local player's own scores only (as Personal Best)
        foreach (var r in list) if (!r.FromStable || r.Player.Equals(me, StringComparison.OrdinalIgnoreCase)) return GradeOf(r);
        return null;
    }

    static string GradeOf(ReplayInfo r)
    {
        int total = r.N300 + r.N100 + r.N50 + r.NMiss;
        if (total == 0) return "D";
        double r300 = r.N300 / (double)total, r50 = r.N50 / (double)total;
        bool silver = r.Mods.HasFlag(Mods.Hidden) || r.Mods.HasFlag(Mods.Flashlight);
        if (r.N300 == total) return silver ? "XH" : "X";
        if (r300 > 0.9 && r50 < 0.01 && r.NMiss == 0) return silver ? "SH" : "S";
        if (r300 > 0.8 && r.NMiss == 0 || r300 > 0.9) return "A";
        if (r300 > 0.7 && r.NMiss == 0 || r300 > 0.8) return "B";
        return r300 > 0.6 ? "C" : "D";
    }

    // stable's song select header (stable's sizes/positions, in 640x480 units; the right-hand group was
    // measured from a stable capture): map info top left, Group/Sort boxes and the group tabs top right, search at
    // the top of the song list.
    static Rectangle SortBox(int w) { float u = GetScreenHeight() / 480f; return new Rectangle(w - 129 * u, 18 * u, 119 * u, 14 * u); }
    static Rectangle GroupBox(int w) => SortBox(w) with { X = SortBox(w).X - 168 * GetScreenHeight() / 480f };
    // The group tabs: the selection-tab sprite at 0.6 units per texel (86 x 14), 73.9 apart so each overlaps the one
    // before, the first 392 units in from the right (measured).
    static Rectangle TabRect(int w, int t) { float u = GetScreenHeight() / 480f; return new Rectangle(w - (392 - t * 73.9f) * u, 34 * u, 143 * 0.6f * u, 24 * 0.6f * u); }

    void DrawTopBar(int w, float s)
    {
        float u = GetScreenHeight() / 480f;
        if (Game.Skin.Tex("songselect-top") is { } top) DrawTexturePro(top.Tex, new Rectangle(0, 0, top.Tex.Width, top.Tex.Height),
            new Rectangle(0, 0, Math.Max(w, top.Width * s), top.Height * s), Vector2.Zero, 0, Color.White);
        else DrawRectangle(0, 0, w, (int)TopH, new Color(0, 0, 0, 220));

        if (Selected is { } b)
        {
            // Ranked-status icon (osu!.db status: 4 ranked, 5 approved, 6 qualified, 7 loved).
            // Unknown / not submitted (an install that never went online): stable's question mark, at scale 1 (measured).
            string status = b.Status switch { 4 => "selection-ranked", 5 or 6 => "selection-approved", 7 => "selection-loved", 2 or 3 => "selection-notranked", _ => "selection-question" };
            if (status == "selection-question" && Game.Skin.Tex(status) is { } q) q.Draw(new Vector2(12 * u, 12 * u), s, Color.White);
            else if (Game.Skin.Tex(status) is { } icon) icon.Draw(new Vector2(11 * u, 11 * u), 15 * u / Math.Max(1, icon.Width), Color.White);
            if (headerFor != (b, mods)) // the header text only changes with the selection or mods
            {
                headerFor = (b, mods);
                double rate = mods.Rate();
                string bpm = b.MaxBpm <= 0 || b.MaxBpm > 100000 ? "?"
                    : Math.Abs(b.MaxBpm - b.MinBpm) < 0.5 ? $"{b.MaxBpm * rate:0}"
                    : $"{b.MinBpm * rate:0}-{b.MaxBpm * rate:0} ({b.MainBpm * rate:0})";
                int secs = (int)(b.TotalMs / 1000 / rate);
                float k = mods.HasFlag(Mods.HardRock) ? 1.4f : mods.HasFlag(Mods.Easy) ? 0.5f : 1;
                float kcs = mods.HasFlag(Mods.HardRock) ? 1.3f : k;
                float stars = b.StarsFor(mods);
                statArrow = rate > 1 ? 1 : rate < 1 ? -1 : 0;
                lengthColour = rate > 1 ? Harder : rate < 1 ? Easier : Color.White;
                statsColour = mods.HasFlag(Mods.HardRock) ? Harder : mods.HasFlag(Mods.Easy) ? Easier : Color.White; // speed mods leave it white (HT checked)
                header = [$"{(b.Source.Length > 0 ? $"{b.Source} ({b.Artist})" : b.Artist)} - {b.Title} [{b.Version}]", // stable: "Source (Artist)" when there is one
                    $"Mapped by {b.Creator}",
                    $"Length: {secs / 60:00}:{secs % 60:00} BPM: {bpm} Objects: {b.Circles + b.Sliders + b.Spinners}",
                    $"Circles: {b.Circles} Sliders: {b.Sliders} Spinners: {b.Spinners}",
                    $"CS:{Math.Min(10, b.CS * kcs):0.#} AR:{Math.Min(10, b.AR * k):0.#} OD:{Math.Min(10, b.OD * k):0.#} HP:{Math.Min(10, b.HP * k):0.#} Star Rating: {(stars is > 0 and < 1000 ? (Math.Floor(stars * 100) / 100).ToString("0.##") : "?")}"]; // stable truncates (3.628 shows 3.62); the star glyph is drawn after
            }
            Game.Text(header[0], 23.4f * u, -3 * u, 18.4f * u, Color.White); // stable's sizes 18, 12, 12 bold, 12, 8 render this wide (measured)
            Game.Text(header[1], 24.6f * u, 12 * u, 12.45f * u, Color.White);
            Game.TextBold(header[2], 2.5f * u, 24 * u, 12.5f * u, lengthColour);
            Game.Text(header[3], 2.5f * u, 36.5f * u, 12.4f * u, Color.White);
            // the stats word by word: the arrow (3x2.2 units, top at the digits' top, 0.5 after the value) follows AR, OD, HP
            float statX = 2 * u, size = 8.2f * u, space = Game.TextWidth(" ", size);
            var parts = header[4].Split(' ', 5);
            for (int p = 0; p < parts.Length; p++)
            {
                if (p > 0) statX += space;
                Game.Text(parts[p], statX, 48 * u, size, statsColour);
                statX += Game.TextWidth(parts[p], size);
                if (statArrow == 0 || p is 0 or 4) continue;
                float ax = statX + 0.5f * u, at = 50.2f * u, ab = at + 2.2f * u;
                if (statArrow > 0) DrawTriangle(new(ax + 1.5f * u, at), new(ax, ab), new(ax + 3 * u, ab), statsColour);
                else DrawTriangle(new(ax, at), new(ax + 1.5f * u, ab), new(ax + 3 * u, at), statsColour);
                statX += 3.5f * u;
            }
            Options.DrawIcon(0xf005, statX + 4.5f * u, 52.5f * u, 8 * u, statsColour); // Aller has no star: stable's comes from a fallback font
        }

        // Group / Sort: label, then a boxed value. Only sorting is functional; grouping shows stable's default.
        void Box(Rectangle r, string label, Color labelColour, string value)
        {
            float size = 23 * u; // measured sizes throughout: stable's text renders ~3-7% wider than its nominal size here
            Game.TextLight(label, r.X - 3 * u - Game.LightWidth(label, size), r.Y + r.Height - size * 0.8f + 2 * u, size, labelColour); // Aller Light
            DrawRectangleRec(r, Color.Black);
            DropdownBorder(r, new Color((byte)(labelColour.R * 0.83f), (byte)(labelColour.G * 0.83f), (byte)(labelColour.B * 0.83f), (byte)255)); // label colour at 83% (measured)
            Game.Text(value, r.X + 0.7f * u, r.Y + r.Height / 2 - 7.3f * u, 13.4f * u, Color.White);
            Options.DrawIcon(0xf078, r.X + r.Width - 9.8f * u, r.Y + r.Height / 2 + 0.7f * u, 12.5f * u, Color.White); // stable's chevron (measured)
        }
        var sortBox = SortBox(w);
        string sortLabel = SortModes.FirstOrDefault(m => m.Key.Equals(SortMode, StringComparison.OrdinalIgnoreCase)).Label ?? "By Artist";
        Box(sortBox, "Sort", new Color(174, 210, 139, 255), sortLabel);
        Box(GroupBox(w), "Group", new Color(146, 195, 230, 255), GroupModes.FirstOrDefault(m => m.Key == GroupMode).Label ?? "No Grouping");

        // Group tabs (stable's selection-tab sprite), the current mode active.
        var tabs = GroupTabLabels;
        for (int t = 0; t < tabs.Length; t++)
        {
            bool active = GroupModes[t].Key == GroupMode;
            var tr = TabRect(w, t);
            if (Game.Skin.Tex("selection-tab") is { } tab)
                DrawTexturePro(tab.Tex, new Rectangle(0, 0, tab.Tex.Width, tab.Tex.Height), tr, Vector2.Zero, 0, active ? Color.White : new Color(235, 30, 70, 255));
            else DrawRectangleRec(tr, active ? Color.White : new Color(235, 30, 70, 255));
            float size = 9.9f * u;
            Game.TextBold(tabs[t], tr.X + tr.Width / 2 - Game.BoldWidth(tabs[t], size) / 2, tr.Y + tr.Height / 2 - size / 2, size, active ? Color.Black : Color.White);
        }

        // Search, at the top of the song list.
        float sx = w - 218 * u;
        string search = filter.Length == 0 ? "Type to search!" : filter;
        if (filter.Length == 0) DrawRectangleRec(new Rectangle(w - 302 * u, 50.7f * u, 302 * u, 11.3f * u), new Color(235, 73, 153, 195)); // the empty search box: a pink bar (measured)
        else DrawRectangleRec(new Rectangle(w - 227.5f * u, 51.1f * u, 227.5f * u, 29.9f * u), new Color(0, 0, 0, 95)); // a search typed: a dark box behind it (measured; stable's edges are soft)
        Game.TextBold("Search:", sx, 54 * u, 13.6f * u, new Color(173, 255, 47, 255)); // GreenYellow
        Game.TextBold(search, sx + Game.BoldWidth("Search: ", 13.6f * u), 54 * u, 13.6f * u, Color.White);
        if (message.Length > 0 || filter.Length > 0)
            Game.TextBold(message.Length > 0 ? message : matchesText.Of(matchCount), sx, 66.5f * u, 11.35f * u, message.Length > 0 ? new Color(255, 120, 120, 255) : Color.White);
    }

    // The user panel (offline, so always the guest): name, "Click to sign in!" and an empty level bar (measured).
    // x, y: the name's text origin in units (song select 443, 430; main menu 53.1, 0.6).
    internal static void DrawUserPanel(float x, float y, float alpha)
    {
        float u = U;
        Game.TextLight("Guest", x * u, (y + 0.9f) * u, 14.5f * u, ColorAlpha(Color.White, alpha)); // measured at 1080p
        Game.TextLight("Click to sign in!", (x - 0.3f) * u, (y + 15) * u, 10.8f * u, ColorAlpha(Color.White, alpha));
        var bar = new Rectangle((x + 25.7f) * u, (y + 42.7f) * u, 123.3f * u, 6.7f * u);
        DrawRectangleRounded(bar, 1, 8, ColorAlpha(new Color(38, 38, 38, 255), alpha));
        DrawRectangleRoundedLinesEx(bar, 1, 8, Math.Max(1, u), ColorAlpha(new Color(90, 90, 90, 255), alpha));
    }

    // Bottom bar buttons: left edges at 140, 197.6, 245.6 and 293.6 units on widescreen, here in
    // 768-scale texels (units / 0.625). Hover swaps to the -over sprite like stable.
    static readonly (string Name, float X)[] BottomButtons = [("selection-mode", 224), ("selection-mods", 316.16f), ("selection-random", 392.96f), ("selection-options", 469.76f)];

    readonly BackButton backButton = new();

    Rectangle ButtonRect(string name, float x, int h, float s)
    {
        var sp = Game.Skin.Tex(name);
        float bw = (sp?.Width ?? 77) * s, bh = (sp?.Height ?? 90) * s;
        return new Rectangle(x * s, h - bh, bw, bh);
    }

    void DrawBottomBar(int w, int h, float s)
    {
        if (Game.Skin.Tex("songselect-bottom") is { } bottom) DrawTexturePro(bottom.Tex, new Rectangle(0, 0, bottom.Tex.Width, bottom.Tex.Height),
            new Rectangle(0, h - bottom.Height * s, w, bottom.Height * s), Vector2.Zero, 0, Color.White);
        else DrawRectangle(0, h - (int)BottomH, w, (int)BottomH, new Color(0, 0, 0, 220));

        var mouse = Input.GetMousePosition();
        backButton.Draw(h, mouse, Beat() is { } bt ? (bt.Phase * bt.Length, bt.Length) : null);
        foreach (var (name, bx) in BottomButtons)
        {
            var r = ButtonRect(name, bx, h, s);
            bool hover = CheckCollisionPointRec(mouse, r) || name == "selection-mods" && modSelect;
            var sp = (hover ? Game.Skin.Tex(name + "-over") : null) ?? Game.Skin.Tex(name);
            sp?.Draw(new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), s, Color.White);
            if (hover && name != "selection-mode" && Game.Skin.Tex(name + "-over") == null) DrawRectangleRec(r, new Color(255, 255, 255, 40));
        }
        // Active mod icons next to the buttons.
        // The current mode's icon sits in the Mode button (additive, centred 28.8 units into it, 35 up).
        float u = h / 480f;
        if (Game.Skin.Tex("mode-osu-small") is { } modeIcon)
        {
            BeginBlendMode(BlendMode.Additive);
            modeIcon.Draw(new Vector2((140 + 28.8f) * u, h - 35 * u), s, Color.White);
            EndBlendMode();
        }
        float mx = 546.56f * s; // 341.6 units: past Options, where stable continues
        foreach (var (flag, icon) in ModIcons)
            if (mods.HasFlag(flag) && !(flag == Mods.DoubleTime && mods.HasFlag(Mods.Nightcore)) && !(flag == Mods.SuddenDeath && mods.HasFlag(Mods.Perfect))
                && Game.Skin.Tex("selection-mod-" + icon) is { } m)
            {
                m.Draw(new Vector2(mx, h - 40 * s), 0.6f * s, Color.White);
                mx += 30 * s;
            }

        DrawUserPanel(443, 430, 1);

        // The osu! cookie: 40 units in from the right at y 450, scale 0.45 -> 0.4 over each beat, with a
        // copy swelling 0.45 -> 0.49 and fading 0.7 -> 0 (a 1s cycle without timing).
        if (Game.Skin.Tex("menu-osu") is { } cookie)
        {
            float p = (float)(BeatPhase() is var ph && !double.IsNaN(ph) ? ph : Game.NowMs % 1000 / 1000);
            var at = new Vector2(w - 40 * u, 450 * u);
            cookie.Draw(at, (0.45f - 0.05f * p) * s, Color.White);
            cookie.Draw(at, (0.45f + 0.04f * p) * s, ColorAlpha(Color.White, 0.7f * (1 - p)));
        }
    }

    internal static readonly (Mods Flag, string Icon)[] ModIcons =
    [
        (Mods.Easy, "easy"), (Mods.NoFail, "nofail"), (Mods.HalfTime, "halftime"), (Mods.HardRock, "hardrock"),
        (Mods.SuddenDeath, "suddendeath"), (Mods.Perfect, "perfect"), (Mods.DoubleTime, "doubletime"), (Mods.Nightcore, "nightcore"),
        (Mods.Hidden, "hidden"), (Mods.Flashlight, "flashlight"), (Mods.Relax, "relax"), (Mods.Autopilot, "relax2"),
        (Mods.SpunOut, "spunout"), (Mods.Autoplay, "autoplay"), (Mods.ScoreV2, "scorev2"),
    ];

    bool ClickBottomBar(Vector2 mouse)
    {
        int w = GetScreenWidth(), h = GetScreenHeight();
        float s = S;
        if (CheckCollisionPointRec(mouse, BackButton.Rect(h)))
        {
            Back();
            return true;
        }
        foreach (var (name, bx) in BottomButtons)
        {
            if (!CheckCollisionPointRec(mouse, ButtonRect(name, bx, h, s))) continue;
            Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
            if (name == "selection-mods") OpenModSelect(!modSelect);
            else if (name == "selection-random" && view.Count > 0) MoveSet(Random.Shared.Next(view.Count) - setIndex);
            else if (name == "selection-options") OpenBeatmapOptions(); // stable's Beatmap Options dialog
            return true;
        }
        if (Vector2.Distance(mouse, new Vector2(w - 70 * s, h - 60 * s)) < 60 * s) { Play(mods); return true; }
        return false;
    }

    // Local scores, left side, as stable's leaderboard panels.
    // stable's local leaderboard (measured from a stable capture, in 640x480 units): a ranking box at 73-88, then
    // up to eight rows of 33.3 from 96: rank, grade, name (16), "Score: N (Mx)" (12), and right-aligned mods /
    // accuracy / gap to the next score (10); "Personal Best" below.
    static float U => GetScreenHeight() / 480f;
    float ScoreTop => 96 * U;
    float ScoreRowH => 33.3f * U;
    int ScoreRowAt(float y) => (int)MathF.Floor((y - ScoreTop) / ScoreRowH);

    void DrawScoreRow(ReplayInfo r, int rank, float y, long? nextScore, bool hover)
    {
        float u = U;
        var row = new Rectangle(4.3f * u, y, 235.7f * u, ScoreRowH - 2 * u);
        DrawRectangleRec(row, new Color(0, 0, 0, hover ? 190 : 130));
        if (rank > 0)
        {
            string n = rankText.Of(rank);
            Game.TextBold(n, 20 * u - Game.BoldWidth(n, 18 * u) / 2, y + row.Height / 2 - 9 * u, 18 * u, ColorAlpha(Color.White, 0.6f));
        }
        if (Game.Skin.Tex(SmallGrade(GradeOf(r))) is { } g) g.Draw(new Vector2(49 * u, y + 14 * u), 21 * u / Math.Max(1, g.Height), Color.White);
        string name = r.FromStable ? (r.Player.Length > 0 ? r.Player : "stable") : Config.Get("Username", "You");
        Game.Text(name, 63 * u, y + 7.7f * u - 8 * u, 16 * u, Color.White);
        var text = RowText(r);
        Game.Text(text.Score, 63 * u, y + 20.5f * u - 6 * u, 12 * u, Color.White);
        float right = 237.7f * u, size = 10 * u;
        string[] lines = rowLines;
        lines[0] = text.Mods; lines[1] = text.Acc; lines[2] = nextScore is long nx ? leadText.Of(r.Score - nx) : "";
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length > 0) Game.Text(lines[i], right - Game.TextWidth(lines[i], size), y + (5 + 10.3f * i) * u - size / 2, size, Color.White);
    }

    // stable's dropdown outline (measured): about half a unit thick with ~1.3-unit rounded corners.
    void DropdownBorder(Rectangle r, Color c) =>
        DrawRectangleRoundedLinesEx(r, 2.6f * U / r.Height, 8, Math.Max(1, U / 2), c);

    void DrawScores(int w, int h, float s)
    {
        float u = U;
        var box = new Rectangle(5 * u, 73 * u, 191 * u, 15 * u);
        DrawRectangleRec(box, Color.Black);
        DropdownBorder(box, new Color(22, 130, 179, 255));
        Game.Text("Local Ranking", box.X + 3 * u, box.Y + box.Height / 2 - 7 * u, 14 * u, Color.White);
        Options.DrawIcon(0xf078, box.X + box.Width - 9.8f * u, box.Y + box.Height / 2 + 0.7f * u, 12.5f * u, Color.White); // stable's chevron (measured)

        var list = SelectedScores;
        if (list.Count == 0) Game.Text("No records set!", 63 * u, ScoreTop + 6 * u, 14 * u, ColorAlpha(Color.White, 0.8f));
        var mouse = Input.GetMousePosition();
        for (int n = 0; n < Math.Min(8, list.Count); n++)
        {
            float y = ScoreTop + n * ScoreRowH;
            bool hover = mouse.X < 240 * u && ScoreRowAt(mouse.Y) == n && mouse.Y >= ScoreTop;
            DrawScoreRow(list[n], n + 1, y, n + 1 < list.Count ? list[n + 1].Score : null, hover);
        }

        // Personal best: the local player's own top score.
        string me = Config.Get("Username", "");
        float centre = 120 * u;
        Game.TextBold("Personal Best", centre - Game.BoldWidth("Personal Best", 12 * u) / 2, 369 * u - 6 * u, 12 * u, Color.White);
        int best = -1; // a loop, not FindIndex: a lambda capturing me would allocate every frame
        for (int i = 0; i < list.Count && best < 0; i++) if (!list[i].FromStable || list[i].Player.Equals(me, StringComparison.OrdinalIgnoreCase)) best = i;
        if (best >= 0) DrawScoreRow(list[best], best + 1, 380 * u, null, false);
        else
        {
            DrawRectangleRec(new Rectangle(45 * u, 380 * u, 195 * u, 29 * u), new Color(0, 0, 0, 100));
            Game.Text("No personal record set", 63 * u, 394.5f * u - 6.5f * u, 13 * u, Color.White);
        }
    }
    // F3 / Beatmap Options: stable's dialog for the selected set. Offline, the actions that would change stable's files
    // (collections, deleting, the unplayed flag, local scores) or need the editor only explain themselves.
    static readonly (string Label, Color Colour, string? Notice)[] BeatmapOptions =
    [
        ("Manage Collections", new Color(154, 205, 50, 255), "Collections are read from stable; manage them in osu!stable."),
        ("Delete...", new Color(255, 69, 0, 255), "This remake never deletes your beatmaps; delete them in osu!stable."),
        ("Remove from Unplayed", new Color(207, 124, 215, 255), "The remake doesn't write osu!stable's database."),
        ("Clear local scores", new Color(207, 124, 215, 255), "Your scores are kept; clear them in osu!stable."),
        ("Edit", new Color(255, 69, 0, 255), "The beatmap editor isn't part of this offline remake."),
        ("Cancel", Color.Gray, null),
    ];
    bool beatmapOptions;
    double beatmapOptionsAt;
    float beatmapOptionsY;

    void OpenBeatmapOptions() { if (Selected != null) { beatmapOptions = true; beatmapOptionsAt = Game.NowMs; } }

    void UpdateBeatmapOptions()
    {
        var mouse = Input.GetMousePosition();
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        int chosen = Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsKeyPressed(KeyboardKey.F3) ? BeatmapOptions.Length - 1 : -1;
        for (int i = 0; i < BeatmapOptions.Length && chosen < 0; i++)
            if (Input.IsKeyPressed(KeyboardKey.One + i) || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(beatmapOptionsY + i * 50))) chosen = i;
        if (chosen < 0) return;
        beatmapOptions = false;
        if (BeatmapOptions[chosen].Notice is { } notice) Notification.Show(notice, 3000);
    }

    void DrawBeatmapOptions()
    {
        if (Selected is not { } b) return;
        double age = Game.NowMs - beatmapOptionsAt;
        float fade = (float)Math.Clamp(age / 200, 0, 1);
        Dialog.Backdrop(fade);
        beatmapOptionsY = Dialog.Title($"{b.Artist} - {b.Title}\nWhat do you want to do with this beatmap?", fade);
        var mouse = Input.GetMousePosition();
        for (int i = 0; i < BeatmapOptions.Length; i++)
            Dialog.Button(i, BeatmapOptions[i].Label, BeatmapOptions[i].Colour, beatmapOptionsY + i * 50, age, mouse);
    }

    // stable's mod select: a dialog titled with its blurb, "Score Multiplier" centred 20 units above the content, three
    // labelled rows (size 24 at x 20; green, orange-red, white) of icons 66 units apart from x 240, rows 60 apart from 35
    // units under the content start, then Reset All Mods / Close. A selected icon springs to 1.2x and 0.1 rad (elastic,
    // 400ms) over a sky-blue glow. 1 / 2 press the buttons; stable's letter hotkeys toggle mods.
    static readonly (string Label, Color Colour, (KeyboardKey Key, Mods Flag, Mods Alt, string Icon, string AltIcon)[] Mods)[] ModRows =
    [
        ("Difficulty Reduction", new Color(50, 205, 50, 255), [(KeyboardKey.Q, Mods.Easy, Mods.None, "easy", ""), (KeyboardKey.W, Mods.NoFail, Mods.None, "nofail", ""), (KeyboardKey.E, Mods.HalfTime, Mods.None, "halftime", "")]),
        ("Difficulty Increase", new Color(255, 69, 0, 255), [(KeyboardKey.A, Mods.HardRock, Mods.None, "hardrock", ""), (KeyboardKey.S, Mods.SuddenDeath, Mods.Perfect, "suddendeath", "perfect"),
            (KeyboardKey.D, Mods.DoubleTime, Mods.Nightcore, "doubletime", "nightcore"), (KeyboardKey.F, Mods.Hidden, Mods.None, "hidden", ""), (KeyboardKey.G, Mods.Flashlight, Mods.None, "flashlight", "")]),
        ("Special", Color.White, [(KeyboardKey.Z, Mods.Relax, Mods.None, "relax", ""), (KeyboardKey.X, Mods.Autopilot, Mods.None, "relax2", ""), (KeyboardKey.C, Mods.SpunOut, Mods.None, "spunout", ""), (KeyboardKey.V, Mods.Autoplay, Mods.None, "autoplay", ""),
            (KeyboardKey.B, Mods.ScoreV2, Mods.None, "scorev2", "")]),
    ];
    const string ModBlurb = "Mods provide different ways to enjoy gameplay. Some have an effect on the score you can achieve during ranked play. Others are just for fun.";
    double modSelectAt, modButtonFlashAt = double.MinValue;
    int modButtonFlashed = -1;
    readonly Dictionary<Mods, (bool On, double At)> modAnim = [];
    Color multFrom = Color.White, multTo = Color.White, multNow = Color.White;
    double multAt;
    float modContentY; // units, from the last draw (the title wraps with the window width)

    void OpenModSelect(bool open)
    {
        modSelect = open;
        if (open) { modSelectAt = Game.NowMs; modButtonFlashed = -1; }
    }

    Vector2 ModIconPos(int row, int col) { float u = U; return new Vector2((240 + col * 66) * u, (modContentY + 35 + row * 60) * u); }

    void UpdateModSelect()
    {
        // each mod's key follows its binding (keyEasy ... keyAuto); Toggle works on stable's default keys
        for (int i = 0; i < ModKeys.Length; i++)
            if (Input.IsKeyPressed(Config.Key(ModBindings[i], ModKeys[i]))) mods = ModInfo.Toggle(mods, ModKeys[i]);
        var mouse = Input.GetMousePosition();
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (click)
            for (int r = 0; r < ModRows.Length; r++)
                for (int c = 0; c < ModRows[r].Mods.Length; c++)
                    if (Vector2.Distance(mouse, ModIconPos(r, c)) < 21 * U) mods = ModInfo.Toggle(mods, ModRows[r].Mods[c].Key);
        float buttonsY = modContentY + 35 + 180;
        bool reset = Input.IsKeyPressed(KeyboardKey.One) || Input.IsKeyPressed(KeyboardKey.Backspace) || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(buttonsY));
        bool close = Input.IsKeyPressed(KeyboardKey.Two) || Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsKeyPressed(KeyboardKey.Enter)
            || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(buttonsY + 50));
        if (reset) { mods = Mods.None; modButtonFlashed = 0; modButtonFlashAt = Game.NowMs; }
        if (close) OpenModSelect(false);
    }

    void DrawModSelect(int w, int h)
    {
        float u = U, s = S;
        double now = Game.NowMs, age = now - modSelectAt;
        float fade = (float)Math.Clamp(age / 200, 0, 1);
        Dialog.Backdrop(fade);
        modContentY = Dialog.Title(ModBlurb, fade);
        string mult = $"Score Multiplier: {mods.Multiplier():0.00}x";
        float ms = 31.1f * u; // stable's 30, measured
        // stable: the text fades over 400ms to green-yellow above 1x, orange-red below, white at 1x
        double mul = mods.Multiplier();
        var multTarget = mul > 1 ? new Color(173, 255, 47, 255) : mul < 1 ? new Color(255, 69, 0, 255) : Color.White;
        if (multTarget.R != multTo.R || multTarget.G != multTo.G || multTarget.B != multTo.B) { multFrom = multNow; multTo = multTarget; multAt = now; }
        multNow = ColorLerp(multFrom, multTo, (float)Math.Clamp((now - multAt) / 400, 0, 1));
        Game.Text(mult, w / 2f - Game.TextWidth(mult, ms) / 2, (modContentY - 20) * u - ms / 2, ms, ColorAlpha(multNow, fade));
        for (int r = 0; r < ModRows.Length; r++)
        {
            var (label, colour, list) = ModRows[r];
            float ls = 25.2f * u, cy = (modContentY + 35 + r * 60) * u;
            Game.Text(label, 22.7f * u, cy - ls / 2, ls, ColorAlpha(colour, fade)); // measured at 1080p
            for (int c = 0; c < list.Length; c++)
            {
                var (_, flag, alt, icon, altIcon) = list[c];
                bool on = mods.HasFlag(flag);
                if (!modAnim.TryGetValue(flag, out var an) || an.On != on) modAnim[flag] = an = (on, now);
                float e = (float)Easing.Apply(25, (now - an.At) / 400);
                float scale = on ? 1 + 0.2f * e : 1.2f - 0.2f * e, rot = on ? 0.1f * e : 0.1f * (1 - e);
                var pos = ModIconPos(r, c);
                if (on && Game.Skin.Tex("lighting") is { } glow)
                {
                    BeginBlendMode(BlendMode.Additive);
                    glow.Draw(pos, s, ColorAlpha(Color.SkyBlue, 0.2f * fade));
                    EndBlendMode();
                }
                string name = alt != Mods.None && mods.HasFlag(alt) ? altIcon : icon;
                Game.Skin.Tex("selection-mod-" + name)?.Draw(pos, scale * s, ColorAlpha(Color.White, fade), rot * 180 / MathF.PI);
            }
        }
        var mouse = Input.GetMousePosition();
        float buttonsY = modContentY + 35 + 180;
        Dialog.Button(0, "Reset All Mods", new Color(255, 69, 0, 255), buttonsY, age, mouse, modButtonFlashed == 0 ? now - modButtonFlashAt : double.MaxValue);
        Dialog.Button(1, "Close", Color.Gray, buttonsY + 50, age, mouse);
    }

    // ---------------------------------------------------------------- background (loaded off the main thread)

    Texture2D bgTex;
    string bgKey = "";
    Task<(string Key, Image Img)>? bgLoading;
    double bgShownAt;

    void UpdateBackground()
    {
        if (bgLoading is { IsCompleted: true })
        {
            var (key, img) = bgLoading.Result;
            bgLoading = null;
            if (key == bgKey && img.Width > 0)
            {
                if (bgTex.Id != 0) UnloadTexture(bgTex);
                bgTex = LoadTextureFromImage(img);
                SetTextureFilter(bgTex, TextureFilter.Bilinear);
                bgShownAt = Game.NowMs;
            }
            if (img.Width > 0) UnloadImage(img);
        }
        if (Selected is not { } b || bgLoading != null || Game.NowMs - selectionChangedAt < 120) return;
        string file = b.FilePath;
        if (file == bgKey) return;
        bgKey = file;
        bgLoading = LoadBackgroundImage(file);
    }

    // Its own method so the closure is only made when a load starts (UpdateBackground runs every frame).
    static Task<(string, Image)> LoadBackgroundImage(string file) => Task.Run(() =>
    {
        try
        {
            var meta = Beatmap.Load(file, metadataOnly: true);
            var path = Path.Combine(meta.Folder, meta.Background);
            return (file, meta.Background.Length > 0 && File.Exists(path) ? LoadImage(path) : default);
        }
        catch (IOException) { return (file, default(Image)); }
    });

    internal void DrawBackground(int w, int h)
    {
        if (bgTex.Id == 0) return;
        // stable's menu background parallax (MenuParallax): 1/180 of the cursor's offset from the centre, scaled 1.01.
        bool parallax = Config.GetBool("MenuParallax", true);
        float k = Math.Max(w / (float)bgTex.Width, h / (float)bgTex.Height) * (parallax ? 1.01f : 1);
        var shift = parallax ? -(Input.GetMousePosition() - new Vector2(w / 2f, h / 2f)) / 180 : Vector2.Zero;
        float a = (float)Math.Clamp((Game.NowMs - bgShownAt) / 250, 0, 1) * 0.6f; // stable: the menu background at 60% (measured)
        DrawTexturePro(bgTex, new Rectangle(0, 0, bgTex.Width, bgTex.Height),
            new Rectangle((w - bgTex.Width * k) / 2 + shift.X, (h - bgTex.Height * k) / 2 + shift.Y, bgTex.Width * k, bgTex.Height * k),
            Vector2.Zero, 0, ColorAlpha(Color.White, a));
    }

    public static void DrawCursor() => DrawCursorAt(Input.GetMousePosition());

    // stable's classic cursor trail (skins without cursormiddle): at most every 60Hz frame a copy of cursortrail is
    // left at the cursor, fading out over 150ms.
    static readonly List<(Vector2 Pos, double At)> trail = [];
    static double lastTrail = double.MinValue;
    static Vector2? smoothFrom;

    // Set by the player: stable's "Automatic cursor size" scales the cursor by 1 - 0.7 x (CS - 4) / 5 in play.
    internal static float CursorPlayScale = 1;

    // stable's cursor ripples (the Cursor ripples option): each click leaves the skin's cursor-ripple (else a ring) at
    // the cursor, added on, growing 0.05 -> 0.5 (Out) and fading out from 20% over 700ms.
    static readonly List<(Vector2 Pos, double At)> ripples = [];
    internal static void Ripple(Vector2 at) { if (Config.GetBool("CursorRipple", false)) ripples.Add((at, Game.NowMs)); }

    static void DrawRipples()
    {
        ripples.RemoveAll(r => Game.NowMs - r.At > 700);
        if (ripples.Count == 0 || (Game.Skin.Own("cursor-ripple") ? Game.Skin.Tex("cursor-ripple") : Game.Skin.DefaultTex("approachcircle")) is not { } tex) return;
        float sd = GetScreenHeight() / 768f;
        BeginBlendMode(BlendMode.Additive);
        foreach (var (p, at) in ripples)
        {
            float f = (float)((Game.NowMs - at) / 700);
            tex.Draw(p, sd * (0.05f + 0.45f * (float)Easing.Apply(1, f)), ColorAlpha(Color.White, 0.2f * (1 - f)));
        }
        EndBlendMode();
    }

    public static void DrawCursorAt(Vector2 pos)
    {
        if (Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right)) Ripple(pos);
        DrawRipples();
        float s = GetScreenHeight() / 768f * (float)Config.GetDouble("CursorSize", 1) * CursorPlayScale;
        var skin = Game.Skin;
        // CursorCentre: 0 anchors the sprite's top-left at the cursor instead of its centre.
        Vector2 Anchor(Sprite sp) => skin.CursorCentre ? pos : pos + new Vector2(sp.Width, sp.Height) * s / 2;
        double now = Game.NowMs;
        bool middle = skin.Own("cursor") ? skin.Own("cursormiddle") : skin.Tex("cursormiddle") != null;
        if (skin.Tex("cursortrail") is { Width: >= 5 } tr && !middle)
        {
            if (now - lastTrail >= 1000 / 60.0) { trail.Add((Anchor(tr), now)); lastTrail = now; }
            int kept = 0; // RemoveAll without a per-frame closure
            for (int i = 0; i < trail.Count; i++) if (now - trail[i].At < 150) trail[kept++] = trail[i];
            trail.RemoveRange(kept, trail.Count - kept);
            foreach (var (p, at) in trail) tr.Draw(p, s, ColorAlpha(Color.White, 1 - (float)((now - at) / 150)));
        }
        else if (skin.Tex("cursortrail") is { Width: >= 5 } smooth)
        {
            // stable's smooth trail (with cursormiddle): copies every 1/2.5 of the sprite's width along the path,
            // added on, fading (1 - age/300ms)^1.7.
            var at = Anchor(smooth);
            float step = smooth.Width * s / 2.5f;
            if (smoothFrom is not { } from || Vector2.Distance(from, at) > GetScreenWidth()) smoothFrom = at;
            else
            {
                var d = at - from;
                float len = d.Length();
                while (len >= step)
                {
                    from += d / len * step;
                    len -= step;
                    trail.Add((from, now));
                }
                smoothFrom = from;
            }
            int kept = 0;
            for (int i = 0; i < trail.Count; i++) if (now - trail[i].At < 300) trail[kept++] = trail[i];
            trail.RemoveRange(kept, trail.Count - kept);
            BeginBlendMode(BlendMode.Additive);
            foreach (var (p, t) in trail) smooth.Draw(p, s, ColorAlpha(Color.White, MathF.Pow(1 - (float)((now - t) / 300), 1.7f)));
            EndBlendMode();
        }
        if (skin.Tex("cursor") is { } c) c.Draw(Anchor(c), s, Color.White, skin.CursorRotate ? (float)(now % 10000 / 10000 * 360) : 0); // a turn per 10s
        else DrawCircleV(pos, 12 * s, new Color(255, 200, 80, 255));
        if (middle && skin.Tex("cursormiddle") is { } m) m.Draw(Anchor(m), s, Color.White); // the default cursor's blue centre
    }
}
