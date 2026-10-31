using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// A texture plus its size in "SD" pixels (@2x files count half).
readonly record struct Sprite(Texture2D Tex, float Scale)
{
    public float Width => Tex.Width * Scale;
    public float Height => Tex.Height * Scale;

    public void Draw(Vector2 centre, float scale, Color tint, float rotation = 0, bool flipX = false)
    {
        float w = Width * scale, h = Height * scale;
        DrawTexturePro(Tex, new Rectangle(0, 0, flipX ? -Tex.Width : Tex.Width, Tex.Height), new Rectangle(centre.X, centre.Y, w, h),
            new Vector2(w / 2, h / 2), rotation, tint);
    }
}

sealed class Skin
{
    readonly string dir;
    readonly Dictionary<string, Sprite?> textures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Sprite?> defaultTextures = new(StringComparer.OrdinalIgnoreCase); // osu!ui only (no per-call key string)
    readonly Dictionary<string, SoundPool?> sounds = new(StringComparer.OrdinalIgnoreCase);

    public Color[] ComboColours =
    [
        new(255, 192, 0, 255), new(0, 202, 0, 255), new(18, 124, 255, 255), new(242, 24, 57, 255),
    ];
    public Color SliderBorder = Color.White;
    public Color StarBreakAdditive = new(255, 182, 193, 255);
    public Color? SliderTrackOverride;
    public string HitCirclePrefix = "default";
    public float HitCircleOverlap = -2;
    public bool AllowSliderBallTint, ComboBurstRandom;
    public double Version = 1; // skin.ini Version; "latest" counts as newest. >1 switches stable's newer layouts
    public string ScorePrefix = "score", ComboPrefix = "score";
    public float ScoreOverlap, ComboOverlap, AnimationFramerate;
    public Color SongSelectActiveText = Color.Black, SongSelectInactiveText = Color.White;
    public Color MenuGlow = new(128, 128, 160, 255); // main menu visualizer (stable's built-in default)
    public bool CursorCentre = true, CursorRotate = true, CursorExpand = true;
    public Color InputOverlayText = Color.Black;
    public Color SpinnerBackground = new(100, 100, 100, 255); // stable tints spinner-background with this
    public bool SpinnerNoBlink;

    // Animation frames ("followpoint-3"), cached by (prefix, index) for the same reason.
    readonly Dictionary<(string, int), Sprite?> frames = [];
    public Sprite? Frame(string prefix, int i) => frames.TryGetValue((prefix, i), out var s) ? s : frames[(prefix, i)] = Tex($"{prefix}-{i}");

    // Number-font glyphs, cached by (prefix, char) so drawing numbers builds no texture-name strings per frame.
    readonly Dictionary<(string, char), Sprite?> glyphs = [];
    public Sprite? Glyph(string prefix, char c)
    {
        if (glyphs.TryGetValue((prefix, c), out var g)) return g;
        return glyphs[(prefix, c)] = GlyphSuffix(c) is { } s ? Tex($"{prefix}-{s}") : null;
    }

    // constant: stable's fixed-width digits (score, accuracy): each digit takes the '5' glyph's width, centred in it
    // (measured: the default skin's score digits step 31 texels, '5''s width, not '0''s 33).
    float Advance(string prefix, char c, Sprite g, bool constant) => constant && char.IsAsciiDigit(c) && Glyph(prefix, '5') is { } z ? z.Width : g.Width;

    // Width in pixels of text in a skin number font at this scale, or -1 if a glyph is missing.
    public float FontWidth(string prefix, float overlap, string text, float scale, bool constant = false)
    {
        float total = 0;
        foreach (char c in text)
        {
            if (Glyph(prefix, c) is not { } g) return -1;
            total += Advance(prefix, c, g, constant) * scale;
        }
        return total - overlap * scale * Math.Max(0, text.Length - 1);
    }

    // Draws text with a skin number font (score-0.png ...). alignX: 0 = left edge at x, 1 = right edge at x.
    // Returns false (drawing nothing) if the skin lacks a glyph, so callers can fall back to plain text.
    public bool DrawFont(string prefix, float overlap, string text, float x, float y, float scale, float alignX, Color tint, bool constant = false)
    {
        float total = 0;
        foreach (char c in text)
        {
            if (Glyph(prefix, c) is not { } g) return false;
            total += Advance(prefix, c, g, constant) * scale;
        }
        total -= overlap * scale * Math.Max(0, text.Length - 1);
        float cx = x - total * alignX;
        foreach (char c in text)
        {
            var g = Glyph(prefix, c)!.Value;
            float cell = Advance(prefix, c, g, constant) * scale, gh = g.Height * scale;
            g.Draw(new Vector2(cx + cell / 2, y + gh / 2), scale, tint);
            cx += cell - overlap * scale;
        }
        return true;
    }

