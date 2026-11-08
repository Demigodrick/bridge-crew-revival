#nullable disable
// Revival voice speech gate and the energy-only fallback detector. Stepped once per 20 ms Opus frame.
namespace StbcRevival.Voice
{
    public sealed class SpeechGate
    {
        readonly int preRoll, hangFrames;
        readonly float openAt, holdAt;
        int hang;
        bool open;

        public SpeechGate(int preRollFrames = 3, int hangFrames = 15, float open = 0.6f, float hold = 0.35f)
        {
            preRoll = preRollFrames;
            this.hangFrames = hangFrames;
            openAt = open;
            holdAt = hold;
        }

        public bool Open { get { return open; } }

        // Frames to send, ending with this one: 0 while closed, 1 while open (hang included),
        // preRollFrames + 1 on the frame that opens the gate (burstStart is true then).
        public int Step(float speechProbability, out bool burstStart)
        {
            burstStart = false;
            if (!open)
            {
                if (speechProbability < openAt) return 0;
                open = true;
                hang = hangFrames;
                burstStart = true;
                return preRoll + 1;
            }
            if (speechProbability >= holdAt)
            {
                hang = hangFrames;
                return 1;
            }
            if (hang > 0)
            {
                hang--;
                return 1;
            }
            open = false;
            return 0;
        }
    }

    public static class EnergyVad
    {
        // Fallback without RNNoise: 1 if RMS > 0.01, else 0.
        public static float Probability(float[] frame, int offset, int count)
        {
            if (count <= 0) return 0f;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                float x = frame[offset + i];
                sum += x * x;
            }
            return System.Math.Sqrt(sum / count) > 0.01 ? 1f : 0f;
        }
    }
}
