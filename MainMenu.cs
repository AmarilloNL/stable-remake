using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's main menu: the cookie beats to the song select's preview music over its background. Clicking it
// slides it left and the buttons out from behind it; 6s without mouse movement folds it back. Layout is
// stable's: centre-relative 640x480 units, buttons top-left anchored at x -100, cookie at x -120 when open.
// Offline remake: no editor or multiplayer, so the main tier is Play / Options / Exit and Play goes straight
// to song select (stable's three-button spacing).
sealed class MainMenu : Screen
{
    readonly SongSelect songs;
    static Skin skin => Game.Skin; // not cached: the options screen can swap the skin
    // stable's two tiers at their own offsets: Play opens Solo / Multiplayer / Back. Edit and Multiplayer are shown
    // like stable's but aren't part of this offline remake.
    static readonly (string Name, float Y)[] MainTier = [("play", -125), ("edit", -60), ("options", 5), ("exit", 70)];
    static readonly (string Name, float Y)[] PlayTier = [("freeplay", -92.5f), ("multiplayer", -27.5f), ("back", 37.5f)];
    bool playTier;
    (string Name, float Y)[] Buttons => playTier ? PlayTier : MainTier;

    // Per-frame-safe text: texture names per button, the wrapped tip, info lines and now-playing title, cached.
    static readonly Dictionary<(string, bool), string> buttonTex = [];
    static string ButtonTex(string name, bool over) => buttonTex.TryGetValue((name, over), out var s) ? s
        : buttonTex[(name, over)] = over ? $"menu-button-{name}-over" : "menu-button-" + name;
    List<string>? tipLines;
    (string?, float, int) tipWrap;
    (int, int, int) infoFor = (-1, -1, -1);
    string[] infoLines = [];
    DbBeatmap? npFor;
    string npText = "";
    (string Text, double At) toast = ("", double.MinValue);
    readonly float[] hover = new float[MainTier.Length]; // 0..1 hover progress per button
    int hovered = -1;
    (int Index, double At) flash = (-1, double.MinValue);

    bool open;
    double openChangedAt = double.MinValue, lastActivity, lastUpdate, quitAt = double.NaN;
    float cookieX; // -120..0 units, animated
    float clickShrink; // stable's hover/click scale offset: +0.1 hovered, -0.08 per click
    Vector2 lastMouse;


    public MainMenu(SongSelect songs, bool open = false, bool play = false) // test hooks: --menu-open, --menu-play
    {
        this.songs = songs;
        songs.Menu = this;
        SetOpen(open || play);
        playTier = play;
        LoadBackground();
        lastActivity = Game.NowMs;
        if (Config.GetBool("MenuVoice", true)) skin.Sound("welcome")?.Play(BeatmapSamples.EffectVolume);
    }

    public override void Entered() => NextTip();

    // stable's menu tips (MenuTip_1..30; English text from the osu! wiki, worded as the game shows it): each visit to the menu shows the next
    // one, centred 52 units above the bottom in 16-unit Aller Light, fading in 2.558s after arriving (stable's intro
    // length) over 1s and out 12s later. The counter continues from stable's MenuTip but is saved to remake.cfg.
    static readonly string[] Tips =
    [
        "Add new beatmaps to osu! by simply dragging the .osz file into the window, double-clicking it in Windows Explorer, or choosing \"open\" when downloading in your browser.",
        "You can efficiently navigate large lists of beatmaps in song select by holding left-mouse to drag the list, or right-mouse to quick-seek.",
        "Press F8/F9 anywhere in game to join osu! chat!",
        "Right-clicking on a beatmap or clicking the Beatmap Options button in the song select will show a menu that will allow you to manage the beatmap.",
        "If your game or mouse feels laggy, try toggling the Framerate Limit in Options or using Fullscreen mode/Letterboxing! It might just help!",
        "For fast combos, try toggling between left and right mouse button, or use keyboard alternative Z/X!",
        "If you can't play videos, take a look at the wiki before posting a bug report!",
        "Take a screenshot from within osu! by hitting F12.",
        "Try watching others as they play using spectator mode. Hit F9 to toggle the extended chat interface and utilise this!",
        "When a beatmap has been updated, as long as you are in online ranking mode you will be given a button to update to the latest version.",
        "The wiki has a wealth of information in it. Seriously, read it.",
        "Did you know you can load new beatmaps by simply double-clicking on an osz file or dragging it into a running osu! window?",
        "You can export any replay whether it be online or offline by hitting F2 at the ranking screen.",
        "If you find that the 'beats' sound too early or late in every beatmap you play, try adjusting the Universal Offset in Options.",
        "The fastest way to find a beatmap is to simply type part of its title/artist/creator directly into the song select screen.",
        "Chat can be set to automatically close during gameplay. See the toggle-buttons located at the bottom-right of the screen when chat is open.",
        "Adjust the volume in-game using the mouse-wheel. This can be disabled in options if you wish.",
        "Pause/unpause the game quickly by hitting the middle mouse-button. This can be disabled in options if you wish.",
        "You can gain quick access to web links by clicking the chat-bubble icon in song select, just above the score listings.",
        "Customize your osu! experience with new skins! Download them from the Skinning forum.",
        "Hit F8 at any time to activate the in-game chat window. You can check out the commands available in chat by typing !help or /help",
        "Make sure to take breaks every so often. Your hands have to serve you in life for more than just osu! spinners.",
        "Don't ignore unranked maps. If you find some you like and leave constructive comments, there is a much higher chance of them being ranked!",
        "You can play songs together with other players in multiplayer mode!",
        "Ever wondered where someone was from? Hit F9 and hover their user panel!",
        "Hit F5 at the song select screen to refresh and load any new songs from disk.",
        "Hit Shift+F12 anywhere in-game to take a screenshot and automatically upload it to the internet.",
        "You can customise the keys you use to play osu! under Options > Input.",
        "You can search for specific conditions at song select, like \"ar>8 od=9\". Supported keywords are: ar, od, hp, cs, bpm, length and drain (time in seconds).",
        "Quickly retry a beatmap by holding Ctrl+R for a short time! You can also use a custom key (see Quick Retry in Keyboard Bindings).",
    ];
    string? tip;
    double tipFrom;

