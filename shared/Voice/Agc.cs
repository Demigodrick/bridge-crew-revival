#nullable disable
// Revival voice automatic gain: slow up, fast down, speech frames only, with a peak limiter.
namespace StbcRevival.Voice
{
    public sealed class Agc
    {
        const float UpDbPerSecond = 6f, DownDbPerSecond = 30f, Ceiling = 0.98f;
        readonly float target, maxGain, minGain;
        const float AttackSlope = 0.002f, ReleaseSlope = 0.0001f;
        float gainDb, limiter = 1f;
        float[] envelope;

        public Agc(float targetDbfs = -20f, float maxGainDb = 18f, float minGainDb = -6f)
        {
            target = targetDbfs;
            maxGain = maxGainDb;
            minGain = minGainDb;
        }

        public float GainDb { get { return gainDb; } }

        public void Process(float[] frame, int offset, int count, bool speech)
        {
            if (count <= 0) return;
            float startDb = gainDb;
            if (speech)
            {
                double sum = 0;
                for (int i = 0; i < count; i++)
                {
                    float x = frame[offset + i];
                    sum += x * x;
                }
                double rms = System.Math.Sqrt(sum / count);
                if (rms > 1e-6)
                {
                    float want = target - 20f * (float)System.Math.Log10(rms);
                    if (want > maxGain) want = maxGain;
                    if (want < minGain) want = minGain;
                    float seconds = count / (float)VoiceFormat.SampleRate;
                    float diff = want - gainDb;
                    float up = UpDbPerSecond * seconds, down = DownDbPerSecond * seconds;
                    if (diff > up) diff = up;
                    if (diff < -down) diff = -down;
                    gainDb += diff;
                }
            }
            float g0 = (float)System.Math.Pow(10.0, startDb / 20.0);
            float g1 = (float)System.Math.Pow(10.0, gainDb / 20.0);
            // Apply the interpolated gain, keeping the post-gain samples in the frame.
            for (int i = 0; i < count; i++)
            {
                float g = g0 + (g1 - g0) * ((i + 1) / (float)count);
                frame[offset + i] *= g;
            }
            Limit(frame, offset, count);
        }

        // Smoothed peak limiter. The gain envelope e[i] never exceeds the per-sample need 0.98/|x|; it falls
        // towards a peak along a slope (so the cut is ramped in ahead of it, not a step) and recovers slowly
        // over the following frames.
        void Limit(float[] frame, int offset, int count)
        {
            if (envelope == null || envelope.Length < count) envelope = new float[count];
            float[] e = envelope;
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                float a = frame[offset + i];
                if (a < 0) a = -a;
                e[i] = a > Ceiling ? Ceiling / a : 1f;
                if (a > Ceiling) any = true;
            }
            if (!any && limiter >= 1f) return;
            for (int i = count - 2; i >= 0; i--)
                if (e[i] > e[i + 1] + AttackSlope) e[i] = e[i + 1] + AttackSlope;
            float prev = limiter;
            for (int i = 0; i < count; i++)
            {
                if (e[i] > prev + ReleaseSlope) e[i] = prev + ReleaseSlope;
                if (e[i] > 1f) e[i] = 1f;
                prev = e[i];
                float x = frame[offset + i] * prev;
                if (x > Ceiling) x = Ceiling;
                if (x < -Ceiling) x = -Ceiling;
                frame[offset + i] = x;
            }
            limiter = prev;
        }
    }
}
