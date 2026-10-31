using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's gameplay HUD, laid out in its 640x480 units (u px each; skin sprites are sd px per
// texel, as everywhere): score/accuracy/progress pie top right, combo counter bottom left, hit error meter
// bottom centre, key overlay on the right edge.
sealed partial class Player
{
    readonly double playStart;
    double shownScore, shownAcc = 100, hudLast = double.NaN;

    // Combo counter: the main number lags behind a pop that plays on every increment.
    int shownCombo;
    string comboText = "0x", popText = "0x";
    double popAt = double.MinValue, bumpAt = double.MinValue, comboTick, comboFadeAt = double.MinValue;
    bool comboShown;

    // Hit error meter.
    readonly List<(float Offset, Color Colour, double At)> meterTicks = [];
    float meterAvg, meterArrowFrom, meterArrowTo;
    double meterArrowAt = double.MinValue, meterLastHit = double.MinValue, meterLastHitSong = double.MinValue;
    static readonly Color Meter300 = new(50, 188, 231, 255), Meter100 = new(87, 227, 19, 255), Meter50 = new(218, 174, 70, 255);

    // Key overlay: K1, K2, M1, M2 (our input bits 1, 2, 4, 8).
    readonly int[] keyCounts = new int[4];
    // HUD text, rebuilt only when the value changes.
    readonly CachedText<long> scoreText = new(v => v.ToString("00000000"));
    readonly CachedText<double> accText = new(v => $"{v:00.00}%"); // stable: two integer digits at least ("00.00%")
    readonly CachedText<int> comboText2 = new(v => $"{v}x");
    readonly CachedText<int>[] keyText = [.. Enumerable.Range(0, 4).Select(_ => new CachedText<int>(v => v.ToString()))];
    readonly NumberText entryNumber = new(v => v.ToString("N0")), entryCombo = new(v => $"{v:N0}x");
    readonly List<(Sprite S, float X)> entryGlyphs = [];
    readonly double[] keyChangedAt = [double.MinValue, double.MinValue, double.MinValue, double.MinValue];
    readonly double[] firstPressAt = new double[4];
    int keysShown, keysEverPressed; // labels give way to counts after the first press, counted or not

    float Meter50Width => map.Hit50 * (float)Config.GetDouble("ScoreMeterScale", 1);

    void MeterHit(double err, int result)
    {
        double half = map.Hit50;
        float clamped = (float)Math.Clamp(err, -half, half);
        float offset = (float)(clamped / half * (Meter50Width / 2)); // units from the centre
        meterAvg = meterAvg * 0.8f + offset * 0.2f;
        meterArrowFrom = CurrentArrow();
        meterArrowTo = meterAvg;
        meterArrowAt = Game.NowMs;
        meterLastHit = Game.NowMs;
        meterLastHitSong = time;
        meterTicks.Add((offset, result == 300 ? Meter300 : result == 100 ? Meter100 : Meter50, Game.NowMs));
    }

    float CurrentArrow()
    {
        float p = (float)Math.Clamp((Game.NowMs - meterArrowAt) / 800, 0, 1);
        return meterArrowFrom + (meterArrowTo - meterArrowFrom) * (1 - (1 - p) * (1 - p));
    }

    void DrawHud(int w, int h)
    {
        float u = h / 480f, sd = h / 768f;
        double now = Game.NowMs;
        double frames = double.IsNaN(hudLast) ? 0 : (now - hudLast) / (1000 / 60.0);
        hudLast = now;
        bool inBreak = InBreak(time) && time > objs[0].Time;
        if (inBreak != hudBreak) { hudBreak = inBreak; hudBreakAt = now; }

        DrawScore(w, u, sd, frames);
        DrawCombo(u, sd, now);
        DrawMeter(w, u, sd, now);
        DrawMods(w, u, sd);
        // stable's "UNRANKED" banner (play-unranked) when the mods keep the play off the rankings: scale 1, centred at y 45.1 (measured).
        if (mods.HasFlag(Mods.Autoplay) || mods.HasFlag(Mods.Relax) || mods.HasFlag(Mods.Autopilot))
            skin.Tex("play-unranked")?.Draw(new Vector2(w / 2f, 45.1f * u), sd, Color.White);
        DrawScoreboard(u);

        DrawReplayOverlay(w, u, now);

        // Drawn over everything above: skins like a full-screen scorebar-bg dim the HUD outside the playfield in
        // stable too. The key overlay stays on top.
        DrawHealthBar(w, sd);
        if (Config.GetBool("KeyOverlay", false) || playback != null || auto) DrawKeyOverlay(w, u, sd, now); // stable always shows it when watching

        DrawSkip();
    }

