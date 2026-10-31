using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

sealed partial class Player
{
    // stable's ranking screen. Layout is stable's 640x480 coordinate space (x from the left, or from the right
    // edge for the grade/buttons/mods column); skin sprites are sized for 768px height, like everywhere else.
    // Nested in Player so it reads the finished play directly; the Player (and its outro music) is disposed on leave.
    sealed class Results : Screen
    {
        readonly Player p;
        readonly Skin skin = Game.Skin;
        readonly bool newLayout = Game.Skin.Version > 1;
        readonly string grade, gradeTex, scoreText;
        readonly string[] modIconTex; // the shown mods' sprites, in display order
        readonly bool perfect;
        readonly Vector2[] graph;
        readonly string tooltip;
        readonly (string Sprite, Vector2 Pos, string? Text, float From, float To, Vector2 TextPos, bool TopLeft)[] rows;
        readonly int gradeAt;
        readonly Texture2D bg;
        readonly bool ownsBg;
        double start; // stable's animation clock origin; moved 7s back when the animations are skipped
        float u, sd; // px per 640x480 unit, px per 768-height sprite pixel
        int w, h;

        public Results(Player p)
        {
            this.p = p;
            start = Game.NowMs + 300;
            grade = p.Grade();
            gradeTex = $"ranking-{grade}";
            modIconTex = [.. SongSelect.ModIcons.OrderBy(x => x.Flag)
                .Where(x => p.mods.HasFlag(x.Flag) && !(x.Flag == Mods.DoubleTime && p.mods.HasFlag(Mods.Nightcore)) && !(x.Flag == Mods.SuddenDeath && p.mods.HasFlag(Mods.Perfect)))
                .Select(x => "selection-mod-" + x.Icon)];
            scoreText = p.score.ToString("00000000");
            perfect = p.maxCombo == p.MapMaxCombo;

            int lift = newLayout ? 20 : 8, textDy = newLayout ? -16 : -25;
            var list = new List<(string, Vector2, string?, float, float, Vector2, bool)>();
            void Row(string sprite, float x, float y, int count) => list.Add((sprite, new(x, y), $"{count}x", 1, 0.5f, new(x + 40, y + textDy), false));
            Row("hit300", 40, 160, p.n300);
            Row("hit100", 40, 220, p.n100);
            Row("hit50", 40, 280, p.n50);
            Row("hit300g", 240, 160, p.nGeki);
            Row("hit100k", 240, 220, p.nKatu);
            Row("hit0", 240, 280, p.nMiss);
            list.Add(("ranking-maxcombo", new(5, 320 - lift), $"{p.maxCombo}x", 1, 1, new(15, 330), true));
            list.Add(("ranking-accuracy", new(182, 320 - lift), $"{p.Accuracy:0.00}%", 1, 1, new(194, 330), true));
            if (perfect) list.Add(("ranking-perfect", new(newLayout ? 260 : 200, 430), null, 1.1f, 1, default, false));
            rows = list.ToArray();
            gradeAt = 300 + 300 * rows.Length;

            // HP graph: a stable replay shows the graph stable saved in it, else ours; thinned to <= 100 points the
            // way stable does it (drop every other one from the end).
            var pts = new List<Vector2>(p.perfGraph.Count >= 2 ? p.perfGraph : p.hpGraph);
            if (p.watching is { FromStable: true } w)
                try { if (Replays.OsrLifeBar(w.Path) is { Count: >= 2 } saved) pts = saved; }
                catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { }
            while (pts.Count > 100)
                for (int i = pts.Count - 1; i >= 0 && i < pts.Count; i -= 2) pts.RemoveAt(i);
            graph = pts.ToArray();

            var errs = p.hitErrors;
            var early = errs.Where(e => e < 0).DefaultIfEmpty().Average();
            var late = errs.Where(e => e >= 0).DefaultIfEmpty().Average();
            tooltip = errs.Count == 0 ? "" : $"Accuracy:\nError: {early:0.00}ms - {late:+0.00}ms avg\nUnstable Rate: {p.HitStats().Ur:0.00}";

            // Gameplay already has the background loaded (decoding it again here cost ~80ms on the transition frame);
            // only a storyboard-drawn one (which gameplay skips) is loaded here, and then owned.
            var bgPath = Path.Combine(p.map.Folder, p.map.Background);
            if (p.bg.Id != 0) bg = p.bg;
            else if (p.map.Background.Length > 0 && File.Exists(bgPath))
            {
                bg = LoadTexture(bgPath); ownsBg = true;
                if (bg.Id != 0) SetTextureFilter(bg, TextureFilter.Bilinear);
            }
            skin.Sound("applause")?.Play(BeatmapSamples.EffectVolume);
        }