    public Skin(string dir)
    {
        this.dir = dir;
        var ini = Path.Combine(dir, "skin.ini");
        // No skin folder (stable's "Default", or none chosen): stable's built-in skin, which counts as the latest version.
        if (!Directory.Exists(dir) || Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)) == "Skins") Version = 99;
        if (!File.Exists(ini)) return;
        var combos = new SortedDictionary<int, Color>();
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            int c = line.IndexOf(':');
            if (line.StartsWith("//") || c < 0) continue;
            string k = line[..c].Trim();
            string v = line[(c + 1)..].Split("//")[0].Trim();
            switch (k)
            {
                case var _ when k.StartsWith("Combo") && int.TryParse(k[5..], out int n):
                    if (ParseColour(v) is { } cc) combos[n] = cc;
                    break;
                case "SliderBorder": SliderBorder = ParseColour(v) ?? SliderBorder; break;
                case "StarBreakAdditive": StarBreakAdditive = ParseColour(v) ?? StarBreakAdditive; break;
                case "SliderTrackOverride": SliderTrackOverride = ParseColour(v); break;
                case "HitCirclePrefix": HitCirclePrefix = v.Replace('\\', '/'); break;
                case "HitCircleOverlap": float.TryParse(v, CultureInfo.InvariantCulture, out HitCircleOverlap); break;
                case "AllowSliderBallTint": AllowSliderBallTint = v == "1"; break;
                case "ComboBurstRandom": ComboBurstRandom = v == "1"; break;
                case "Version": Version = v.Equals("latest", StringComparison.OrdinalIgnoreCase) ? 99 : double.TryParse(v, CultureInfo.InvariantCulture, out var ver) ? ver : 1; break;
                case "SongSelectActiveText": SongSelectActiveText = ParseColour(v) ?? SongSelectActiveText; break;
                case "MenuGlow": MenuGlow = ParseColour(v) ?? MenuGlow; break;
                case "SpinnerBackground": SpinnerBackground = ParseColour(v) ?? SpinnerBackground; break;
                case "SpinnerNoBlink": SpinnerNoBlink = v == "1"; break;
                case "CursorCentre": CursorCentre = v != "0"; break;
                case "CursorRotate": CursorRotate = v != "0"; break;
                case "CursorExpand": CursorExpand = v != "0"; break;
                case "InputOverlayText": InputOverlayText = ParseColour(v) ?? InputOverlayText; break;
                case "SongSelectInactiveText": SongSelectInactiveText = ParseColour(v) ?? SongSelectInactiveText; break;
                case "AnimationFramerate": float.TryParse(v, CultureInfo.InvariantCulture, out AnimationFramerate); break;
                case "ScorePrefix": ScorePrefix = v.Replace('\\', '/'); break;
                case "ScoreOverlap": float.TryParse(v, CultureInfo.InvariantCulture, out ScoreOverlap); break;
                case "ComboPrefix": ComboPrefix = v.Replace('\\', '/'); break;
                case "ComboOverlap": float.TryParse(v, CultureInfo.InvariantCulture, out ComboOverlap); break;
            }
        }
        if (combos.Count > 0) ComboColours = combos.Values.ToArray();
    }

    static Color? ParseColour(string v)
    {
        var p = v.Split(',');
        if (p.Length < 3) return null;
        return new Color(byte.Parse(p[0].Trim()), byte.Parse(p[1].Trim()), byte.Parse(p[2].Trim()), (byte)255);
    }

    public Sprite? Tex(string name) => mapDir != null && MapTex(name) is { } m ? m : Load(name, userSkin: true);

    // A beatmap's own skin elements (its folder's images), used over the user's skin during play unless "Ignore all
    // beatmap skins"; "Always use skin cursor" keeps the user's cursor.
    string? mapDir;
    readonly Dictionary<string, Sprite?> mapTextures = new(StringComparer.OrdinalIgnoreCase);

    public void SetMapSkin(string? folder)
    {
        if (folder == mapDir) return;
        foreach (var t in mapTextures.Values) if (t is { } sp) UnloadTexture(sp.Tex);
        mapTextures.Clear();
        own.Clear(); glyphs.Clear(); frames.Clear();
        mapDir = folder;
    }

    Sprite? MapTex(string name)
    {
        if (mapTextures.TryGetValue(name, out var t)) return t;
        Sprite? s = null;
        if (!(name.StartsWith("cursor", StringComparison.OrdinalIgnoreCase) && Config.GetBool("UseSkinCursor", false)))
            foreach (var (file, scale) in new[] { ($"{name}@2x.png", 0.5f), ($"{name}.png", 1f) })
            {
                var path = Path.Combine(mapDir!, file);
                if (File.Exists(path) && Upload(LoadTexture(path), scale) is { } up) { s = up; break; }
            }
        return mapTextures[name] = s;
    }

    // Whether the user's skin itself has this sprite (not stable's default).
    readonly Dictionary<string, bool> own = new(StringComparer.OrdinalIgnoreCase);
    public bool Own(string name) => own.TryGetValue(name, out bool o) ? o
        : own[name] = mapDir != null && MapTex(name) != null || File.Exists(Path.Combine(dir, name + ".png")) || File.Exists(Path.Combine(dir, name + "@2x.png"));

    // stable's own default sprite, ignoring the user's skin (for elements stable always takes from osu!ui).
    public Sprite? DefaultTex(string name) => Load(name, userSkin: false);

    // Decoding a PNG takes 5-20ms. Prefetch decodes sprites off the main thread (e.g. the ranking screen's, during
    // gameplay), so their first Tex() only uploads. Same lookup order as Load; the dictionary is main-thread only.
    readonly Dictionary<string, Task<(Image Img, float Scale)>> decoding = new(StringComparer.OrdinalIgnoreCase);

    public void Prefetch(IEnumerable<string> names)
    {
        foreach (var name in names)
            if (!textures.ContainsKey(name) && !decoding.ContainsKey(name)) decoding[name] = Task.Run(() => Decode(name));
    }

    // Called every song select frame: one finished prefetch goes to the GPU (upload + mipmaps cost ~0.1-1ms each),
    // so starting a map finds everything ready instead of uploading ~40ms of it in its first frame.
    public void UploadPrefetched()
    {
        foreach (var (name, job) in decoding)
            if (job.IsCompletedSuccessfully) { Load(name, userSkin: true); return; }
    }

    (Image, float) Decode(string name)
    {
        foreach (var (file, scale) in new[] { ($"{name}@2x.png", 0.5f), ($"{name}.png", 1f) })
            if (Path.Combine(dir, file) is var path && File.Exists(path) && LoadImage(path) is { Width: > 0 } img) return (img, scale);
        foreach (var (key, scale) in new[] { ($"{name}@2x", 0.5f), (name, 1f) })
            if (DefaultSkin.Get(key) is { } png && png[0] == 0x89 && LoadImageFromMemory(".png", png) is { Width: > 0 } img) return (img, scale);
        return (default, 0);
    }

    Sprite? Load(string name, bool userSkin)
    {
        var cache = userSkin ? textures : defaultTextures;
        if (cache.TryGetValue(name, out var t)) return t;
        if (userSkin && decoding.Remove(name, out var job))
        {
            if (job.IsCompletedSuccessfully)
            {
                var (img, scale) = job.Result;
                if (img.Width == 0) return cache[name] = null;
                var tex = LoadTextureFromImage(img);
                UnloadImage(img);
                return cache[name] = Upload(tex, scale);
            }
            // Not done: never wait on the pool (it may be busy loading the library); free its image when it lands
            // and load here instead.
            job.ContinueWith(j => { if (j.IsCompletedSuccessfully && j.Result.Img.Width > 0) UnloadImage(j.Result.Img); });
        }
        Sprite? result = null;
        if (userSkin)
            foreach (var (file, scale) in new[] { ($"{name}@2x.png", 0.5f), ($"{name}.png", 1f) })
            {
                var path = Path.Combine(dir, file);
                if (!File.Exists(path)) continue;
                if (Upload(LoadTexture(path), scale) is { } s) { result = s; break; }
            }
        // Not in the user's skin: stable's default skin from osu!ui.dll, like stable does.
        if (result == null)
            foreach (var (key, scale) in new[] { ($"{name}@2x", 0.5f), (name, 1f) })
                if (DefaultSkin.Get(key) is { } png && png[0] == 0x89)
                {
                    var img = LoadImageFromMemory(".png", png);
                    var tex = LoadTextureFromImage(img);
                    UnloadImage(img);
                    if (Upload(tex, scale) is { } s) { result = s; break; }
                }
        cache[name] = result;
        return result;
    }

    static Sprite? Upload(Texture2D tex, float scale)
    {
        if (tex.Id == 0) return null;
        GenTextureMipmaps(ref tex);
        SetTextureFilter(tex, tex.Mipmaps > 1 ? TextureFilter.Trilinear : TextureFilter.Bilinear);
        SetTextureWrap(tex, TextureWrap.Clamp); // no bleeding from the opposite edge when filtered or stretched
        return new Sprite(tex, scale);
    }

    public SoundPool? Sound(string name)
    {
        if (sounds.TryGetValue(name, out var s)) return s;
        SoundPool? pool = null;
        foreach (var ext in new[] { ".wav", ".ogg", ".mp3" })
            if ((pool = SoundPool.Load(Path.Combine(dir, name + ext))) != null) break;
        if (pool == null && DefaultSkin.Get(name) is { } bytes && bytes[0] != 0x89) pool = SoundPool.Load(bytes);
        return sounds[name] = pool;
    }

    // stable's own sample, ignoring the user's skin (the "Use skin's sound samples" option off).
    readonly Dictionary<string, SoundPool?> defaultSounds = new(StringComparer.OrdinalIgnoreCase);
    public SoundPool? DefaultSound(string name) =>
        defaultSounds.TryGetValue(name, out var s) ? s : defaultSounds[name] = DefaultSkin.Get(name) is { } b && b[0] != 0x89 ? SoundPool.Load(b) : null;

    // Everything gameplay draws (besides the map's own files), for PrefetchGameplay; frames past a skin's last are
    // just misses. Glyphs are added per font prefix.
    static readonly string[] GameplaySprites =
    [
        "hitcircle", "hitcircleoverlay", "sliderstartcircle", "sliderstartcircleoverlay", "sliderendcircle", "approachcircle",
        "sliderb", "sliderb0", "sliderfollowcircle", "sliderscorepoint", "reversearrow", "followpoint", "cursor",
        "hit0", "hit50", "hit100", "hit300", "hit100k", "hit300g", "particle50", "particle100", "particle300",
        "spinner-circle", "spinner-approachcircle", "spinner-clear", "spinner-background", "spinner-bottom", "spinner-top",
        "spinner-glow", "spinner-metre", "spinner-middle", "spinner-middle2", "spinner-rpm", "spinner-spin",
        "scorebar-bg", "scorebar-colour", "scorebar-marker", "scorebar-ki", "scorebar-kidanger", "scorebar-kidanger2",
        "inputoverlay-background", "inputoverlay-key", "play-skip", "play-warningarrow", "arrow-warning", "circularmetre",
        "count1", "count2", "count3", "go", "ready", "section-pass", "section-fail", "unpause",
        "pause-overlay", "fail-background", "pause-continue", "pause-retry", "pause-back",
        "followpoint-0", "play-skip-0", "sliderb1",
        "menu-button-background", "scoreboard-explosion-1", "scoreboard-explosion-2", // in-game leaderboard
    ];

    public void PrefetchGameplay()
    {
        Prefetch(GameplaySprites);
        Prefetch(SongSelect.ModIcons.Select(m => "selection-mod-" + m.Icon)); // HUD + ranking; mods aren't picked yet
        foreach (var anim in new[] { "followpoint-", "play-skip-", "sliderb" }) // every frame the skin has (followpoint: 61 in some)
            for (int i = 1; File.Exists(Path.Combine(dir, $"{anim}{i}.png")) || File.Exists(Path.Combine(dir, $"{anim}{i}@2x.png")); i++)
                Prefetch([$"{anim}{i}"]);
        foreach (var prefix in new[] { HitCirclePrefix, ScorePrefix, ComboPrefix, "scoreentry" })
            Prefetch("0123456789,.%x".Select(c => $"{prefix}-{GlyphSuffix(c)}"));
    }

    static string? GlyphSuffix(char c) => c switch
    {
        >= '0' and <= '9' => c.ToString(), ',' => "comma", '.' => "dot", '%' => "percent", 'x' => "x", _ => null,
    };

    // Load everything gameplay can touch up front, so no frame hitches on first use mid-map.
    public void PreloadGameplay()
    {
        string[] tex =
        [
            "hitcircle", "hitcircleoverlay", "sliderstartcircle", "sliderstartcircleoverlay", "sliderendcircle",
            "approachcircle", "sliderb", "sliderb0", "sliderfollowcircle", "sliderscorepoint", "reversearrow",
            "spinner-circle", "spinner-approachcircle", "spinner-clear", "hit0", "hit50", "hit100", "hit300", "cursor",
        ];
        foreach (var t in tex) Tex(t);
        foreach (var prefix in new[] { HitCirclePrefix, ScorePrefix, ComboPrefix })
            foreach (char c in "0123456789,.%x") Glyph(prefix, c);
        foreach (var t in new[] { "scorebar-bg", "scorebar-colour", "scorebar-marker", "scorebar-ki", "scorebar-kidanger", "scorebar-kidanger2" }) Tex(t);
        // The ranking screen's sprites (big grade letters included) decode in the background during the map.
        Prefetch(["ranking-XH", "ranking-X", "ranking-SH", "ranking-S", "ranking-A", "ranking-B", "ranking-C", "ranking-D",
            "hit300g", "hit100k", "ranking-maxcombo", "ranking-accuracy", "ranking-perfect", "ranking-panel", "ranking-graph",
            "ranking-title", "ranking-background-overlay", "ranking-retry", "ranking-replay", "pause-retry", "pause-replay", "menu-back"]);
        foreach (var set in new[] { "normal", "soft", "drum" })
            foreach (var s in new[] { "hitnormal", "hitwhistle", "hitfinish", "hitclap", "slidertick" })
                Sound($"{set}-{s}");
        Sound("combobreak");
        Sound("spinnerbonus");
    }

    public void Unload()
    {
        foreach (var t in textures.Values.Concat(defaultTextures.Values)) if (t is { } sp) UnloadTexture(sp.Tex);
        foreach (var job in decoding.Values) if (job.Wait(5000) && job.Result.Img.Width > 0) UnloadImage(job.Result.Img);
        decoding.Clear();
        foreach (var s in sounds.Values.Concat(defaultSounds.Values)) s?.Unload();
        textures.Clear(); defaultTextures.Clear();
        glyphs.Clear(); frames.Clear();
        sounds.Clear(); defaultSounds.Clear();
    }
}