    // Score rolls toward the real value (x0.75 per 60Hz frame), accuracy eases (x0.5); the pie sits 24 units left
    // of the accuracy and shows song progress (green while counting down to the first object).
    void DrawScore(int w, float u, float sd, double frames)
    {
        shownScore = Math.Abs(shownScore - score) < 0.5 ? score : score + (shownScore - score) * Math.Pow(0.75, frames);
        shownAcc = Math.Abs(shownAcc - Accuracy) < 0.005 ? Accuracy : Accuracy + (shownAcc - Accuracy) * Math.Pow(0.5, frames);
        float scoreScale = 0.96f * sd, accScale = 0.576f * sd, right = w - 6 * u;
        string sc = scoreText.Of((long)Math.Round(shownScore));
        if (!skin.DrawFont(skin.ScorePrefix, skin.ScoreOverlap, sc, right, 0, scoreScale, 1, Color.White, constant: true))
            Game.Text(sc, right - Game.TextWidth(sc, 40 * u / 3), 0, 40 * u / 3, Color.White);
        float glyphH = skin.Glyph(skin.ScorePrefix, '0')?.Height ?? 30;
        float accY = glyphH * scoreScale + (skin.Version > 1 ? 3 * u : 0);
        string acc = accText.Of(Math.Round(shownAcc, 2));
        if (!skin.DrawFont(skin.ScorePrefix, skin.ScoreOverlap, acc, right, accY, accScale, 1, Color.White, constant: true))
            Game.Text(acc, right - Game.TextWidth(acc, 20 * u / 3), accY, 20 * u / 3, Color.White);

        float accW = skin.FontWidth(skin.ScorePrefix, skin.ScoreOverlap, "00.00%", accScale, constant: true);
        DrawBreakGrade(new Vector2(right - Math.Max(0, accW) - 52 * u, accY + glyphH * accScale / 2), sd);
        double first = objs[0].Time, last = objs[^1].EndTime;
        double frac = time < first ? -Math.Clamp((first - time) / Math.Max(1, first - playStart), 0, 1) : Math.Clamp((time - first) / Math.Max(1, last - first), 0, 1);
        string progress = Config.Get("ProgressBarType", "Pie");
        if (progress != "Pie")
        {
            // stable's bars (3 units tall): white at alpha 20 behind, (255,255,128) at half alpha filling from the first
            // object to the end. Top-Right: 170 wide from 178 units in from the right, 2 above the accuracy's bottom;
            // Bottom-Right: the last 118 units of row 460; Bottom: the full width at 477.
            float wu = w / u, accBottom = (accY + glyphH * accScale) / u;
            (float X, float Y, float W) bar = progress switch
            {
                "Bottom" => (0, 477, wu),
                "BottomRight" => (wu - 118, 460, 118),
                _ => (wu - 178, accBottom - 2, 170),
            };
            DrawRectangleRec(new Rectangle(bar.X * u, bar.Y * u, bar.W * u, 3 * u), new Color(255, 255, 255, 20));
            DrawRectangleRec(new Rectangle(bar.X * u, bar.Y * u, bar.W * (float)Math.Max(0, frac) * u, 3 * u), new Color(255, 255, 128, 128));
            return;
        }
        var centre = new Vector2(right - Math.Max(0, accW) - 24 * u, accY + glyphH * accScale / 2);
        var fill = frac < 0 ? new Color(199, 255, 47, 153) : new Color(255, 255, 255, 153);
        if (frac != 0) DrawCircleSector(centre, 10 * u, -90, -90 + 360 * (float)Math.Abs(frac), 48, fill);
        skin.Tex("circularmetre")?.Draw(centre, sd, Color.White); // osu!gameplay.dll
    }

