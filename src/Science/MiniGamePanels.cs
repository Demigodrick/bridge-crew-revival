using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StbcRevival.Science
{
    /// <summary>
    /// One mini game on the science console's scan screen: a picture (a 256x128 texture drawn by MiniGameArt), a readout
    /// line and three sliders, each with its name. The sliders set the game's controls; when the game moves another
    /// control (a sweep ring nudging its neighbour), that slider follows, unless a hand is holding it.
    /// </summary>
    internal sealed class MiniGamePanel
    {
        private static readonly string[][] SliderNames =
        {
            new[] { "Frequency", "Amplitude", "Phase" },
            new[] { "Outer ring", "Middle ring", "Inner ring" },
            new[] { "Band", "Gain", "Polarity" },
        };

        private readonly Texture2D texture;
        private readonly Color32[] pixels = new Color32[MiniGameArt.W * MiniGameArt.H];
        private readonly TMPro.TMP_Text readout;
        private readonly TMPro.TMP_Text[] names = new TMPro.TMP_Text[MiniGame.Controls];
        private readonly SliderInteraction[] sliders = new SliderInteraction[MiniGame.Controls];
        private MiniGame game;
        private bool syncing;
        private string shownReadout;

        internal MiniGamePanel(RectTransform panel, UnityEngine.Object grid)
        {
            var picture = ScienceScreen.Box(panel, "Picture", 0f, 0.44f, 1f, 1f);
            readout = ConsoleWidgets.CloneLabel(ScienceScreen.Box(panel, "Readout", 0f, 0.36f, 1f, 0.44f), ScienceScreen.SmallFont);
            for (int i = 0; i < MiniGame.Controls; i++)
            {
                float top = 0.34f - i * 0.115f, bottom = top - 0.1f;
                names[i] = ConsoleWidgets.CloneLabel(ScienceScreen.Box(panel, "Name " + i, 0f, bottom, 0.3f, top), ScienceScreen.SmallFont);
                var track = ScienceScreen.Box(panel, "Track " + i, 0.32f, bottom, 1f, top);
                var line = ScienceScreen.Box(track, "Line", 0f, 0.42f, 1f, 0.58f);
                line.gameObject.AddComponent<Image>().color = new Color32(13, 35, 55, 255);
                int index = i;
                sliders[i] = ConsoleWidgets.CloneSlider(track, v => OnSlider(index, v), grid);
            }
            // Last, so a panel that fails to build leaves no texture behind.
            texture = new Texture2D(MiniGameArt.W, MiniGameArt.H, TextureFormat.RGBA32, false)
            {
                name = "Science mini game", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            picture.gameObject.AddComponent<RawImage>().texture = texture;
        }

        internal IEnumerable<SliderInteraction> Sliders => sliders;

        /// <summary>The game this panel shows (null: none).</summary>
        internal void Bind(MiniGame g)
        {
            if (g == game) return;
            game = g;
            shownReadout = null;
            if (g == null) return;
            string[] n = SliderNames[(int)g.Kind];
            for (int i = 0; i < MiniGame.Controls; i++) ScienceScreen.SetText(names[i], Translation.T(n[i]));
            Sync(-1);
        }

        private void OnSlider(int i, float v)
        {
            if (syncing || game == null) return;
            game.SetControl(i, v);
            Sync(i);
        }

        // Sliders to the game's controls (but the one just moved, and any a hand holds).
        private void Sync(int skip)
        {
            syncing = true;
            try
            {
                for (int j = 0; j < MiniGame.Controls; j++)
                {
                    var s = sliders[j];
                    if (j == skip || s == null || s.JLLBFOIFAKI) continue; // IsBeingManipulated
                    float want = game.GetControl(j);
                    if (Mathf.Abs(s.OJKPBPCBPMA - want) > 1e-4f) s.OJKPBPCBPMA = want; // Value
                }
            }
            finally { syncing = false; }
        }

        /// <summary>The picture again (about 20 times a second).</summary>
        internal void Draw(float t)
        {
            if (game == null) return;
            MiniGameArt.Draw(game, t, pixels);
            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        /// <summary>The readout line (a few times a second).</summary>
        internal void UpdateText()
        {
            if (game == null) return;
            int pct = Mathf.RoundToInt(game.Match * 100f);
            string text;
            switch (game.Kind)
            {
                case MiniGameKind.Signal: text = string.Format(Translation.T("Match {0}%"), pct); break;
                case MiniGameKind.Sweep: text = string.Format(Translation.T("Aligned {0} of 3"), ((SweepGame)game).Aligned); break;
                default: text = string.Format(Translation.T("Signal {0}%"), pct); break;
            }
            if (text == shownReadout) return;
            shownReadout = text;
            ScienceScreen.SetText(readout, text);
        }

        internal void Destroy()
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
    }

    /// <summary>
    /// The mini games' pictures, as in the mockup (science-minigames-v4): the signal's faint noisy reading under the
    /// player's bright wave; the sweep's three rings, each with a gap, turning to the amber mark at the top (green when
    /// within 10 degrees); the tuning's noisy readout that settles into a clean wave near the peak, over a 20-step
    /// signal meter. Drawn into a reused buffer, bottom row first (as SetPixels32 takes it).
    /// </summary>
    internal static class MiniGameArt
    {
        internal const int W = 256, H = 128;
        private const float Tau = 6.2831853f;

        private static readonly Color32 Back = new Color32(3, 11, 20, 255);
        private static readonly Color32 Bright = new Color32(127, 224, 255, 255);
        private static readonly Color32 Faint = new Color32(47, 85, 102, 255);    // the bright colour at 35% over the back
        private static readonly Color32 Amber = new Color32(205, 162, 76, 255);
        private static readonly Color32 RingOn = new Color32(127, 255, 176, 255);
        private static readonly Color32 RingOff = new Color32(47, 127, 192, 255);
        private static readonly Color32 MeterOn = new Color32(79, 179, 255, 255);
        private static readonly Color32 MeterOff = new Color32(13, 35, 55, 255);

        private static readonly float[] Radii = { 54f, 40f, 26f };
        private const float RingHalf = 3.5f, Gap = 0.35f;

        // Each pixel's distance from the middle and angle (y up), for the rings: worked out once.
        private static float[] radius, angle;
        private static uint noise = 2463534242u;

        internal static void Draw(MiniGame game, float t, Color32[] px)
        {
            for (int i = 0; i < px.Length; i++) px[i] = Back;
            switch (game.Kind)
            {
                case MiniGameKind.Signal: Signal(game, px); break;
                case MiniGameKind.Sweep: Sweep((SweepGame)game, px); break;
                default: Tuning(game, t, px); break;
            }
        }

        private static void Signal(MiniGame g, Color32[] px)
        {
            float tf = 1f + 7f * g.Target(0), ta = 0.1f + 0.9f * g.Target(1), tp = Tau * g.Target(2);
            float f = 1f + 7f * g.GetControl(0), a = 0.1f + 0.9f * g.GetControl(1), p = Tau * g.GetControl(2);
            Wave(px, tf, ta, tp, 0.12f, 2.5f, Faint);
            Wave(px, f, a, p, 0f, 1f, Bright);
        }

        // A wave across the picture: amp * sin(u * freq + phase) for u in 0..2pi (plus noise), -1..1 filling 84% of the height.
        private static void Wave(Color32[] px, float freq, float amp, float phase, float noisy, float half, Color32 c)
        {
            float last = 0f;
            for (int x = 0; x < W; x++)
            {
                float u = x / (float)W * Tau;
                float v = H / 2f + (amp * Mathf.Sin(u * freq + phase) + (noisy > 0f ? (Random01() - 0.5f) * noisy : 0f)) * H * 0.42f;
                Column(px, x, x == 0 ? v : last, v, half, c);
                last = v;
            }
        }

        private static void Sweep(SweepGame g, Color32[] px)
        {
            if (radius == null) Tables();
            for (int y = H / 2; y < H - 2; y++) px[y * W + W / 2] = Amber; // the mark, straight up
            for (int r = 0; r < 3; r++)
            {
                float turn = MiniGame.Wrap01(g.GetControl(r) - g.Target(r)) * Tau; // 0 when lined up
                float gapAt = Tau / 4f - turn;                                     // turning clockwise from the top
                Color32 c = g.RingOff(r) < 10f ? RingOn : RingOff;
                float lo = Radii[r] - RingHalf, hi = Radii[r] + RingHalf;
                int y0 = Mathf.Max(0, (int)(H / 2f - hi) - 1), y1 = Mathf.Min(H - 1, (int)(H / 2f + hi) + 1);
                int x0 = Mathf.Max(0, (int)(W / 2f - hi) - 1), x1 = Mathf.Min(W - 1, (int)(W / 2f + hi) + 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = y * W + x;
                        float d = radius[i];
                        if (d < lo || d > hi) continue;
                        float off = angle[i] - gapAt;
                        off -= Tau * Mathf.Floor((off + Mathf.PI) / Tau); // -pi..pi
                        if (Mathf.Abs(off) > Gap) px[i] = c;
                    }
            }
        }

        private static void Tuning(MiniGame g, float t, Color32[] px)
        {
            float q = g.Match;
            const int meterTop = 16;
            float mid = (H + meterTop) / 2f, room = H - meterTop;
            float last = 0f;
            for (int x = 0; x < W; x++)
            {
                float v = mid + (Random01() - 0.5f) * room * (1f - q) * 0.9f + Mathf.Sin(x * 0.11f + t * 3f) * room * 0.3f * q;
                Column(px, x, x == 0 ? v : last, v, 0.8f, Bright);
                last = v;
            }
            // The meter: 20 steps along the bottom, lit up to the match.
            const int steps = 20, gap = 3;
            float step = (W - gap) / (float)steps;
            for (int s = 0; s < steps; s++)
            {
                Color32 c = s < q * steps ? MeterOn : MeterOff;
                int xa = (int)(gap + s * step), xb = (int)(gap + (s + 1) * step) - gap;
                for (int y = 2; y < meterTop - 3; y++)
                    for (int x = xa; x < xb; x++) px[y * W + x] = c;
            }
        }

        // A column of a line from y a to y b (and half a width either side).
        private static void Column(Color32[] px, int x, float a, float b, float half, Color32 c)
        {
            int lo = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a, b) - half));
            int hi = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(a, b) + half));
            for (int y = lo; y <= hi; y++) px[y * W + x] = c;
        }

        private static void Tables()
        {
            radius = new float[W * H];
            angle = new float[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float dx = x + 0.5f - W / 2f, dy = y + 0.5f - H / 2f;
                    radius[y * W + x] = Mathf.Sqrt(dx * dx + dy * dy);
                    angle[y * W + x] = Mathf.Atan2(dy, dx);
                }
        }

        private static float Random01()
        {
            noise ^= noise << 13; noise ^= noise >> 17; noise ^= noise << 5;
            return (noise >> 8) / 16777216f;
        }
    }
}