    void NextTip()
    {
        tip = null;
        if (!Config.GetBool("ShowMenuTips", true)) return;
        int n = Math.Abs(Config.GetInt("MenuTip", 0)) % Tips.Length;
        tip = Tips[n];
        Config.SetOwn("MenuTip", ((n + 1) % Tips.Length).ToString());
        tipFrom = Game.NowMs + 2558;
    }

    // stable's Now Playing controls (y measured): FontAwesome icons 20 units apart from 136 to 16 units
    // in from the right edge, growing to 1.2x on hover over 100ms, and a 134x3-unit seek bar (additive track and
    // fill) 143 units in at y 40. Each click plays a sound and shows a notice in the centre band.
    // TODO: the notice wording is from memory (stable's exact text is unknown); the list button does nothing yet.
    static readonly int[] MusicIcons = [0xf048, 0xf04b, 0xf04c, 0xf04d, 0xf051, 0xf129, 0xf0c9];
    readonly float[] musicHover = new float[MusicIcons.Length];

    // The controls drop 20 units over 400ms on each song change and go back up 7.6-8s later (unless
    // PermanentSongInfo), so they sit in the top bar once the title has gone.
    double songChangedAt = double.MinValue;
    float layer = 1, infoAlpha;
    string songKey = "";
    static bool PermanentInfo => Config.GetBool("PermanentSongInfo", false);
    float ControlsDrop
    {
        get
        {
            double t = Game.NowMs - songChangedAt;
            return 20 * (float)(Math.Clamp(t / 400, 0, 1) - (PermanentInfo ? 0 : Math.Clamp((t - 7600) / 400, 0, 1)));
        }
    }
    float ControlsY => (9.7f + ControlsDrop) * u;

    Rectangle MusicButtonRect(int i) => new(w - (136 - 20 * i + 9) * u, ControlsY - 9 * u, 18 * u, 18 * u);
    Rectangle SeekRect => new(w - 143 * u, ControlsY + 10.3f * u, 134 * u, 3 * u);