    // stable's grade during breaks (measured at 1080p): ranking-X-small 28 units left of the pie, shown from 350ms
    // into the break until a second before its end, with an echo behind it growing 0.3 a second and fading from 50%.
    void DrawBreakGrade(Vector2 at, float sd)
    {
        foreach (var (start, end) in map.Breaks)
        {
            double t = time - (start + 350);
            if (t < 0 || time > end - 1000) continue;
            if (skin.Tex($"ranking-{Grade()}-small") is not { } g) return;
            if (t < 1000) g.Draw(at, sd * (1 + 0.3f * (float)t / 1000), ColorAlpha(Color.White, 0.5f * (1 - (float)t / 1000)));
            g.Draw(at, sd, Color.White);
            return;
        }
    }

    // stable hides parts of the HUD in breaks: the combo fades out over 1s sliding 80 units left (In),
    // back with Out; the health bar fades over 500ms rising 20 units, its marker growing to 1.6x.
    bool hudBreak;
    double hudBreakAt = double.MinValue;

    void DrawCombo(float u, float sd, double now)
    {
        // Increments one step per update; a break counts the shown combo down at 60 per second.
        if (combo < shownCombo)
        {
            if (combo > 0) shownCombo = combo;
            else if (now - comboTick >= 1000 / 60.0) { shownCombo--; comboTick = now; }
        }
        else if (combo > shownCombo)
        {
            shownCombo++;
            if (comboText != popText) bumpAt = now;
            comboText = popText;
            popAt = now;
        }
        popText = comboText2.Of(shownCombo);
        if (comboText != popText && now - popAt > 160) { comboText = popText; bumpAt = now; }
        if (shownCombo > 0 != comboShown) { comboShown = shownCombo > 0; comboFadeAt = now; }

        float fade = (float)Math.Clamp((now - comboFadeAt) / 120, 0, 1);
        float alpha = comboShown ? fade : 1 - fade;
        float glyphH = (skin.Glyph(skin.ComboPrefix, '0')?.Height ?? 30) * 0.625f;
        // Custom origin (originX, glyph height + 9), as in stable; it scales with the text.
        void Draw(string text, float scale, float originX, Color tint)
        {
            double q = Math.Clamp((now - hudBreakAt) / 1000, 0, 1);
            float dx = -80 * (float)(hudBreak ? Easing.Apply(2, q) : 1 - Easing.Apply(1, q));
            float x = (2 + dx - 0.625f * scale * originX) * u, y = (472 - 0.625f * scale * (glyphH + 9)) * u;
            tint = ColorAlpha(tint, tint.A / 255f * (float)(hudBreak ? 1 - q : q));
            if (!skin.DrawFont(skin.ComboPrefix, skin.ComboOverlap, text, x, y, scale * sd, 0, tint))
                Game.Text(text, x, y, 24 * u * scale / 1.28f, tint);
        }

        double bump = now - bumpAt;
        float mainScale = bump < 50 ? 1.28f + 0.12f * (float)(bump / 50) : bump < 100 ? 1.4f - 0.12f * (float)((bump - 50) / 50) : 1.28f;
        if (alpha > 0) Draw(comboText, mainScale, 0, ColorAlpha(Color.White, alpha));
        double pop = now - popAt;
        if (pop < 300)
        {
            float p = (float)(pop / 300);
            BeginBlendMode(BlendMode.Additive);
            Draw(popText, 2 - 0.72f * p, 3, ColorAlpha(Color.White, 0.6f * (1 - p)));
            EndBlendMode();
        }
    }

    double[]? spinnerStarts;

