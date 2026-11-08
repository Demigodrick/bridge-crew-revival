#nullable disable
// Revival voice automatic gain: slow up, fast down, speech frames only, with a peak limiter.
namespace StbcRevival.Voice
{
    public sealed class Agc
    {
        const float UpDbPerSecond = 6f, DownDbPerSecond = 30f, Ceiling = 0.98f;
        readonly float target, maxGain, minGain;
        float gainDb;

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
            float peak = 0f;
            for (int i = 0; i < count; i++)
            {
                float g = g0 + (g1 - g0) * ((i + 1) / (float)count);
                float y = frame[offset + i] * g;
                frame[offset + i] = y;
                float a = y < 0 ? -y : y;
                if (a > peak) peak = a;
            }
            if (peak > Ceiling)
            {
                float k = Ceiling / peak;
                for (int i = 0; i < count; i++) frame[offset + i] *= k;
            }
        }
    }
}