    bool UpdateMusicControls(Vector2 mouse, bool click, double dtMs)
    {
        string key = songs.Current?.Md5 ?? "";
        if (key != songKey) { songKey = key; songChangedAt = Game.NowMs; }
        // stable's keys in the menu: Z/Left previous, X play, C pause, V/Right next, R random.
        int keyed = Input.IsKeyPressed(KeyboardKey.Z) || !VolumeControl.Shown && Input.IsKeyPressed(KeyboardKey.Left) ? 0 : Input.IsKeyPressed(KeyboardKey.X) ? 1
            : Input.IsKeyPressed(KeyboardKey.C) ? 2 : Input.IsKeyPressed(KeyboardKey.V) || !VolumeControl.Shown && Input.IsKeyPressed(KeyboardKey.Right) ? 4 : -1;
        if (Input.IsKeyPressed(KeyboardKey.R)) { songs.MusicNext(); skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume); }
        int over = -1;
        for (int i = 0; i < MusicIcons.Length; i++)
        {
            bool on = CheckCollisionPointRec(mouse, MusicButtonRect(i));
            if (on) over = i;
            musicHover[i] = Math.Clamp(musicHover[i] + (float)(on ? dtMs : -dtMs) / 100, 0, 1);
        }
        if (keyed >= 0) { MusicAction(keyed); return false; }
        if (!click) return false;
        var seek = SeekRect;
        if (CheckCollisionPointRec(mouse, new Rectangle(seek.X, seek.Y - 3 * u, seek.Width, seek.Height + 6 * u)))
        {
            songs.MusicSeek(Math.Clamp((mouse.X - seek.X) / seek.Width, 0, 1));
            return true;
        }
        if (over < 0) return false;
        MusicAction(over);
        return true;
    }

    // Info: stable's PermanentSongInfo toggle (ours saved to remake.cfg); the title comes back either way.
    string ToggleSongInfo()
    {
        bool on = !PermanentInfo;
        Config.SetOwn("PermanentSongInfo", on ? "1" : "0");
        songChangedAt = Game.NowMs;
        return on ? "Song info will be displayed permanently." : "Song info will be displayed temporarily.";
    }

    void MusicAction(int over)
    {
        string? notice = over switch
        {
            0 => "<< Prev",
            1 => "Play",
            2 => songs.MusicPause() ? "Pause" : "Unpause",
            3 => "Stop playing",
            4 => ">> Next",
            5 => ToggleSongInfo(),
            _ => null,
        };
        if (over == 0) songs.MusicPrevious();
        else if (over == 1) songs.MusicPlay();
        else if (over == 3) songs.MusicStop();
        else if (over == 4) songs.MusicNext();
        skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
        if (notice != null) Notification.Show(notice, 1000);
    }

    void DrawMusicControls()
    {
        for (int i = 0; i < MusicIcons.Length; i++)
        {
            float e = 1 - (1 - musicHover[i]) * (1 - musicHover[i]);
            float size = 12 * u * (1 + 0.2f * e), x = w - (136 - 20 * i) * u, drop = Math.Max(1, u / 3); // 240 with a drop shadow (measured)
            Options.DrawIcon(MusicIcons[i], x, ControlsY + drop, size, ColorAlpha(new Color(0, 0, 0, 128), layer));
            Options.DrawIcon(MusicIcons[i], x, ControlsY, size, ColorAlpha(new Color(240, 240, 240, 255), layer));
        }
        var seek = SeekRect;
        BeginBlendMode(BlendMode.Additive);
        DrawRectangleRec(seek, ColorAlpha(new Color(20, 20, 20, 128), layer));
        if (songs.Music is { } m && m.LengthMs > 0)
            DrawRectangleRec(seek with { Width = seek.Width * (float)Math.Clamp(songs.MusicMs / m.LengthMs, 0, 1) }, ColorAlpha(new Color(255, 255, 255, 128), layer));
        EndBlendMode();
    }

    void DrawTip(double now)
    {
        if (tip == null) return;
        double t = now - tipFrom;
        float a = (float)Math.Clamp(Math.Min(t / 1000, 1 - (t - 12000) / 500), 0, 1);
        if (a <= 0) return;
        float size = 16.7f * u; // stable says 16; its text renders this wide (measured)
        if (tipLines == null || tipWrap != (tip, size, w)) // wrapped once per tip/size, not every frame
        {
            var wrapped = new List<string>();
            foreach (var word in tip.Split(' '))
            {
                if (wrapped.Count > 0 && Game.LightWidth(wrapped[^1] + " " + word, size) <= w) wrapped[^1] += " " + word;
                else wrapped.Add(word);
            }
            tipLines = wrapped;
            tipWrap = (tip, size, w);
        }
        var lines = tipLines;
        for (int i = 0; i < lines.Count; i++)
            Game.TextLight(lines[i], (w - Game.LightWidth(lines[i], size)) / 2, h - 52 * u + i * size, size, ColorAlpha(Color.White, a * layer));
    }

    // stable's menu background: one of its cached seasonal backgrounds (Data\bg) unless SeasonalBackgrounds is
    // Never, else the skin's / default menu-background, else (no texture) song select's map background.
    Texture2D background;

    void LoadBackground()
    {
        var seasonal = Path.Combine(Config.OsuDir, "Data", "bg");
        var files = Config.Get("SeasonalBackgrounds", "Sometimes") != "Never" && Directory.Exists(seasonal)
            ? Directory.GetFiles(seasonal, "*.jpg") : [];
        if (files.Length > 0) background = LoadTexture(files[Random.Shared.Next(files.Length)]);
        // No skin background: one of stable's built-in ones (JPEGs in osu!ui.dll), picked at random.
        else if (!skin.Own("menu-background") && DefaultSkin.Get($"menu-background-{Random.Shared.Next(1, 8)}@2x") is { Length: > 2 } jpg && jpg[0] == 0xFF
            && LoadImageFromMemory(".jpg", jpg) is { Width: > 0 } img)
        {
            background = LoadTextureFromImage(img);
            UnloadImage(img);
        }
        if (background.Id != 0) SetTextureFilter(background, TextureFilter.Bilinear);
    }

    float u, sd;
    int w, h;
    // stable's menu parallax (MenuParallax): the cookie, buttons and visualizer move against the cursor by 1/60 of its offset
    // from the centre in 640x480 units, the background by 1/180 (scaled 1.01 to hide its edges).
    static bool ParallaxOn => Config.GetBool("MenuParallax", true);
    Vector2 CursorOffset => ParallaxOn ? (Input.GetMousePosition() - new Vector2(w / 2f, h / 2f)) / u : Vector2.Zero;
    Vector2 Centre => new Vector2(w / 2f, h / 2f) - CursorOffset / 60 * u;

    Rectangle ButtonRect(int i)
    {
        var sp = skin.Tex("menu-button-" + Buttons[i].Name);
        float bw = (sp?.Width ?? 583) * sd, bh = (sp?.Height ?? 89) * sd;
        var p = Centre + new Vector2(-100, Buttons[i].Y) * u;
        return new Rectangle(p.X, p.Y, bw, bh);
    }

    float CookieRadius => (skin.Tex("menu-osu")?.Width ?? 540) * sd / 2;
    Vector2 CookiePos => Centre + new Vector2(cookieX * u, 0);

    void SetOpen(bool o)
    {
        if (open == o) return;
        open = o;
        openChangedAt = Game.NowMs;
        if (!o) playTier = false; // collapsing returns to the main tier
    }

    // Switching tiers slides the new row in like opening does.
    void SetTier(bool play)
    {
        playTier = play;
        openChangedAt = Game.NowMs;
        Array.Clear(hover);
        hovered = -1;
    }

    void Activate(int i)
    {
        string name = Buttons[i].Name;
        (skin.Sound($"menu-{name}-click") ?? skin.Sound("menuhit"))?.Play(BeatmapSamples.EffectVolume);
        flash = (i, Game.NowMs);
        switch (name)
        {
            case "play": SetTier(true); break;
            case "freeplay": Game.Screen = songs; break;
            case "back": SetTier(false); break;
            case "edit": toast = ("The beatmap editor isn't part of this offline remake.", Game.NowMs); break;
            case "multiplayer": toast = ("Multiplayer isn't part of this offline remake.", Game.NowMs); break;
            case "options": Game.Screen = new Options(this, songs); break;
            case "exit":
                if (Config.GetBool("MenuVoice", true)) skin.Sound("seeya")?.Play(BeatmapSamples.EffectVolume);
                quitAt = Game.NowMs + 1500; // fade out under the farewell, like stable
                break;
        }
    }

    public override void Update(double dtMs)
    {
        w = GetScreenWidth(); h = GetScreenHeight();
        u = h / 480f; sd = h / 768f;
        songs.Ambient(menu: true);
        if (Game.NowMs - lastUpdate > 250) lastActivity = Game.NowMs; // back from another screen: not idle
        lastUpdate = Game.NowMs;
        coverAlpha = Math.Min(1, coverAlpha + (float)(dtMs / 200));
        UpdateVisuals(dtMs);
        if (!double.IsNaN(quitAt))
        {
            if (Game.NowMs >= quitAt) Game.Quit = true;
            return;
        }
        if (!double.IsNaN(exitAskedAt)) { UpdateExitDialog(); return; }

        var mouse = Input.GetMousePosition();
        bool moved = Vector2.Distance(mouse, lastMouse) > 5;
        if (moved) lastActivity = Game.NowMs;
        lastMouse = mouse;
        // stable: the music controls, Now Playing, tip and info layer returns quickly on activity and fades to 1%
        // over ~16s once the mouse has been still for 6s; the info lines also fade in/out (500ms) with the menu.
        double ticks = dtMs / (1000 / 60.0);
        layer = moved || Game.NowMs - lastActivity < 6000 ? Math.Min(1, layer + 0.08f * (float)ticks) : Math.Max(0.01f, layer - 0.001f * (float)ticks);
        infoAlpha = Math.Clamp(infoAlpha + (open ? 1 : -1) * (float)(dtMs / 500), 0, 1);
        if (open && Game.NowMs - lastActivity > 6000) SetOpen(false);

        // Cookie slides left in 400ms when opening, drifts home over 2s when closing.
        float target = open ? -120 : 0;
        float k = (float)Math.Min(1, dtMs / (open ? 100 : 500));
        cookieX += (target - cookieX) * k;
        // stable: hovering grows the cookie to +0.1 and a click dents it by 0.08, both at 0.012 per 60Hz frame.
        bool overCookie = Vector2.Distance(mouse, CookiePos) <= CookieRadius * CookieScale;
        clickShrink = overCookie || clickShrink < 0 ? Math.Min(clickShrink + 0.012f * (float)ticks, 0.1f) : Math.Max(clickShrink - 0.012f * (float)ticks, 0);

        int now = -1;
        for (int i = 0; i < Buttons.Length; i++)
        {
            bool over = open && CheckCollisionPointRec(mouse, ButtonRect(i)) && Vector2.Distance(mouse, CookiePos) > CookieRadius;
            if (over) now = i;
            hover[i] = Math.Clamp(hover[i] + (float)(over ? dtMs / 580 : -dtMs / 400), 0, 1);
        }
        if (now != hovered && now >= 0) skin.Sound("menuclick")?.Play(BeatmapSamples.EffectVolume);
        hovered = now;

        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (click) lastActivity = Game.NowMs;
        if (UpdateMusicControls(mouse, click, dtMs)) click = false;
        if (click && Vector2.Distance(mouse, CookiePos) <= CookieRadius)
        {
            clickShrink -= 0.08f;
            if (open) Activate(0); // the cookie is the Play button once the menu is open
            else { skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume); SetOpen(true); }
        }
        else if (click && hovered >= 0) Activate(hovered);

        if (Input.IsKeyPressed(KeyboardKey.P) || Input.IsKeyPressed(KeyboardKey.Enter)) { lastActivity = Game.NowMs; if (open) Activate(0); else SetOpen(true); }
        else if (playTier && Input.IsKeyPressed(KeyboardKey.M)) Activate(1);
        else if (playTier && (Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsKeyPressed(KeyboardKey.B))) Activate(2);
        else if (playTier) { }
        else if (Input.IsKeyPressed(KeyboardKey.E)) Activate(1);
        else if (Input.IsKeyPressed(KeyboardKey.O)) Activate(2);
        else if (Input.IsKeyPressed(KeyboardKey.Q)) Activate(3);
        else if (Input.IsKeyPressed(KeyboardKey.Escape))
        {
            if (open) SetOpen(false);
            else exitAskedAt = Game.NowMs; // stable asks first
        }
    }



    // ---- stable's visualizer: 1024 FFT bars wrapped four times round the cookie. Each bar keeps its peak,
    // decaying 5% per 60Hz frame; every 10ms the bin->bar mapping rotates by 50, which makes it swirl.
    readonly float[] fft = new float[1024], bars = new float[1024];
    double visAcc;
    int visOffset;
    (double RedTime, long Index) lastBeat = (double.NaN, 0);
    readonly List<(double At, float Scale)> shockwaves = [];
    (double At, float Peak, double Ms) flashLeft, flashRight;

    // stable's cookie pulse: eases (x0.5 per 60Hz tick) toward 1 - (beat phase + 1 - (level - average level) / 0.5) / 2,
    // where level = (L + R peak) / 65536 x music volume and the average follows it by 10% a tick; the cookie is
    // 1.05 - 0.05 x pulse (+ hover/click), its glowing copy 1.05 + 0.03 x pulse at 0.4 x (1 - pulse) (0.1 in kiai).
    float cookiePulse, levelAvg, glowLevel = 1;
    float CookieScale => 1.05f - 0.05f * cookiePulse + clickShrink;

    void UpdatePulse(double dtMs)
    {
        double ticks = dtMs / (1000 / 60.0);
        int level = 32768;
        if (songs.Music is { } m) { var (l, r) = m.Level; level = l + r; }
        float now = level / 65536f * (float)(Config.GetDouble("VolumeMusic", 100) / 100);
        float a = (float)Math.Pow(0.9, ticks);
        levelAvg = levelAvg * a + now * (1 - a);
        double phase = songs.BeatPhase() is var p && !double.IsNaN(p) ? p : 0.5;
        float target = Math.Clamp(1 - ((float)phase * 0.5f + (1 - (now - levelAvg) / 0.5f) * 0.5f), 0, 1);
        float k = (float)Math.Pow(0.5, ticks);
        cookiePulse = cookiePulse * k + target * (1 - k);
        float g = songs.Beat() is { Kiai: true } ? 1 : 0.6f + Math.Clamp((level - 30000) / 35536f, 0, 1) * 0.4f, gk = (float)Math.Pow(0.8, ticks);
        glowLevel = glowLevel * gk + g * (1 - gk);
    }

    // stable's kiai fountain: when the menu song's kiai starts (at most every 500ms, and only within 500ms of the timing
    // point that turned it on, so a song starting mid-kiai throws none), 100 star2s from each bottom corner
    // (100 units in, 490 down), 10ms apart, thrown up (700-800 units/s) and fanned sideways - all one way, the other way
    // or straight up at random - falling with gravity 14.4 a 60Hz tick, bouncing off the screen's sides; each, added on,
    // fades out while growing to 1..2.8 and turning between two random angles over 300-1300ms (all eased Out; both stars
    // of a pair share their life, scale and turn).
    sealed class MenuStar { public Vector2 Pos, Vel; public double Born, Life; public float Scale, Rot0, Rot1; }
    readonly List<MenuStar> menuStars = [];
    bool menuKiaiWas = true;
    double menuFountainAt = double.MinValue, menuStarTick = double.NaN;

    void UpdateMenuStars(double now)
    {
        var beat = songs.Beat();
        bool kiai = beat is { Kiai: true };
        if (kiai && !menuKiaiWas && now - menuFountainAt > 500 && Math.Abs(songs.MusicMs - beat!.Value.PointTime) < 500)
        {
            menuFountainAt = now;
            var rnd = Random.Shared;
            int dir = rnd.Next(-1, 2);
            float wu = GetScreenWidth() / (GetScreenHeight() / 480f);
            for (int i = 0; i < 100; i++)
            {
                double life = rnd.Next(300, 1300);
                float scale = 1 + rnd.NextSingle() * 1.8f, rot0 = rnd.NextSingle() * 4 - 2, rot1 = rnd.NextSingle() * 4 - 2;
                foreach (int side in (int[])[1, -1])
                    menuStars.Add(new MenuStar
                    {
                        Born = now + i * 10, Life = life,
                        Pos = new Vector2(side > 0 ? 100 : wu - 100, 490),
                        Vel = new Vector2(dir * (i - 50) / 50f * 450 * side + rnd.Next(-50, 50), rnd.Next(-800, -700)),
                        Scale = scale, Rot0 = rot0, Rot1 = rot1,
                    });
            }
        }
        menuKiaiWas = kiai;
        if (double.IsNaN(menuStarTick)) menuStarTick = now;
        float w = GetScreenWidth() / (GetScreenHeight() / 480f);
        for (; now - menuStarTick >= 1000 / 60.0; menuStarTick += 1000 / 60.0)
            foreach (var s in menuStars)
            {
                if (now < s.Born) continue;
                s.Vel.Y += 8 * 1.8f;
                s.Pos += s.Vel * (1 / 60f);
                if (s.Pos.X < 0 && s.Vel.X < 0 || s.Pos.X > w && s.Vel.X > 0) s.Vel.X = -s.Vel.X;
            }
        menuStars.RemoveAll(s => now - s.Born >= s.Life);
    }

    void DrawMenuStars(double now)
    {
        if (menuStars.Count == 0 || skin.Tex("star2") is not { } tex) return;
        float u = GetScreenHeight() / 480f, sd = GetScreenHeight() / 768f;
        BeginBlendMode(BlendMode.Additive);
        foreach (var s in menuStars)
        {
            if (now < s.Born) continue;
            float f = (float)Easing.Apply(1, (now - s.Born) / s.Life);
            tex.Draw(s.Pos * u, sd * (1 + (s.Scale - 1) * f), ColorAlpha(Color.White, 1 - f), (s.Rot0 + (s.Rot1 - s.Rot0) * f) * 180 / MathF.PI);
        }
        EndBlendMode();
    }

    void UpdateVisuals(double dtMs)
    {
        UpdatePulse(dtMs);
        if (songs.Music is { } music) music.Fft(fft);
        else Array.Clear(fft);
        double left = Math.Min(dtMs, 1000);
        while (left > 0)
        {
            visAcc += left;
            double over = Math.Max(0, visAcc - 10);
            left -= over;
            double decay = Math.Pow(0.95, left / (1000 / 60.0));
            for (int n = 1023; n >= 0; n--)
            {
                ref float bar = ref bars[1023 - n];
                bar = (float)(Math.Max(bar, fft[(n + visOffset) % 1024] * 3) * decay);
                if (bar < 0.01f) bar = 0;
            }
            if (over == 0) break;
            visAcc = 0;
            visOffset = (visOffset + 50) % 1024;
            left = over;
        }

        if (songs.Beat() is not { } b || (b.RedTime, b.Index) == lastBeat) return;
        lastBeat = (b.RedTime, b.Index);
        double now = Game.NowMs;
        shockwaves.Add((now, CookieScale));
        if (Vector2.Distance(Input.GetMousePosition(), CookiePos) <= CookieRadius) skin.Sound("heartbeat")?.Play(BeatmapSamples.EffectVolume);
        // Side flashes: every beat in kiai (alternating sides), otherwise on downbeats; brightness from the music.
        if (songs.Music is { } m && (b.Kiai || b.Index > 0 && b.Index % b.Meter == 0))
        {
            var (l, r) = m.Level;
            static float Peak(int level) => Math.Clamp((2f * level - 20000) / 45536, 0, 1) / 2;
            double ms = Math.Max(300, b.Length);
            if (!b.Kiai || b.Index % 2 == 0) flashLeft = (now, Peak(l), ms);
            if (!b.Kiai || b.Index % 2 == 1) flashRight = (now, Peak(r), ms);
        }
    }

    static float FlashAlpha((double At, float Peak, double Ms) f, double now)
    {
        double t = now - f.At;
        if (t < 64) return f.Peak * (float)(t / 64);
        double p = Math.Clamp((t - 64) / (f.Ms - 64), 0, 1);
        return f.Peak * (float)(1 - p * p);
    }

    void DrawVisuals(double now)
    {
        BeginBlendMode(BlendMode.Additive);
        if (skin.Tex("menu-flash") is { } fl)
        {
            float fw = fl.Width * sd, fh = h; // stretched to full height like stable's 4x
            var src = new Rectangle(0, 0, fl.Tex.Width, fl.Tex.Height);
            float a = FlashAlpha(flashLeft, now);
            if (a > 0) DrawTexturePro(fl.Tex, src with { Width = -fl.Tex.Width }, new Rectangle(0, 0, fw, fh), Vector2.Zero, 0, ColorAlpha(Color.White, a));
            a = FlashAlpha(flashRight, now);
            if (a > 0) DrawTexturePro(fl.Tex, src, new Rectangle(w - fw, 0, fw, fh), Vector2.Zero, 0, ColorAlpha(Color.White, a));
        }

        if (skin.Tex("menu-vis") is { } vis)
        {
            var centre = CookiePos;
            float radius = CookieScale * 150 * u, bw = vis.Width * sd, bh = vis.Height * sd * 0.5f;
            var src = new Rectangle(0, 0, vis.Tex.Width, vis.Tex.Height);
            for (int i = 0; i < bars.Length; i++)
            {
                float len = bars[i];
                if (len == 0) continue;
                float alpha = 0.4f * Math.Min(1, (len - 0.04f) / 0.08f);
                if (alpha <= 0) continue;
                float angle = MathF.PI * 2 * (0.4f + i / 1024f * 4);
                var p = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                DrawTexturePro(vis.Tex, src, new Rectangle(p.X, p.Y, bw * len, bh), new Vector2(0, bh / 2), angle * 180 / MathF.PI, ColorAlpha(skin.MenuGlow, alpha));
            }
        }

        // Beat shockwave: a faint disc growing to 1.4x over a second.
        int kept = 0; // RemoveAll without a per-frame closure
        for (int i = 0; i < shockwaves.Count; i++) if (now - shockwaves[i].At < 1000) shockwaves[kept++] = shockwaves[i];
        shockwaves.RemoveRange(kept, shockwaves.Count - kept);
        if (skin.Tex("menu-osu-shockwave") is { } sw)
            foreach (var (at, s0) in shockwaves)
            {
                float t = (float)((now - at) / 1000);
                sw.Draw(CookiePos, s0 * (1 + 0.4f * (1 - (1 - t) * (1 - t))) * sd, ColorAlpha(Color.White, 0.1f * (1 - t)));
            }
        EndBlendMode();
    }

    // Covered by the options panel: stable keeps the cookie and visualizer going but hides the bars, info, user panel,
    // music controls and tip (faded over 200ms here).
    float coverAlpha = 1;
    internal void UpdateCovered(double dtMs)
    {
        coverAlpha = Math.Max(0, coverAlpha - (float)(dtMs / 200));
        UpdateVisuals(dtMs);
    }

    public override void Draw()
    {
        float idleLayer = layer;
        layer *= coverAlpha;
        try { DrawMenu(); } finally { layer = idleLayer; }
    }

    void DrawMenu()
    {
        w = GetScreenWidth(); h = GetScreenHeight();
        u = h / 480f; sd = h / 768f;
        double now = Game.NowMs;
        // Looked up per frame (not kept), so swapping skins in the options can't leave a freed texture here.
        var bgTex = background.Id != 0 ? background : skin.Tex("menu-background")?.Tex ?? default;
        if (bgTex.Id != 0)
        {
            float k = Math.Max(w / (float)bgTex.Width, h / (float)bgTex.Height) * (ParallaxOn ? 1.01f : 1);
            var shift = -CursorOffset / 180 * u;
            DrawTexturePro(bgTex, new Rectangle(0, 0, bgTex.Width, bgTex.Height),
                new Rectangle((w - bgTex.Width * k) / 2 + shift.X, (h - bgTex.Height * k) / 2 + shift.Y, bgTex.Width * k, bgTex.Height * k), Vector2.Zero, 0, Color.White);
        }
        else songs.DrawBackground(w, h);

        // stable's black bars top and bottom (54 units, 40%).
        DrawRectangle(0, 0, w, (int)(54 * u), new Color(0, 0, 0, (int)(102 * coverAlpha)));
        DrawRectangle(0, h - (int)(54 * u), w, (int)(54 * u), new Color(0, 0, 0, (int)(102 * coverAlpha)));
        // stable's info lines, top left of the bar (size 14 at x 210).
        double up = Game.NowMs / 1000;
        var clock = DateTime.Now;
        var infoKey = (SongSelect.TotalBeatmaps, up < 60 ? (int)up : 1000 + (int)(up / 60), clock.Hour * 60 + clock.Minute);
        if (infoKey != infoFor) // rebuilt only when a number changes, not every frame
        {
            infoFor = infoKey;
            string running = up < 60 ? $"{(int)up} seconds" : $"{(int)(up / 60)} minutes";
            infoLines = [$"You have {SongSelect.TotalBeatmaps} beatmaps available!", $"osu! has been running for {running}.", $"It is currently {clock.ToShortTimeString()}."];
        }
        var info = infoLines;
        if (infoAlpha * layer > 0)
            for (int i = 0; i < info.Length; i++) Game.Text(info[i], 211.3f * u, (i * 13.7f - 0.1f) * u, 14.45f * u, ColorAlpha(Color.White, infoAlpha * layer));
        DrawTip(now);
        DrawMusicControls();
        SongSelect.DrawUserPanel(53.1f, 0.6f, layer);
        if (now - toast.At < 3000)
        {
            float a = (float)Math.Min(1, (3000 - (now - toast.At)) / 500);
            float tw = Game.TextWidth(toast.Text, 14 * u);
            DrawRectangleRec(new Rectangle(w / 2f - tw / 2 - 8 * u, h - 90 * u, tw + 16 * u, 22 * u), ColorAlpha(Color.Black, 0.7f * a));
            Game.Text(toast.Text, w / 2f - tw / 2, h - 86 * u, 14 * u, ColorAlpha(Color.White, a));
        }
        if (songs.Current is { } b)
        {
            // stable's "Now Playing" header: the song title 10 units in from the right edge with the menu-np sprite
            // anchored 100 texels left of it, sliding in from 90 units further right and fading in over 1s on each
            // song change; unless PermanentSongInfo is set they leave the same way 6-8s later.
            if (npFor != b) { npFor = b; npText = $"{b.Artist} - {b.Title}"; }
            string np = npText;
            float npSize = 14 * u, npW = Game.LightWidth(np, npSize);
            double t = now - songChangedAt;
            float into = (float)Easing.Apply(1, t / 1000), outOf = PermanentInfo ? 0 : (float)Easing.Apply(1, (t - 6000) / 2000);
            float slide = (1 - into + outOf) * 90 * u, alpha = Math.Clamp(into - outOf, 0, 1) * layer;
            float npX = w - 13 * u - npW + slide; // 10 units plus stable's text padding (measured)
            if (alpha > 0)
            {
                if (skin.Tex("menu-np") is { } npSprite)
                    npSprite.Draw(new Vector2(npX - 100 * sd + npSprite.Width * sd / 2, npSprite.Height * sd / 2), sd, ColorAlpha(Color.White, alpha));
                Game.TextLight(np, npX, 2 * u, npSize, ColorAlpha(Color.White, alpha));
            }
        }
        if (skin.Tex("menu-copyright") is { } copyright) // ppy logo, bottom left
            copyright.Draw(new Vector2(2 * u + copyright.Width * sd / 2, h - 2 * u - copyright.Height * sd / 2), sd, Color.White);
        {
        }

        DrawVisuals(now);

        // Buttons (behind the cookie): fade/slide in from 100 units left when opening, nudge right on hover.
        float shown = open ? Math.Clamp((float)(now - openChangedAt) / 350, 0, 1) : 1 - Math.Clamp((float)(now - openChangedAt) / 500, 0, 1);
        float slideIn = open ? 1 - MathF.Pow(1 - Math.Clamp((float)(now - openChangedAt) / 400, 0, 1), 2) : 1;
        for (int i = 0; i < Buttons.Length; i++)
        {
            if (shown <= 0 || skin.Tex(ButtonTex(Buttons[i].Name, false)) is not { } sp) continue;
            var r = ButtonRect(i);
            float e = 1 - MathF.Pow(1 - hover[i], 3);
            var pos = new Vector2(r.X + r.Width / 2 + (20 * e - 100 * (1 - slideIn)) * u, r.Y + r.Height / 2);
            sp.Draw(pos, sd, ColorAlpha(Color.White, shown));
            if (hover[i] > 0) skin.Tex(ButtonTex(Buttons[i].Name, true))?.Draw(pos, sd, ColorAlpha(Color.White, shown * Math.Min(1, hover[i] * 4)));
            float f = (float)((now - flash.At) / 500);
            if (flash.Index == i && f < 1)
            {
                BeginBlendMode(BlendMode.Additive);
                sp.Draw(pos, sd, ColorAlpha(new Color(0, 78, 165, 255), 0.6f * (1 - f)));
                EndBlendMode();
            }
        }

        // The cookie and its glowing copy (see UpdatePulse).
        if (skin.Tex("menu-osu") is { } cookie)
        {
            cookie.Draw(CookiePos, CookieScale * sd, Color.White);
            bool kiai = songs.Beat() is { Kiai: true };
            if (kiai) BeginBlendMode(BlendMode.Additive);
            cookie.Draw(CookiePos, (1.05f + 0.03f * cookiePulse + clickShrink) * sd, ColorAlpha(Color.White, (kiai ? 0.1f : 0.4f) * (1 - cookiePulse) * glowLevel));
            if (kiai) EndBlendMode();
        }
        else
        {
            Game.TextBold("osu!", CookiePos.X - Game.BoldWidth("osu!", 80 * u) / 2, CookiePos.Y - 40 * u, 80 * u, Color.White);
        }

        UpdateMenuStars(now);
        DrawMenuStars(now);

        if (!double.IsNaN(exitAskedAt)) DrawExitDialog(now);
        if (!double.IsNaN(quitAt))
            DrawRectangle(0, 0, w, h, ColorAlpha(Color.Black, 1 - (float)Math.Clamp((quitAt - now) / 1500, 0, 1)));
        Snow.Update(songs);
        Snow.Draw();
        SongSelect.DrawCursor();
    }

    // Esc on the closed menu: stable's "Are you sure you want to exit osu!?" dialog. 1 / Enter quits, 2 / Esc cancels.
    double exitAskedAt = double.NaN;
    const string ExitQuestion = "Are you sure you want to exit osu!?";
    float exitButtonsY;

    void UpdateExitDialog()
    {
        var mouse = Input.GetMousePosition();
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (Input.IsKeyPressed(KeyboardKey.One) || Input.IsKeyPressed(KeyboardKey.Enter) || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(exitButtonsY)))
        {
            exitAskedAt = double.NaN;
            Activate(3);
        }
        else if (Input.IsKeyPressed(KeyboardKey.Two) || Input.IsKeyPressed(KeyboardKey.Escape) || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(exitButtonsY + 50)))
            exitAskedAt = double.NaN;
    }

    void DrawExitDialog(double now)
    {
        double age = now - exitAskedAt;
        Dialog.Backdrop((float)Math.Clamp(age / 200, 0, 1));
        exitButtonsY = Dialog.Title(ExitQuestion, (float)Math.Clamp(age / 200, 0, 1));
        var mouse = Input.GetMousePosition();
        Dialog.Button(0, "Let's do it!", new Color(154, 205, 50, 255), exitButtonsY, age, mouse);
        Dialog.Button(1, "Cancel", new Color(255, 69, 0, 255), exitButtonsY + 50, age, mouse);
    }
}