    void DrawMeter(int w, float u, float sd, double now)
    {
        if (Config.Get("ScoreMeter", "Error") != "Error") return;
        float alpha = now - meterLastHit < 4000 ? 1 : (float)Math.Max(0, 1 - (now - meterLastHit - 4000) / 500);
        // stable also fades it out over 450ms when a spinner starts, until the next hit (measured)
        spinnerStarts ??= objs.Where(o => o.Type == ObjType.Spinner).Select(o => o.Time).ToArray();
        foreach (double s in spinnerStarts)
            if (s <= time && s > meterLastHitSong) { alpha = Math.Min(alpha, 1 - (float)Math.Clamp((time - s) / 450, 0, 1)); break; }
        int kept = 0; // RemoveAll without a per-frame closure
        for (int i = 0; i < meterTicks.Count; i++) if (now - meterTicks[i].At <= 10000) meterTicks[kept++] = meterTicks[i];
        meterTicks.RemoveRange(kept, meterTicks.Count - kept);
        if (alpha <= 0) return;
        float k = (float)Config.GetDouble("ScoreMeterScale", 1);
        var c = new Vector2(w / 2f, (480 - 6 * k) * u);
        void Bar(float width, float height, Color colour) =>
            DrawRectangleRec(new Rectangle(c.X - width * u / 2, c.Y - height * u / 2, width * u, height * u), colour);
        float full = Meter50Width;
        Bar(full, 12 * k, ColorAlpha(Color.Black, 0.6f * alpha));
        Bar(full, 3 * k, ColorAlpha(Meter50, alpha));
        Bar(map.Hit100 / (float)map.Hit50 * full, 3 * k, ColorAlpha(Meter100, alpha));
        Bar(map.Hit300 / (float)map.Hit50 * full, 3 * k, ColorAlpha(Meter300, alpha));
        Bar(1.5f, 12 * k, ColorAlpha(Color.White, alpha));
        BeginBlendMode(BlendMode.Additive);
        foreach (var (offset, colour, at) in meterTicks)
        {
            float a = 0.4f * (1 - (float)((now - at) / 10000)) * alpha;
            DrawRectangleRec(new Rectangle(c.X + offset * u - 0.94f * u, c.Y - 6 * k * u, 1.875f * u, 12 * k * u), ColorAlpha(colour, a));
        }
        EndBlendMode();
        if (skin.Tex("editor-rate-arrow") is { } arrow)
        {
            float sc = 0.6f * k * sd;
            arrow.Draw(new Vector2(c.X + CurrentArrow() * u, c.Y - 3 * u - arrow.Height * sc / 2), sc, ColorAlpha(Color.White, alpha));
        }
    }


    void DrawKeyOverlay(int w, float u, float sd, double now)
    {
        float top = 200 * u;
        if (skin.Tex("inputoverlay-background") is { } bg)
            DrawTexturePro(bg.Tex, new Rectangle(0, 0, bg.Tex.Width, bg.Tex.Height),
                new Rectangle(w, top, bg.Width * 1.05f * sd, bg.Height * sd), Vector2.Zero, 90, Color.White);
        bool counting = !InBreak(time) && !paused && !failed;
        string[] labels = ["K1", "K2", "M1", "M2"];
        for (int i = 0; i < 4; i++)
        {
            bool down = ((auto && playback == null ? autoKeys : inputKeys) & (1 << i)) != 0;
            if (down != ((keysShown & (1 << i)) != 0))
            {
                keysShown ^= 1 << i;
                keyChangedAt[i] = now;
                if (down && (keysEverPressed & (1 << i)) == 0) { keysEverPressed |= 1 << i; firstPressAt[i] = now; }
                if (down && counting && (autoPressed & (1 << i)) == 0) keyCounts[i]++;
            }
            if ((autoPressed & (1 << i)) != 0 && counting) keyCounts[i]++; // each Auto press, even one shorter than a frame
            autoPressed &= (byte)~(1 << i);
            float p = (float)Math.Clamp((now - keyChangedAt[i]) / 160, 0, 1);
            float ease = 1 - (1 - p) * (1 - p);
            float scale = down ? 1 - 0.2f * ease : 0.8f + 0.2f * ease;
            var pressed = i < 2 ? new Color(255, 222, 0, 255) : new Color(248, 0, 158, 255);
            float back = (float)Math.Clamp((now - keyChangedAt[i]) / 100, 0, 1);
            var tint = down ? pressed : ColorLerp(pressed, Color.White, keyChangedAt[i] == double.MinValue ? 1 : back);
            var pos = new Vector2(w - 15 * u, (219 + i * 29.5f) * u);
            skin.Tex("inputoverlay-key")?.Draw(pos, scale * sd, tint);
            // The label (12-unit text) gives way to the count (scoreentry font, overlap 1) over 100ms on the first press.
            bool counted = (keysEverPressed & (1 << i)) != 0;
            float swap = counted ? (float)Math.Clamp((now - firstPressAt[i]) / 100, 0, 1) : 0;
            if (swap < 1)
            {
                float size = 12 * u * scale;
                Game.Text(labels[i], pos.X - Game.TextWidth(labels[i], size) / 2, pos.Y - size / 2, size, ColorAlpha(skin.InputOverlayText, 1 - swap));
            }
            if (swap > 0)
            {
                float gh = (skin.Tex("scoreentry-0")?.Height ?? 0) * sd * scale;
                EntryText(keyText[i].Of(keyCounts[i]), pos.X, pos.Y - gh / 2, scale, false, 1, 0.5f, ColorAlpha(skin.InputOverlayText, swap));
            }
        }
    }