        ReplayInfo? Replay => p.savedReplay ?? p.watching;
        Vector2 L(float x, float y) => new(x * u, y * u);
        Vector2 R(float x, float y) => new(w - x * u, y * u);
        double T => Game.NowMs - start;

        static float Progress(double t, double from, double ms) => (float)Math.Clamp((t - from) / ms, 0, 1);

        Sprite? Tex(string name) => skin.Tex(name) ?? skin.Tex(name + "-0");

        // origin: (0,0) top-left, (0.5,0.5) centre, (1,0.5) centre-right...
        static Vector2 Centre(Sprite sp, Vector2 pos, Vector2 origin, float scale) =>
            pos + (new Vector2(0.5f) - origin) * new Vector2(sp.Width, sp.Height) * scale;

        void Leave(Screen next)
        {
            if (ownsBg) UnloadTexture(bg); // else gameplay's, freed by p.Dispose
            p.Dispose();
            Game.Screen = next;
        }

        void Back()
        {
            skin.Sound("menuback")?.Play(BeatmapSamples.EffectVolume);
            Leave(p.back);
        }

        void Retry()
        {
            skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume);
            Leave(new Player(p.map, p.back, p.mods, p.playback, p.watching));
        }

        void Watch()
        {
            if (AutoPlay && Replay == null) { skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume); Leave(new Player(p.map, p.back, p.mods)); return; } // Auto again: the same replay
            if (Replay is not { } r) return;
            skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume);
            Leave(new Player(p.map, p.back, r.Mods, Replays.Frames(r), r));
        }

        // First click/key within 5s fast-forwards every animation, like stable.
        bool SkipAnimations()
        {
            if (T >= 5000) return false;
            start = Game.NowMs - 7000;
            return true;
        }

        (Sprite Sp, Rectangle Rect)? Button(string name, float y)
        {
            if ((skin.Tex(name == "retry" ? "ranking-retry" : "ranking-replay") ?? skin.Tex(name == "retry" ? "pause-retry" : "pause-replay")) is not { } sp) return null;
            var c = Centre(sp, R(0, y), new(1, 0.5f), sd);
            return (sp, new Rectangle(c.X - sp.Width * sd / 2, c.Y - sp.Height * sd / 2, sp.Width * sd, sp.Height * sd));
        }

        // Auto (a replay in stable): Watch Replay only.
        bool AutoPlay => p.auto && p.playback == null;
        (Sprite Sp, Rectangle Rect)? RetryButton => p.playback == null && !AutoPlay ? Button("retry", 360) : null;
        (Sprite Sp, Rectangle Rect)? ReplayButton => Replay != null || AutoPlay ? Button("replay", RetryButton != null ? 420 : 360) : null;

        Rectangle BackRect => BackButton.Rect(h);
        readonly BackButton backButton = new();

        public override void Update(double dtMs)
        {
            w = GetScreenWidth(); h = GetScreenHeight();
            u = h / 480f; sd = h / 768f;
            var mouse = Input.GetMousePosition();

            if (Input.IsKeyPressed(KeyboardKey.Escape)) Back();
            else if (Input.IsKeyPressed(KeyboardKey.R) || Input.IsKeyPressed(KeyboardKey.Grave)) Retry();
            else if (Input.IsKeyPressed(KeyboardKey.W)) Watch();
            else if (Input.IsKeyPressed(KeyboardKey.Space) || Input.IsKeyPressed(KeyboardKey.Enter)) SkipAnimations();
            else if (Input.IsMouseButtonPressed(MouseButton.Left))
            {
                if (RetryButton is { } rb && CheckCollisionPointRec(mouse, rb.Rect)) Retry();
                else if (ReplayButton is { } pb && CheckCollisionPointRec(mouse, pb.Rect)) Watch();
                else if (CheckCollisionPointRec(mouse, BackRect)) Back();
                else SkipAnimations();
            }
        }

        public override void Draw()
        {
            if (u == 0) return; // drawn before the first Update
            double t = T;
            var mouse = Input.GetMousePosition();

            if (bg.Id != 0)
            {
                // stable's menu background: 60% bright whatever the dim setting, with the menu parallax.
                bool parallax = Config.GetBool("MenuParallax", true);
                float k = Math.Max(w / (float)bg.Width, h / (float)bg.Height) * (parallax ? 1.01f : 1);
                var shift = parallax ? -(mouse - new Vector2(w / 2f, h / 2f)) / 180 : Vector2.Zero;
                DrawTexturePro(bg, new Rectangle(0, 0, bg.Width, bg.Height),
                    new Rectangle((w - bg.Width * k) / 2 + shift.X, (h - bg.Height * k) / 2 + shift.Y, bg.Width * k, bg.Height * k), Vector2.Zero, 0, new Color(153, 153, 153, 255));
            }

            // Rotating light behind the grade (one turn per 20s).
            if (skin.Tex("ranking-background-overlay") is { } overlay)
            {
                BeginBlendMode(BlendMode.Additive);
                overlay.Draw(R(180, newLayout ? 200 : 170), sd, Color.White, (float)(Game.NowMs / 20000 * 360 % 360));
                EndBlendMode();
            }

            if (skin.Tex("ranking-panel") is { } panel) panel.Draw(Centre(panel, L(0, newLayout ? 64 : 46), Vector2.Zero, sd), sd, Color.White);
            DrawGraph(t, mouse); // under the rows: stable's "Perfect" overlaps it

            // Hit counts, max combo, accuracy, perfect: one every 300ms, sprite shrinks in, number slides in.
            float textScale = 1.12f * sd;
            for (int i = 0; i < rows.Length; i++)
            {
                var (name, pos, text, from, to, textPos, topLeft) = rows[i];
                int at = 300 + 300 * i + (name == "ranking-perfect" ? 300 : 0);
                float a = Progress(t, at, 300);
                if (a > 0 && Tex(name) is { } sp)
                {
                    float sc = (from + (to - from) * a * a) * sd; // ease in
                    sp.Draw(Centre(sp, L(pos.X, pos.Y), topLeft ? Vector2.Zero : new(0.5f), sc), sc, ColorAlpha(Color.White, a));
                }
                float ta = Progress(t, at + 200, 300);
                if (text != null && ta > 0)
                {
                    float slide = 40 * (1 - ta) * (1 - ta); // ease out
                    var tp = L(textPos.X - slide, textPos.Y);
                    if (!skin.DrawFont(skin.ScorePrefix, skin.ScoreOverlap, text, tp.X, tp.Y, textScale, 0, ColorAlpha(Color.White, ta)))
                        Game.Text(text, tp.X, tp.Y, 24 * u, ColorAlpha(Color.White, ta));
                }
            }

            // Score: each digit rolls until its 500ms slot passes.
            string shown = t / 500 >= scoreText.Length ? scoreText // settled: no new string per frame
                : new(scoreText.Select((c, i) => i < t / 500 ? c : (char)('0' + Random.Shared.Next(10))).ToArray());
            float scoreScale = (newLayout ? 1.3f : 1.05f) * sd;
            float glyphH = (skin.Glyph(skin.ScorePrefix, '0')?.Height ?? 0) * scoreScale;
            var sp0 = L(220, 94);
            if (!skin.DrawFont(skin.ScorePrefix, newLayout ? -2 : skin.ScoreOverlap, shown, sp0.X, sp0.Y - glyphH / 2, scoreScale, 0.5f, Color.White, constant: true))
                Game.Text(shown, sp0.X - Game.TextWidth(shown, 30 * u) / 2, sp0.Y - 15 * u, 30 * u, Color.White);

            // Grade: drops in from 2x after the rows; good grades get a fading glow copy.
            float ga = Progress(t, gradeAt, 1000);
            if (ga > 0 && skin.Tex(gradeTex) is { } g)
            {
                var gp = R(120, newLayout ? 200 : 170);
                float gs = (2 - ga * ga) * sd;
                g.Draw(gp, gs, ColorAlpha(Color.White, ga));
                float glow = Progress(t, gradeAt + 1000, 2400);
                if (grade is not ("C" or "D") && glow > 0 && glow < 1)
                {
                    BeginBlendMode(BlendMode.Additive);
                    g.Draw(gp, (1 + 0.05f * glow) * sd, ColorAlpha(Color.White, 1 - glow));
                    EndBlendMode();
                }
            }

            // Mod icons, right to left, 500ms apart.
            for (int m = 0; m < modIconTex.Length; m++)
            {
                float ma = Progress(t, -300 + 500 * m, 400);
                if (ma > 0 && skin.Tex(modIconTex[m]) is { } mi) mi.Draw(R(40 + 20 * m, 260), (2 - ma) * sd, ColorAlpha(Color.White, ma));
            }

            DrawTopBar();

            float bright = 178 / 255f;
            if (RetryButton is { } rb) rb.Sp.Draw(new(rb.Rect.X + rb.Rect.Width / 2, rb.Rect.Y + rb.Rect.Height / 2), sd, ColorAlpha(Color.White, CheckCollisionPointRec(mouse, rb.Rect) ? 1 : bright));
            if (ReplayButton is { } pb) pb.Sp.Draw(new(pb.Rect.X + pb.Rect.Width / 2, pb.Rect.Y + pb.Rect.Height / 2), sd, ColorAlpha(Color.White, CheckCollisionPointRec(mouse, pb.Rect) ? 1 : bright));
            backButton.Draw(h, mouse);

            if (p.watching is { FromStable: true } sr && (sr.N300, sr.N100, sr.N50, sr.NMiss, sr.MaxCombo) != (p.n300, p.n100, p.n50, p.nMiss, p.maxCombo))
            {
                string cmp = $"stable judged {sr.N300}/{sr.N100}/{sr.N50}/{sr.NMiss} {sr.MaxCombo}x; re-judged here {p.n300}/{p.n100}/{p.n50}/{p.nMiss} {p.maxCombo}x";
                Game.Text(cmp, w / 2f - Game.TextWidth(cmp, 12 * u) / 2, h - 16 * u, 12 * u, Color.LightGray);
            }

            // White flash as the grade lands.
            float flash = Progress(t, gradeAt + 1000, 1400);
            if (flash > 0 && flash < 1)
            {
                BeginBlendMode(BlendMode.Additive);
                DrawRectangle(0, 0, w, h, ColorAlpha(Color.White, 0.5f * (1 - flash)));
                EndBlendMode();
            }

            if (tooltipShown) DrawTooltip(mouse);
            SongSelect.DrawCursor();
        }

        bool tooltipShown;

        // HP over the play (green above half, red below), drawn out over 4s; hovering shows the hit error stats.
        void DrawGraph(double t, Vector2 mouse)
        {
            var gpos = L(160, newLayout ? 380 : 360);
            tooltipShown = false;
            if (skin.Tex("ranking-graph") is { } gs)
            {
                gs.Draw(Centre(gs, gpos, Vector2.Zero, sd), sd, Color.White);
                tooltipShown = tooltip.Length > 0 && CheckCollisionPointRec(mouse, new Rectangle(gpos.X, gpos.Y, gs.Width * sd, gs.Height * sd));
            }
            if (graph.Length < 2) return;
            float pad = newLayout ? 5 : 10;
            var box = new Rectangle(gpos.X + pad * u, gpos.Y + pad * u, 186 * u, 86 * u);
            float t0 = graph[0].X, t1 = graph[^1].X;
            Vector2 At(Vector2 v) => new(box.X + (v.X - t0) / Math.Max(1, t1 - t0) * box.Width, box.Y + (1 - v.Y) * box.Height);
            float total = 0;
            for (int i = 1; i < graph.Length; i++) total += Vector2.Distance(At(graph[i - 1]), At(graph[i]));
            float left = total * Progress(t, 0, 4000);
            float thick = 2 * sd;
            for (int i = 1; i < graph.Length && left > 0; i++)
            {
                Vector2 a = At(graph[i - 1]), b = At(graph[i]);
                float len = Vector2.Distance(a, b);
                if (len > left) b = a + (b - a) * (left / len);
                DrawLineEx(a, b, thick, graph[i].Y > 0.5f ? new Color(154, 205, 50, 255) : Color.Red);
                left -= len;
            }
        }

        void DrawTooltip(Vector2 mouse)
        {
            float size = 12 * u;
            var lines = tooltip.Split('\n');
            float tw = lines.Max(l => Game.TextWidth(l, size)) + 8 * u, th = lines.Length * size + 6 * u;
            float x = Math.Min(mouse.X + 12 * u, w - tw), y = mouse.Y + 12 * u;
            DrawRectangleRec(new Rectangle(x, y, tw, th), new Color(0, 0, 0, 200));
            DrawRectangleLinesEx(new Rectangle(x, y, tw, th), 1, Color.Gray);
            for (int i = 0; i < lines.Length; i++) Game.Text(lines[i], x + 4 * u, y + 3 * u + i * size, size, Color.White);
        }

        void DrawTopBar()
        {
            DrawRectangle(0, 0, w, (int)(60 * u), new Color(0, 0, 0, 204));
            var map = p.map;
            string player = p.watching is { Player.Length: > 0 } r ? r.Player : AutoPlay ? "osu!" : Config.Get("Username", Environment.UserName);
            var date = p.watching?.Date ?? DateTime.Now;
            // Measured against stable: its header sits ~3 units in and renders ~3.5% wider than its nominal sizes.
            Game.Text($"{map.Artist} - {map.Title} [{map.Version}]", 3.3f * u, 1 * u, 22.8f * u, Color.White); // stable: no source here (song select shows "Source (Artist)")
            Game.TextRegular($"Beatmap by {map.Creator}", 3.3f * u, 20.7f * u, 16.5f * u, Color.White); // stable: size 16, regular weight
            Game.TextRegular($"Played by {player} on {date}.", 3.3f * u, 34.7f * u, 16.5f * u, Color.White);
            if (skin.Tex("ranking-title") is { } title) title.Draw(Centre(title, R(20, 0), new(1, 0), sd), sd, Color.White);
        }
    }
}