// Beatmap-folder hitsounds take priority over the skin, like stable.
sealed class BeatmapSamples
{
    readonly Dictionary<string, SoundPool?> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly string[] SetNames = ["normal", "normal", "soft", "drum"];
    static readonly System.Text.RegularExpressions.Regex SampleName = new(@"^(normal|soft|drum)-(hit\w+|slidertick)\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public BeatmapSamples(string folder)
    {
        // Preload the map's own hitsounds; misses are filled in lazily (they fall back to the skin).
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (SampleName.IsMatch(name) && Path.GetExtension(file).ToLowerInvariant() is ".wav" or ".ogg" or ".mp3" && !cache.ContainsKey(name))
                cache[name] = SoundPool.Load(file);
        }
    }

    public static float EffectVolume =>
        Config.GetInt("VolumeUniversal", 100) / 100f * Config.GetInt("VolumeEffect", 100) / 100f;

    // stable's IgnoreBeatmapSamples (and the per-map Hitsounds toggle): off = always the skin's samples.
    public bool UseMapSamples = !Config.GetBool("IgnoreBeatmapSamples", false);

    public void Play(int set, string name, int index, int volumePercent)
    {
        string sample = $"{SetNames[Math.Clamp(set, 0, 3)]}-{name}";
        float vol = EffectVolume * volumePercent / 100f;
        if (index > 0 && UseMapSamples)
        {
            // Everything in the folder was preloaded, so a cache miss means "not in the map": use the skin.
            if (cache.TryGetValue(index > 1 ? sample + index : sample, out var pool) && pool != null) { pool.Play(vol); return; }
        }
        (Config.GetBool("SkinSamples", true) ? Game.Skin.Sound(sample) : Game.Skin.DefaultSound(sample))?.Play(vol);
    }

    public void Unload()
    {
        foreach (var s in cache.Values) s?.Unload();
    }
}