    // ---- stable's in-game leaderboard (left edge, measured from stable captures in 640x480 units): rank #1, the
    // four local scores right above the player's live rank, then the player (live score and combo). Tab toggles.
    // As in stable, panels slide to a new slot over 600ms (Out easing) and fade in/out over 600ms; overtaking a
    // score bursts scoreboard-explosion-1/2 (additive) from the left edge of the player's panel.
    bool scoreboardOn = Config.GetBool("ScoreboardVisible", true);
    float boardAlpha;
    string? mapMd5;
    static readonly object PlayerPanel = new();
    readonly Dictionary<object, (float From, float To, double MovedAt, bool Shown, double ShownAt)> panels = [];
    int boardRank;
    double explodedAt = double.MinValue;
    readonly List<(ReplayInfo? Info, int Rank)> boardRows = [];
    readonly Dictionary<object, (ReplayInfo? Info, int Rank)> boardRanks = [];
    readonly List<object> boardKeys = [];

    double hintFrom = double.NaN;
    // stable cuts names wider than 90 units and adds "..." (cached: drawn every frame).
    readonly Dictionary<(string, float), string> panelNames = [];
    string PanelName(string name, float u)
    {
        if (panelNames.TryGetValue((name, u), out var s)) return s;
        s = name;
        while (s.Length > 1 && Game.LightWidth(s, 14.4f * u) / u > 90) s = s[..^1];
        return panelNames[(name, u)] = s.Length < name.Length ? s + "..." : s;
    }

