using Raylib_cs;

namespace OsuStableRemake;

// Same bit values as stable.
[Flags]
enum Mods
{
    None = 0,
    NoFail = 1,
    Easy = 2,
    Hidden = 8,
    HardRock = 16,
    SuddenDeath = 32,
    DoubleTime = 64,
    Relax = 128,
    HalfTime = 256,
    Nightcore = 512, // always set together with DoubleTime
    Flashlight = 1024,
    Autoplay = 2048,
    SpunOut = 4096,
    Autopilot = 8192,
    Perfect = 16384, // always set together with SuddenDeath
    ScoreV2 = 1 << 29,
}

static class ModInfo
{
    public const Mods Supported = (Mods)0x7FFF | Mods.ScoreV2; // NF..PF and Score V2; other stable mods (TD, cinema, keys...) are dropped

    public static double Rate(this Mods m) => m.HasFlag(Mods.DoubleTime) ? 1.5 : m.HasFlag(Mods.HalfTime) ? 0.75 : 1;

    public static double ScoreMultiplier(this Mods m)
    {
        // stable's order (it matters for the last bit of the double); Relax and Autopilot score nothing
        double x = 1;
        if (m.HasFlag(Mods.NoFail)) x *= 0.5;
        if (m.HasFlag(Mods.Easy)) x *= 0.5;
        if (m.HasFlag(Mods.HalfTime)) x *= 0.3;
        if (m.HasFlag(Mods.Hidden)) x *= 1.06;
        if (m.HasFlag(Mods.HardRock)) x *= 1.06;
        if (m.HasFlag(Mods.DoubleTime)) x *= 1.12;
        if (m.HasFlag(Mods.Flashlight)) x *= 1.12;
        if (m.HasFlag(Mods.SpunOut)) x *= 0.9;
        if (m.HasFlag(Mods.Relax) || m.HasFlag(Mods.Autopilot)) x *= 0.0;
        return x;
    }

    // Score V2's: HardRock 1.1x and DoubleTime 1.2x, NoFail 1x, the rest as above
    public static double ScoreMultiplierV2(this Mods m)
    {
        double x = 1;
        if (m.HasFlag(Mods.HardRock)) x *= 1.1;
        if (m.HasFlag(Mods.DoubleTime)) x *= 1.2;
        return x * (m & ~(Mods.HardRock | Mods.DoubleTime | Mods.NoFail)).ScoreMultiplier();
    }

    // the multiplier the selected mods play with
    public static double Multiplier(this Mods m) => m.HasFlag(Mods.ScoreV2) ? m.ScoreMultiplierV2() : m.ScoreMultiplier();

    public static string Short(this Mods m)
    {
        var parts = new List<string>();
        void Add(Mods f, string s) { if (m.HasFlag(f)) parts.Add(s); }
        Add(Mods.Easy, "EZ"); Add(Mods.NoFail, "NF"); Add(Mods.HalfTime, "HT"); Add(Mods.HardRock, "HR");
        if (m.HasFlag(Mods.Perfect)) parts.Add("PF"); else Add(Mods.SuddenDeath, "SD");
        if (m.HasFlag(Mods.Nightcore)) parts.Add("NC"); else Add(Mods.DoubleTime, "DT");
        Add(Mods.Hidden, "HD"); Add(Mods.Flashlight, "FL"); Add(Mods.Relax, "RX"); Add(Mods.Autopilot, "AP");
        Add(Mods.SpunOut, "SO"); Add(Mods.Autoplay, "Auto"); Add(Mods.ScoreV2, "V2");
        return parts.Count == 0 ? "No mods" : string.Join(" ", parts);
    }

    // stable's mod-select hotkeys. S and D cycle SD -> PF -> off and DT -> NC -> off.
    public static Mods Toggle(Mods m, KeyboardKey key) => key switch
    {
        KeyboardKey.Q => Flip(m, Mods.Easy, Mods.HardRock),
        KeyboardKey.W => Flip(m, Mods.NoFail, Mods.SuddenDeath | Mods.Perfect),
        KeyboardKey.E => Flip(m, Mods.HalfTime, Mods.DoubleTime | Mods.Nightcore),
        KeyboardKey.A => Flip(m, Mods.HardRock, Mods.Easy),
        KeyboardKey.S => m.HasFlag(Mods.Perfect) ? m & ~(Mods.SuddenDeath | Mods.Perfect)
                   : m.HasFlag(Mods.SuddenDeath) ? m | Mods.Perfect
                   : Flip(m, Mods.SuddenDeath, Mods.NoFail),
        KeyboardKey.D => m.HasFlag(Mods.Nightcore) ? m & ~(Mods.DoubleTime | Mods.Nightcore)
                   : m.HasFlag(Mods.DoubleTime) ? m | Mods.Nightcore
                   : Flip(m, Mods.DoubleTime, Mods.HalfTime),
        KeyboardKey.F => Flip(m, Mods.Hidden, Mods.None),
        KeyboardKey.G => Flip(m, Mods.Flashlight, Mods.None),
        KeyboardKey.Z => Flip(m, Mods.Relax, Mods.Autopilot | Mods.Autoplay),
        KeyboardKey.X => Flip(m, Mods.Autopilot, Mods.Relax | Mods.Autoplay | Mods.SpunOut),
        KeyboardKey.C => Flip(m, Mods.SpunOut, Mods.Autopilot | Mods.Autoplay),
        KeyboardKey.V => Flip(m, Mods.Autoplay, Mods.Relax | Mods.Autopilot),
        KeyboardKey.B => Flip(m, Mods.ScoreV2, Mods.None),
        _ => m,
    };

    // "HDHR", "HD DT", "NC" ... (used by the --mods test flag)
    public static Mods Parse(string s)
    {
        var m = Mods.None;
        s = s.Replace(" ", "").ToUpperInvariant();
        for (int i = 0; i + 1 < s.Length; i += 2)
            m |= s.Substring(i, 2) switch
            {
                "EZ" => Mods.Easy, "NF" => Mods.NoFail, "HT" => Mods.HalfTime, "HR" => Mods.HardRock,
                "SD" => Mods.SuddenDeath, "PF" => Mods.SuddenDeath | Mods.Perfect, "DT" => Mods.DoubleTime,
                "NC" => Mods.DoubleTime | Mods.Nightcore, "HD" => Mods.Hidden, "FL" => Mods.Flashlight,
                "RX" => Mods.Relax, "AP" => Mods.Autopilot, "SO" => Mods.SpunOut, "AT" => Mods.Autoplay, "V2" => Mods.ScoreV2,
                _ => Mods.None,
            };
        return m;
    }

    static Mods Flip(Mods m, Mods f, Mods incompatible) => m.HasFlag(f) ? m & ~f : (m & ~incompatible) | f;
}

