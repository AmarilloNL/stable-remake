namespace OsuStableRemake;

// stable's Score V2 for osu!: 700,000 for combo (each judgement's points times 1 + combo / 10, against what a perfect
// play collects), 300,000 for accuracy^10 scaled by the share of objects judged so far, plus spinner bonus (100 a spin,
// 500 a bonus spin), all times the V2 mod multiplier. The combo counts the same events as the game's combo, after them.
sealed partial class Player
{
    bool ScoreV2 => mods.HasFlag(Mods.ScoreV2);
    double v2Combo, v2ComboMax, v2Bonus, v2Mul;
    int v2Total;

    // The perfect play stable measures against is the one it simulates for HP drain at load (see Health.Calibrate):
    // object by object, a slider's head, repeats and end (30 each) first, then its ticks (10 each), then its 300,
    // which adds no combo; circles and spinners are a 300 that does.
    void InitScoreV2()
    {
        if (!ScoreV2) return;
        v2Mul = mods.ScoreMultiplierV2();
        v2Total = objs.Count;
        int c = 0;
        void Add(int value, bool addsCombo) { if (addsCombo) c++; v2ComboMax += value * (1 + c / 10.0); }
        foreach (var o in objs)
        {
            if (o.Type == ObjType.Slider)
            {
                for (int k = 0; k < o.Slides + 1; k++) Add(30, true);
                for (int k = 0; k < o.Checkpoints.Count(cp => cp.Kind == CheckpointKind.Tick); k++) Add(10, true);
                Add(300, false);
            }
            else Add(300, true);
        }
    }

    // a judgement worth points, after the combo has counted it
    void V2Hit(int value)
    {
        if (!ScoreV2) return;
        v2Combo += value * (1 + combo / 10.0);
        UpdateV2();
    }

    void V2Bonus(int value)
    {
        if (!ScoreV2) return;
        v2Bonus += value;
        UpdateV2();
    }

    void UpdateV2()
    {
        if (!ScoreV2) return;
        int judged = n300 + n100 + n50 + nMiss;
        double acc = judged == 0 ? 1 : (300.0 * n300 + 100.0 * n100 + 50.0 * n50) / (300.0 * judged);
        score = v2Total == 0 || v2ComboMax == 0 ? 0
            : (long)Math.Round((700000.0 * v2Combo / v2ComboMax + 300000.0 * Math.Pow(acc, 10.0) * ((double)judged / v2Total) + v2Bonus) * v2Mul);
    }
}