    void DrawScoreboard(float u)
    {
        if (Input.IsKeyPressed(Config.Key("keyToggleScoreboard", KeyboardKey.Tab))) scoreboardOn = !scoreboardOn;
        // stable's "Hit <key> to toggle scoreboard!" (the bound key) when the board appears: bold 12 just above it, fading out over 6s.
        hintFrom = double.IsNaN(hintFrom) ? Game.NowMs : hintFrom;
        double hintAge = Game.NowMs - hintFrom;
        if (hintAge < 6000 && scoreboardOn && !overlayHidden)
            Game.TextBold($"Hit <{Config.Get("keyToggleScoreboard", "Tab")}> to toggle scoreboard!", 2 * u, 186.3f * u, 12.4f * u, ColorAlpha(Color.White, (float)(1 - hintAge / 6000)));
        // stable: the board fades 0.08 a 60Hz tick, in from 0 at the start, out when hidden or failed.
        boardAlpha = Math.Clamp(boardAlpha + (scoreboardOn && !failed ? 0.08f : -0.08f) * GetFrameTime() * 60, 0, 1);
        if (boardAlpha <= 0 || overlayHidden) return;
        mapMd5 ??= Replays.Md5Of(map.File);
        var others = back.ScoresFor(mapMd5); // includes the replay being watched, as stable lists it
        int rank = 1;
        foreach (var r in others) if (r.Score > score) rank++;
        var rows = boardRows; // reused every frame
        rows.Clear();
        if (rank <= 5)
        {
            for (int i = 0; i < others.Count && rows.Count < 6; i++)
            {
                if (i == rank - 1) rows.Add((null, rank));
                if (rows.Count < 6) rows.Add((others[i], i < rank - 1 ? i + 1 : i + 2));
            }
            if (rows.All(r => r.Info != null) && rows.Count < 6) rows.Add((null, rank));
        }
        else
        {
            rows.Add((others[0], 1));
            for (int i = rank - 5; i <= rank - 2; i++) rows.Add((others[i], i + 1));
            rows.Add((null, rank));
        }
        double now = Game.NowMs;
        if (boardRank > 0 && rank < boardRank) explodedAt = now;
        boardRank = rank;

        bool first = panels.Count == 0; // the opening layout appears in place, like stable's instant first sort
        var ranks = boardRanks;
        ranks.Clear();
        for (int slot = 0; slot < rows.Count; slot++)
        {
            object key = rows[slot].Info ?? PlayerPanel;
            ranks[key] = rows[slot];
            float target = 200.5f + slot * 36.1f;
            if (!panels.TryGetValue(key, out var p)) panels[key] = (target, target, now, true, first ? double.MinValue : now);
            else
            {
                if (p.To != target) p = (PanelY(p, now), target, now, p.Shown, p.ShownAt);
                if (!p.Shown) p = p with { Shown = true, ShownAt = now };
                panels[key] = p;
            }
        }
        boardKeys.Clear();
        boardKeys.AddRange(panels.Keys);
        foreach (var key in boardKeys)
            if (!ranks.ContainsKey(key))
            {
                var p = panels[key];
                if (p.Shown) panels[key] = p with { Shown = false, ShownAt = now };
                else if (now - p.ShownAt > 600) panels.Remove(key);
            }

        string me = watching is { Player.Length: > 0 } wr ? wr.Player : auto && playback == null ? "osu!" : Config.Get("Username", "You"); // stable's Auto player
        float sd = GetScreenHeight() / 768f;
        foreach (var (key, p) in panels)
        {
            float fade = (float)Math.Clamp((now - p.ShownAt) / 600, 0, 1), a = p.Shown ? fade : 1 - fade;
            if (a <= 0) continue;
            var info = key as ReplayInfo;
            int r = ranks.TryGetValue(key, out var row) ? row.Rank : 0;
            float y = PanelY(p, now) * u, anchor = y + 4.65f * u; // stable positions the panel's parts from here
            // stable fades other players' panels to 0.8 - (rank - 1) / count x 0.3; the live player's stays at 1.
            float dim = info != null && r > 0 ? 0.8f - (r - 1) / (float)Math.Max(1, ranks.Count) * 0.3f : 1;
            Color A(Color c) => ColorAlpha(c, c.A / 255f * a * boardAlpha * dim);
            // menu-button-background at 0.62 scale with its origin at texel (470, 20).
            if (skin.Tex("menu-button-background") is { } bg)
            {
                float k = 0.62f * sd;
                DrawTexturePro(bg.Tex, new Rectangle(0, 0, bg.Tex.Width, bg.Tex.Height),
                    new Rectangle(-470 * k, anchor - 20 * k, bg.Width * k, bg.Height * k), Vector2.Zero, 0,
                    A(info != null ? new Color(31, 115, 153, 150) : new Color(250, 250, 250, auto || playback != null ? 40 : 100))); // stable: stored scores blue, the live player light (Auto and replays: measured 40)
            }
            if (r > 0) // every panel shows its rank (stable hides it only on its online personal-best extra panel)
                EntryText(r.ToString(), 82 * u, anchor - 2 * u, 2.2f, false, 3, 1, A(new Color(255, 255, 255, 47))); // measured; a nominal 80 comes out brighter
            Game.TextLight(PanelName(info?.Player is { Length: > 0 } pl ? pl : info == null ? me : "stable", u), 2 * u, y + 2.5f * u, 14.4f * u, A(Color.White)); // Aller Light, measured against stable
            // The live panel counts along: current score and max combo (stable's shows the max, measured watching a replay).
            long panelScore = info?.Score ?? score; // stable shows nothing for a zero score or combo (measured)
            if (panelScore > 0) EntryText(entryNumber.Of(panelScore), 2 * u, anchor + 18 * u, 1, true, 2.5f, 0, A(Color.White));
            int panelCombo = info?.MaxCombo ?? maxCombo;
            if (panelCombo > 0) EntryText(entryCombo.Of(panelCombo), 82 * u, anchor + 18 * u, 1, true, 2.5f, 1, A(new Color(153, 251, 255, 255)));
        }

        // Burst from the panel's anchor + 15 units: -2 stretches 6x wide over 200ms and fades in 400ms; -1 grows
        // 1.3x tall and fades over 700ms.
        double e = now - explodedAt;
        if (e >= 700 || !panels.TryGetValue(PlayerPanel, out var me0)) return;
        float ey = (PanelY(me0, now) + 4.65f + 15) * u;
        BeginBlendMode(BlendMode.Additive);
        void Burst(string name, float sx, float sy, float alpha)
        {
            if (skin.Tex(name) is not { } s || alpha <= 0) return;
            float bw = s.Width * sd * sx, bh = s.Height * sd * sy;
            DrawTexturePro(s.Tex, new Rectangle(0, 0, s.Tex.Width, s.Tex.Height), new Rectangle(0, ey, bw, bh), new Vector2(0, bh / 2), 0, ColorAlpha(Color.White, alpha * boardAlpha));
        }
        float q2 = (float)Easing.Apply(1, e / 200), q1 = (float)Easing.Apply(1, e / 700);
        Burst("scoreboard-explosion-2", 1 + 5 * q2, 1 + 0.2f * q2, 1 - (float)(e / 400));
        Burst("scoreboard-explosion-1", 1, 1 + 0.3f * q1, 1 - (float)(e / 700));
        EndBlendMode();
    }

    static float PanelY((float From, float To, double MovedAt, bool Shown, double ShownAt) p, double now) =>
        p.From + (p.To - p.From) * (float)Easing.Apply(1, (now - p.MovedAt) / 600);

    // stable's scoreentry sprite font: with fixedWidth, digits and 'x' sit centred in "0"-wide cells while
    // punctuation keeps its own width; overlap is in SD texels. alignX 0 = left edge at x, 1 = right.
    void EntryText(string text, float x, float y, float scale, bool fixedWidth, float overlap, float alignX, Color tint)
    {
        float px = GetScreenHeight() / 768f * scale; // pixels per SD texel
        var glyphs = entryGlyphs;
        glyphs.Clear();
        float cell = skin.Tex("scoreentry-0")?.Width ?? 0, pos = 0, lap = 0;
        foreach (char c in text)
        {
            bool punct = c is ',' or '.' or '%';
            if (skin.Glyph("scoreentry", c) is not { } s) continue;
            lap = overlap; // in SD texels: stable measures glyphs at SD size, so @2x skins overlap the same
            if (fixedWidth && !punct) { glyphs.Add((s, pos + Math.Max(0, (cell - s.Width) / 2))); pos += cell - lap; }
            else { glyphs.Add((s, pos)); pos += s.Width - lap; }
        }
        if (!fixedWidth) pos += lap; // stable overlaps only between glyphs here, but after each fixed cell
        float left = x - pos * px * alignX;
        foreach (var (s, gx) in glyphs)
            DrawTexturePro(s.Tex, new Rectangle(0, 0, s.Tex.Width, s.Tex.Height),
                new Rectangle(left + gx * px, y, s.Width * px, s.Height * px), Vector2.Zero, 0, tint);
    }

    // Active mods as the skin's icons, right to left in flag order under the accuracy (NF rightmost, then HD...): scale 1,
    // 49.5 units apart, the first centred 30.2 units in from the
    // right at y 94 (measured on stable Auto and replay plays).
    void DrawMods(int w, float u, float sd)
    {
        modIcons ??= [.. SongSelect.ModIcons.OrderBy(m => m.Flag)
            .Where(m => mods.HasFlag(m.Flag) && !(m.Flag == Mods.DoubleTime && mods.HasFlag(Mods.Nightcore)) && !(m.Flag == Mods.SuddenDeath && mods.HasFlag(Mods.Perfect)))
            .Select(m => skin.Tex("selection-mod-" + m.Icon)).OfType<Sprite>()];
        float x = w - 30.2f * u;
        foreach (var sp in modIcons)
        {
            sp.Draw(new Vector2(x, 94 * u), sd, Color.White);
            x -= 49.5f * u;
        }
    }
    Sprite[]? modIcons; // the mods never change mid-play
}

// Text that's rebuilt only when its value changes (the HUD draws thousands of frames a second).
sealed class CachedText<T>(Func<T, string> format) where T : IEquatable<T>
{
    T last = default!;
    string? text;
    public string Of(T value)
    {
        if (text == null || !last.Equals(value)) { last = value; text = format(value); }
        return text;
    }
}

// Formatted numbers for places that show several values per frame (the leaderboard): cached per value.
sealed class NumberText(Func<long, string> format)
{
    readonly Dictionary<long, string> cache = [];
    public string Of(long value)
    {
        if (cache.TryGetValue(value, out var s)) return s;
        if (cache.Count > 256) cache.Clear(); // the live score keeps changing; keep this bounded
        return cache[value] = format(value);
    }
}
