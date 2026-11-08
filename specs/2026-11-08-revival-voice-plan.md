# Revival Voice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Replace the game's voice chat between modded players in Steam mode with the mod's own pipeline,
Revival voice. It uses 48 kHz Opus in 20 ms frames, carries the previous frame plus in-band FEC, runs an
adaptive reordering jitter buffer, and cleans the mic with RNNoise, AGC and VAD. Playback goes through the
game's own `WwiseAudioPlayer`.

**Architecture:**
- **Pure, Unity-free voice logic** lives in `shared/Voice/`. It is compiled into the mod (net35) and into a
  new xunit test project (net8), and tested there against the system `libopus`.
- **Game-facing glue** lives in `src/Voice/`:
  - a mic tap shared with voice commands
  - a capture worker thread
  - Steam P2P transport on channel 4
  - a playout thread per session
  - a push into each crewmate's `WwiseAudioPlayer`
- **The existing `DirectVoice` and `VoiceJitter`** stay as the game-voice fallback. `DirectVoice`'s hello
  also advertises Revival support.

**Tech stack:**
- BepInEx 5 / HarmonyX plugin, C# `LangVersion latest`, targeting **net35** (Unity 5.4 Mono)
- libopus through the game's own `opus.dll` (`[DllImport("opus")]`, the same name the game uses)
- RNNoise 0.2, built by us as a mingw-w64 DLL
- Steamworks P2P through the obfuscated `Steamworks.IJILPGFHMJG` (as in `src/DirectVoice.cs`)
- xunit on net8 for the tests

**Spec:** `specs/2026-11-08-voice-chat-design.md`. Read it first. This plan argues from it.

## Global Constraints

- Mod code targets **net35**. No `System.Threading.Tasks`, no `System.Collections.Concurrent`, no
  `ValueTuple`, no `Span`. Use threads, `lock`, `AutoResetEvent` and plain arrays. `shared/Voice/*.cs` must
  compile under both net35 and net8.
- Revival voice runs only when `Plugin.Mode.Value == PhotonMode.Steam` and `[Voice] Engine == Revival`.
- New config, section `[Voice]` in `src/Plugin.cs`, with these exact defaults:
  - `Engine = Revival` (enum `VoiceEngine { Revival, Game }`)
  - `NoiseSuppression = true`
  - `AutoGain = true`
  - `Bitrate = 48` (kbps, clamped 24–96)
  - `DebugNetSim = ""` (for example `"loss=5,burst=2,reorder=2,jitter=50"`)
  - `DebugLoopback = false`
- Existing `[Voice] JitterBufferMs` and `DirectVoice` stay unchanged and still govern the game-voice
  fallback.
- Audio format:
  - 48000 Hz mono
  - Opus frame 20 ms = 960 samples
  - RNNoise frame 10 ms = 480 samples
  - Opus `OPUS_APPLICATION_VOIP`, VBR on, DTX off
  - `INBAND_FEC` 1
  - `PACKET_LOSS_PERC` starts at 10, floor 5, cap 30
- VAD:
  - pre-roll 60 ms (3 Opus frames)
  - hang 300 ms (15 frames)
  - open at speech probability ≥ 0.6, stay open while a frame reaches ≥ 0.35 within the hang time
- Jitter buffer:
  - target delay starts at 60 ms, bounded 40–300 ms
  - grows 20 ms per mid-burst underrun
  - conceals at most 5 frames (100 ms), then fades out within the last concealed frame and stops
- Steam: channel 4, `k_EP2PSendUnreliableNoDelay`. Steam calls only on the main thread.
- One mic recording: never call `Microphone.Start`/`Microphone.End` on a device voice chat is recording
  (commit c903fa8).
- The native RNNoise DLL lives at `BepInEx/native/rnnoise.dll`, not under `plugins/`, because BepInEx scans
  `plugins/` for mods. It is loaded by full path with `LoadLibrary`/`GetProcAddress`, as Vosk is in
  `src/VoiceCommands.cs:1020-1060`. If it's missing, run without noise suppression (energy VAD) and log
  why. Never crash.
- Obfuscated names: look them up in `tools/xref/names.map` and the `decompiled/` tree. Add any new names you
  rely on to `names.map` in the same style.
- **Never build or deploy while the game is running.** Replacing the DLL freezes it. Building is fine;
  close the game before deploying.
- Log style: plain sentences prefixed `Revival voice:`, matching `DirectVoice`/`VoiceJitter`. The per-peer
  report goes out every 30 s.
- Commits: one per task, message in the repo's style (a plain sentence, no `feat:` prefix), ending with the
  `Co-Authored-By` line.

## Review Focus

The inputs the spec implies that no unit test reaches, most likely first. Each has a check in the task named.

1. **A crewmate joins, leaves or rejoins mid-session (a new Steam stream ID, the same actor).** Expect no
   double voice and no stuck silence; the new stream is picked up within a second. Check: Task 9 step
   "rejoin".
2. **The mic is muted in the game, or the mic device is changed in the menu, while talking.** Expect nothing
   to be sent while muted, and the tap to follow the new device with no `Microphone.Start` of its own.
   Check: Task 8 step "mute and device change".
3. **Main-thread hitches of 100–500 ms (loading, the hang watchdog's cases).** Expect one glitch at most,
   no runaway delay, and the delay back to target by the next burst. Check: Task 10 step "hitch", using a
   debug key that sleeps the main thread for 300 ms.
4. **The voice player for a crewmate doesn't exist yet, or was destroyed (scene change, briefing room to
   bridge).** Expect packets to be dropped quietly and playback to resume once the player exists, with no
   exceptions in the log. Check: Task 10 step "scene change".
5. **PvP, the other ship's crew and the lobby's table chat.** Expect Revival voice to obey exactly what
   `PvpRelay.VoiceChat_Prefix` enforces for game voice. Check: Task 11 step "PvP".

---

## Task 1: Spike (throwaway): 48 kHz playback and capture inside the game

This proves the spec's first risk before anything is built on it. The outcome decides `VoiceFormat.PlaybackRate`
and whether capture raises the mic to 48 kHz.

**Files:**
- Create (throwaway, on branch `spike/voice-48k`, not merged): `src/Voice/Spike48k.cs`
- Modify: `specs/2026-11-08-voice-chat-design.md`. Append a section headed "Spike results" (this part is
  kept).

- [ ] **Step 1: Branch** with `git switch -c spike/voice-48k`.
- [ ] **Step 2: Write `Spike48k`**, with a `Patch(Harmony)` and an `Update()` called from `Plugin`:
  1. Postfix `DaikonForge.VoIP.NJPHCGOEOPO.GetInputSampleRate` to return 48000.
  2. Every 10 s, log the voice chat mic clip's `frequency`, found as `VoiceCommands.VoiceChatRecording()`
     finds it.
  3. On F10, for each remote `WwiseAudioPlayer` under `VoIpManager.Instance` (`_voipPlayers`), call
     `SetSampleRate(48000)`. Then call `BufferAudio` with 2 s of a 1 kHz sine followed by a 2 s
     200 Hz→16 kHz log sweep, in 960-sample pieces over 100 frames.
  4. On F11, do the same through a Unity `AudioSource` with a streaming clip
     (`AudioClip.Create(name, 48000*4, 1, 48000, true, reader)`), for the loopback path.
- [ ] **Step 3: Build** with `dotnet build src -c Release`. Close the game first, then deploy.
- [ ] **Step 4: In a real session with one crewmate, check:**
  - (a) The log shows a mic clip at 48000 Hz.
  - (b) Game voice both ways still works with the patch on, since the game resamples to its own rate.
  - (c) Voice commands still work.
  - (d) F10: the crewmate hears a clean tone and a sweep that reaches high frequencies, from your avatar's
    position, with lip movement.
  - (e) F11: you hear the tone locally.
- [ ] **Step 5: Record results** under "Spike results" in the spec, one line each for (a)–(e). Include:
  - **`PlaybackRate`:** 48000 if (d) is clean. Otherwise the rate the player plays correctly, and Opus
    decodes straight to it.
  - **`RaiseMicRate`:** true if (a)–(c) pass.
  - **`LoopbackOutput`:** `UnityAudioSource` if (e) works, otherwise `None`.
- [ ] **Step 6: Commit the spec change only, on `main`.** Run `git switch main`, then
  `git checkout spike/voice-48k -- specs/2026-11-08-voice-chat-design.md`, then commit "Voice spec: 48 kHz
  spike results". Leave the spike branch unmerged.

## Task 2: Voice test project and wire format

**Files:**
- Create: `tests/Voice.Tests/Voice.Tests.csproj`. It is net8 xunit like
  `server/StbcServer.Tests/StbcServer.Tests.csproj`, with
  `<Compile Include="../../shared/Voice/*.cs" Link="Voice/%(Filename).cs" />`.
- Create: `tests/Voice.Tests/GlobalUsings.cs` containing `global using Xunit; global using StbcRevival.Voice;`
- Create: `shared/Voice/VoiceWire.cs`
- Create: `tests/Voice.Tests/VoiceWireTests.cs`
- Modify: `src/StbcRevival.csproj`. Add `<Compile Include="../shared/Voice/*.cs" Link="Shared/Voice/%(Filename).cs" />`.

**Interfaces (produces):**

```csharp
namespace StbcRevival.Voice {
  public static class VoiceFormat {
    public const int SampleRate = 48000, FrameSamples = 960, FrameMs = 20, NoiseFrameSamples = 480;
    public const int Channel = 4;
    public static int PlaybackRate = 48000;   // set from Task 1's result
  }
  public sealed class VoicePacketIn {
    public uint Stream; public uint Seq; public bool BurstStart;
    public byte[] Opus; public int OpusLength;   // frame Seq
    public byte[] Prev; public int PrevLength;   // frame Seq-1, PrevLength 0 = none
  }
  public static class VoiceWire {
    public const byte VoiceType = (byte)'R', ReportType = (byte)'Q', Version = 1;
    public static int WriteVoice(byte[] dest, uint stream, uint seq, bool burstStart,
                                 byte[] opus, int opusLength, byte[] prev, int prevLength); // returns bytes written
    public static bool TryReadVoice(byte[] src, int length, VoicePacketIn into);           // copies payloads
    public static int WriteReport(byte[] dest, uint stream, byte lossPercent, ushort jitterMs);
    public static bool TryReadReport(byte[] src, int length, out uint stream, out byte lossPercent, out ushort jitterMs);
  }
}
```

Voice layout (little-endian):

| Bytes | Field |
|---|---|
| `[0]` | `'R'` |
| `[1]` | version |
| `[2]` | flags (bit 0 = burst start) |
| `[3..6]` | stream |
| `[7..10]` | seq |
| `[11..12]` | opus length |
| | opus bytes |
| 2 bytes | prev length |
| | prev bytes |

Report layout: `'Q'`, version, stream (4 bytes), loss (1 byte), jitter in ms (2 bytes).

- [ ] **Step 1: Write the failing tests:**
  - `RoundTripsVoiceWithPrev`: stream 7, seq 1000, burst start true, 3-byte and 2-byte payloads. Every
    field matches after reading.
  - `RoundTripsVoiceWithoutPrev`: `PrevLength == 0`.
  - `RejectsTruncated`: every prefix shorter than the full packet gives false.
  - `RejectsWrongTypeOrVersion`.
  - `RoundTripsReport`: stream 9, loss 12, jitter 85.
- [ ] **Step 2: Run** `dotnet test tests/Voice.Tests`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement** `VoiceFormat`, `VoicePacketIn` and `VoiceWire`. Bounds-check everything:
  `TryReadVoice` never throws on any input.
- [ ] **Step 4: Run** `dotnet test tests/Voice.Tests`. Expected: PASS. Also run `dotnet build src -c Release`,
  which must succeed under net35.
- [ ] **Step 5: Commit** "Revival voice: test project and the voice packet format".

## Task 3: Opus encoder and decoder wrapper

**Files:**
- Create: `shared/Voice/Opus.cs`
- Create: `tests/Voice.Tests/OpusTests.cs`

**Interfaces (produces):**

```csharp
public interface IFrameDecoder {
  int Decode(byte[] packet, int length, float[] pcm, bool fec); // samples written (FrameSamples), <0 error
  int Conceal(float[] pcm);                                     // PLC, samples written
}
public sealed class OpusEncoder : IDisposable {
  public OpusEncoder(int sampleRate, int bitrateKbps);          // VOIP, VBR, DTX off, INBAND_FEC 1, LOSS_PERC 10, complexity 8
  public int PacketLossPercent { set; }                         // clamped 5..30
  public int Encode(float[] pcm, int offset, byte[] output);    // one FrameSamples frame; bytes written
}
public sealed class OpusDecoder : IFrameDecoder, IDisposable {
  public OpusDecoder(int sampleRate);                           // sampleRate = VoiceFormat.PlaybackRate
}
```

- Use `[DllImport("opus")]` for `opus_encoder_create`, `opus_encode_float`, `opus_encoder_ctl`,
  `opus_decoder_create`, `opus_decode_float` and `opus_*_destroy`.
- `opus_encoder_ctl` is variadic. Declare it as `(IntPtr st, int request, int value)`.
- The ctl request codes from `opus_defines.h`:

  | Request | Code |
  |---|---|
  | `SET_BITRATE` | 4002 |
  | `SET_VBR` | 4006 |
  | `SET_COMPLEXITY` | 4010 |
  | `SET_INBAND_FEC` | 4012 |
  | `SET_PACKET_LOSS_PERC` | 4014 |
  | `SET_DTX` | 4016 |
  | `SET_SIGNAL` | 4024, with `OPUS_SIGNAL_VOICE` = 3001 |

- `OPUS_APPLICATION_VOIP` is 2048.
- The decoder's frame size is `PlaybackRate / 50`.
- With `fec` true, `opus_decode_float(..., decode_fec: 1)` reconstructs the *previous* frame from this
  packet.

- [ ] **Step 1: Write the failing tests** (skip all of them with a clear message if `libopus` can't be
  loaded):
  - `SineSurvivesRoundTrip`: 1 s of 1 kHz sine at 0.5 amplitude, encoded and decoded frame by frame. After
    the first 2 frames, the output RMS is within 10% of the input's and the dominant frequency is 1 kHz,
    within one FFT bin or by zero-crossing count.
  - `FecRecoversDroppedFrame`: encode 50 frames of speech-like noise (seeded) with
    `PacketLossPercent = 20`. Decode with frame 25 missing, using `Decode(packet26, fec: true)` for the slot
    of frame 25. The recovered frame's correlation with a clean decode of frame 25 is > 0.5, and it isn't
    silence.
  - `ConcealProducesFullFrame`: `Conceal` returns 960 samples.
  - `PacketLossPercentClamped`: setting 50 and then 1 doesn't throw (read back through `OPUS_GET` if you
    add it, else just exercise it).
- [ ] **Step 2: Run the tests.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests.** Expected: PASS. Run `dotnet build src -c Release`.
- [ ] **Step 5: Commit** "Revival voice: Opus encoder and decoder with FEC".

## Task 4: Resampler, AGC and speech gate

**Files:**
- Create: `shared/Voice/Resampler.cs`
- Create: `shared/Voice/Agc.cs`
- Create: `shared/Voice/SpeechGate.cs`
- Create: `tests/Voice.Tests/ProcessingTests.cs`

**Interfaces (produces):**

```csharp
public sealed class Resampler {                 // streaming windowed-sinc (Kaiser, 32 taps per phase)
  public Resampler(int fromRate, int toRate);
  public int Process(float[] input, int count, float[] output); // returns samples written; output sized by caller (count*to/from+64)
}
public sealed class Agc {
  public Agc(float targetDbfs = -20f, float maxGainDb = 18f, float minGainDb = -6f);
  public float GainDb { get; }
  public void Process(float[] frame, int offset, int count, bool speech);
}
public sealed class SpeechGate {
  public SpeechGate(int preRollFrames = 3, int hangFrames = 15, float open = 0.6f, float hold = 0.35f);
  public int Step(float speechProbability, out bool burstStart); // frames to send ending with this one: 0, 1, or preRollFrames+1 on opening
  public bool Open { get; }
}
public static class EnergyVad { public static float Probability(float[] frame, int offset, int count); } // fallback without RNNoise: 1 if RMS > 0.01, else 0
```

`Agc` details:
- Measures RMS on speech frames only.
- Moves the gain toward `target − rms`, clamped to [min, max]: up at most 6 dB/s, down at most 30 dB/s.
- Applies the gain per sample, interpolated across the frame.
- Then a peak limiter keeps |x| ≤ 0.98.
- Non-speech frames get the current gain with no update.

- [ ] **Step 1: Write the failing tests:**
  - `ResamplerUp24kTo48kKeepsTone`: a 3 kHz tone at 24 kHz becomes a 3 kHz tone at 48 kHz, with RMS within
    5% after warm-up and twice the sample count within ±64.
  - `ResamplerSameRateIsCopy`.
  - `AgcBringsQuietSpeechUp`: 5 s of a −40 dBFS tone marked speech ends with RMS within 3 dB of −22 dBFS
    (target minus the 18 dB cap = −22).
  - `AgcDoesNotPumpNoise`: 5 s of −50 dBFS noise marked non-speech leaves `GainDb == 0`.
  - `AgcNeverClips`: a 0 dBFS square wave with a +18 dB gain stays ≤ 0.98.
  - `GateOpensWithPreRoll`: probabilities `[0.1, 0.1, 0.1, 0.9]` give 0, 0, 0, then 4 with `burstStart`.
  - `GateHangsThenCloses`: after opening, 15 frames at 0.1 each give 1. The 16th gives 0.
  - `GateHoldResetsHang`: a 0.4 frame inside the hang time restarts it.
- [ ] **Step 2: Run the tests.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests.** Expected: PASS. Run `dotnet build src -c Release`.
- [ ] **Step 5: Commit** "Revival voice: resampler, auto gain and speech gate".

## Task 5: Jitter buffer

The core of the "no chop" goal. It is pure, takes an injected `IFrameDecoder`, and is driven by a caller's
clock.

**Files:**
- Create: `shared/Voice/JitterBuffer.cs`
- Create: `tests/Voice.Tests/JitterBufferTests.cs`, using a `FakeDecoder : IFrameDecoder` that writes
  recognisable frames:
  - a decoded frame of seq N is filled with N
  - FEC from packet N is filled with −(N−1)
  - concealment is filled with 0.5

**Interfaces (produces):**

```csharp
public enum FrameSource { Normal, Redundant, Fec, Concealed }
public sealed class JitterStats {
  public int Received, Late, Duplicates, Lost, Redundant, Fec, Concealed, Underruns;
  public int TargetMs, DepthMs, JitterMs, LossPercent;
}
public sealed class JitterBuffer {
  public JitterBuffer(IFrameDecoder decoder);
  public void Push(VoicePacketIn packet, double arrivalMs);  // thread-safe
  public bool Pop(float[] pcm, out FrameSource source);     // one 20 ms playout tick; false = silence (nothing to play)
  public bool Playing { get; }
  public JitterStats TakeStats();                           // counters reset, gauges kept
}
```

**Behaviour to implement (the algorithm the tests pin):**
- **Reordering:** packets are stored by seq. A packet whose seq is ≤ the last played seq counts as `Late`
  and is dropped. A seq already held counts as `Duplicates`.
- **Stream change:** a different `Stream` resets everything.
- **Start:** not playing until a `BurstStart` packet, or any packet when idle, has arrived and the held span
  (highest seq − next seq + 1) × 20 ms ≥ `TargetMs`. Then play from the lowest held seq.
- **Each `Pop` while playing, for the next seq:**
  1. Held: decode it → `Normal`.
  2. Otherwise, a held packet of next+1 with `PrevLength > 0`: decode `Prev` → `Redundant`.
  3. Otherwise, a held packet of next+1: `Decode(fec: true)` → `Fec`.
  4. Otherwise, if any later packet is held, or fewer than 5 frames have been concealed in a row: `Conceal`
     → `Concealed`. Count `Lost` when a later packet proves the frame existed. Count an `Underrun` (and grow
     the target by 20 ms, max 300) when nothing later is held.
  5. On the 5th concealed frame in a row with nothing later held, fade the frame linearly to zero, stop
     playing (`Playing = false`), and return it. The next `Pop` returns false.
- **Jitter estimate:**
  - Per packet, transit = `arrivalMs − seq × 20`. Keep the last 100.
  - `JitterMs` = p95(transit − min(transit)).
  - At each start, `TargetMs = clamp(JitterMs + 20, 40, 300)`, but never below the current target minus
    20 (shrink slowly). The initial `TargetMs` is 60.
- **Loss and depth:** `LossPercent` = lost / (lost + received) over the window since the last `TakeStats`.
  `DepthMs` = held span × 20 at the last `Pop`.

- [ ] **Step 1: Write the failing tests** (each pushes packets with explicit arrival times, then pops):
  - `PlaysInOrder`: seqs 0..9 pushed in order at 20 ms spacing. After the target is reached, pops give
    1..10-style frames in seq order, all `Normal`.
  - `ReordersSwappedPackets`: pushing 0,1,3,2,4… plays 0,1,2,3,4 with no `Concealed`.
  - `RecoversFromRedundancy`: seq 5 is never pushed, and 6 carries prev. Frame 5 is `Redundant` with seq-5
    content, and `Lost` is 1.
  - `RecoversFromFecWhenNoPrev`: seq 5 is missing and 6 has `PrevLength 0`. Frame 5 is `Fec`.
  - `ConcealsThenFadesAndStops`: after seq 9, nothing more is pushed. Pops give 5 `Concealed`, the 5th
    fading to 0 at its end, then false. `Underruns` ≥ 1.
  - `DropsLateAndDuplicate`: seq 3 is pushed again after 3 has played, giving `Late` 1. A held seq pushed
    twice gives `Duplicates` 1.
  - `NewStreamResets`: stream 1 playing, then stream 2 seq 0 with burst start → it plays stream 2 from
    seq 0.
  - `TargetAdaptsToJitter`: 200 packets with ±60 ms uniform arrival jitter (seeded), followed by a new burst.
    `TargetMs` lands between 80 and 160.
  - `TargetShrinksSlowly`: after the jittery period, 200 steady packets and 3 bursts. The target drops by
    at most 20 per burst.
  - `UnderrunGrowsTarget`: a mid-burst gap with nothing later held raises `TargetMs` by 20.
- [ ] **Step 2: Run the tests.** Expected: FAIL.
- [ ] **Step 3: Implement.** Use a `lock` around state, because `Push` (main thread) and `Pop` (playout
  thread) race.
- [ ] **Step 4: Run the tests.** Expected: PASS. Run `dotnet build src -c Release`.
- [ ] **Step 5: Commit** "Revival voice: reordering, adaptive jitter buffer with redundancy, FEC and
  concealment".

## Task 6: Network simulator (debug)

**Files:**
- Create: `shared/Voice/NetSim.cs`
- Create: `tests/Voice.Tests/NetSimTests.cs`

**Interfaces (produces):**

```csharp
public sealed class NetSim {
  public static NetSim Parse(string spec, int seed);       // "" or null -> null (off). keys: loss, burst, reorder, jitter (ints; % or ms)
  public void Offer(byte[] packet, int length, double nowMs);
  public bool TryTake(double nowMs, out byte[] packet, out int length);
}
```

- `loss`: the % of packets dropped.
- `burst`: once a drop happens, the extra packets also dropped (fixed count).
- `reorder`: the % of packets delayed by one extra slot (20 ms).
- `jitter`: a uniform 0..N ms added delay.

- [ ] **Step 1: Write the failing tests:**
  - `OffIsNull`
  - `LossRateRoughlyMatches`: 10,000 packets at loss=5 give 4–6% dropped.
  - `BurstDropsRuns`
  - `JitterDelaysWithinBound`
  - `ReorderSwaps`
- [ ] **Step 2: Run the tests.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Commit** "Revival voice: network simulator for testing".

## Task 7: RNNoise native build, loader and packaging

**Files:**
- Create: `tools/rnnoise/build.sh`. It uses Docker (`debian:bookworm` with `mingw-w64`, `autoconf`,
  `automake` and `libtool`) to cross-build `rnnoise.dll` (x86_64) from the upstream release tarball
  `https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz`. It checks a pinned SHA-256
  (record the value you verify in the script), statically links libgcc so there are no extra runtime DLLs,
  and writes `tools/rnnoise/out/rnnoise.dll`. `out/` is git-ignored.
- Create: `src/Voice/RnNoise.cs`
- Modify: `tools/package.sh`. Run `tools/rnnoise/build.sh` if `out/rnnoise.dll` is missing, then copy it to
  `$out/native/rnnoise.dll`.
- Modify: `docs/install.ps1` and `docs/install.sh`.
  - Install: copy `native\rnnoise.dll` to `BepInEx\native\`.
  - Uninstall: remove `BepInEx\native\rnnoise.dll`, and the folder if empty.
  - Mirror the existing StbcCore handling around `install.ps1:290-360`.

**Interfaces (produces):**

```csharp
internal sealed class RnNoise : IDisposable {
  internal static RnNoise TryCreate(out string why);        // null + reason if the DLL or a symbol is missing
  internal float Process(float[] frame, int offset);        // 480 samples in place (scale ×32768 in, ÷32768 out); returns speech probability
}
```

It loads `Path.Combine(Paths.BepInExRootPath, "native", "rnnoise.dll")` with `LoadLibrary` and binds
`rnnoise_create`, `rnnoise_process_frame` and `rnnoise_destroy` through
`Marshal.GetDelegateForFunctionPointer`, as `VoiceCommands`' `Vosk.Bind` does.

- [ ] **Step 1: Run** `tools/rnnoise/build.sh`. Expected: `tools/rnnoise/out/rnnoise.dll` exists, and
  `file` reports "PE32+ executable (DLL) … x86-64". `x86_64-w64-mingw32-objdump -p` (inside the container)
  lists only `KERNEL32.dll` and `msvcrt.dll` imports.
- [ ] **Step 2: Implement `RnNoise`.**
- [ ] **Step 3: Update packaging and the installers.** Run `tools/package.sh`. Expected: the zip contains
  `native/rnnoise.dll`.
- [ ] **Step 4: Check in-game.** Install from the zip with the game closed, start it, and add a temporary
  startup log line `Revival voice: noise suppression ready` from `TryCreate` (it is kept, logged once).
  Expected: the log shows it under Proton. Delete or rename the DLL, and expected: `Revival voice: noise
  suppression off: <reason>` with no exception.
- [ ] **Step 5: Commit** "Revival voice: RNNoise built from source, loaded from BepInEx/native, packaged and
  installed".

## Task 8: Capture pipeline

**Files:**
- Create: `src/Voice/MicTap.cs`. Extract `VoiceChatRecording`, `ChatInput` and `ChatDevice` from
  `src/VoiceCommands.cs:600-662` into it, and have `VoiceCommands` call `MicTap` instead. Its behaviour
  stays the same.
- Create: `src/Voice/VoiceCapture.cs`
- Modify: `src/VoiceCommands.cs`, to use `MicTap`
- Modify: `src/Plugin.cs`, for the config entries from Global Constraints and the `Patch`/`Update` calls
  (wrapped in `Safely(...)` like the others)

**Interfaces:**
- **Consumes:** `MicTap`, `RnNoise`, `Resampler`, `Agc`, `SpeechGate`, `EnergyVad`, `OpusEncoder`,
  `VoiceWire`, `VoiceFormat`.
- **Produces:**

```csharp
internal static class MicTap {
  internal static bool TryFind(out string device, out AudioClip clip); // voice chat's recording, null if not recording
  internal sealed class Reader { internal int Read(List<float> into); } // new mono samples since the last call, at clip.frequency
}
internal static class VoiceCapture {
  internal static void Patch(Harmony harmony);   // AudioInputDeviceBase.get_IsTalking postfix; GetInputSampleRate postfix if Task 1 said RaiseMicRate
  internal static void Update();                 // main thread: tap -> worker queue; start/stop worker
  internal static bool Talking { get; }          // gate open
  internal static uint Stream { get; }           // random per capture start
  internal static bool TryTakePacket(out byte[] wire, out int length); // encoded VoiceWire packets, in order
  internal static int PacketLossPercent { set; } // forwarded to the encoder
  internal static string Describe();             // for the 30 s report: speech seconds, gain dB, NS on/off
}
```

**Worker thread loop:**
1. Take samples from the queue.
2. If the clip rate isn't 48 kHz, resample to 48 kHz.
3. For each 480-sample frame: if NS is on, `RnNoise.Process` gives the probability; otherwise
   `EnergyVad.Probability`.
4. For each pair of 480-sample frames (= 960): run `Agc.Process(speech: gate.Open || prob ≥ 0.6)` if AGC
   is on, then `OpusEncoder.Encode`. Encode every frame, so the encoder stays continuous.
5. Keep the last 4 encoded frames.
6. `gate.Step(max prob of the two halves, out burstStart)` returns n. Emit the last n frames as packets:
   seq = frame counter, burst start on the first, and prev = the encoded frame before each.

Other rules:
- The seq counter increases for every 20 ms of mic time, sent or not.
- When the tap finds no recording (muted, mic off), stop feeding. `Talking` becomes false, and the next
  start gets a new `Stream`.
- `get_IsTalking` postfix: for the *local* input device only, while Revival is active and capture is
  running, return `Talking`.

- [ ] **Step 1: Do the `MicTap` extraction, and build.** Expected: the build succeeds and `VoiceCommands`
  still compiles against `MicTap`.
- [ ] **Step 2: Implement `VoiceCapture`, and build.**
- [ ] **Step 3: Add a temporary in-game check.** With `DebugLoopback = true`, decode `TryTakePacket`'s
  packets with an `OpusDecoder` and play them through the Task 1 `LoopbackOutput`. If that is `None`, write
  10 s to `BepInEx/voice-loopback.wav` instead. This debug path stays, behind `DebugLoopback`.
  Expected:
  - You hear or record yourself clearly, with fan or keyboard noise suppressed.
  - Quiet speech is lifted.
  - The first syllable isn't clipped.
  - The talking indicator follows speech.
- [ ] **Step 4: Check mute and device change.** Mute the mic in the game's menu while talking: packets stop
  within 100 ms. Unmute: they resume with a new stream. Change the mic device in the menu: capture follows,
  with no `Microphone.Start` from the mod (grep the log for the voice-commands warning). Voice commands
  still work while talking.
- [ ] **Step 5: Commit** "Revival voice: capture with noise suppression, auto gain and speech gate; mic tap
  shared with voice commands".

## Task 9: Transport, peers and handover

Tasks 9 and 10 depend on each other: transport delivers to playback, and playback needs transport to hear
anything. Order the work like this:
1. Task 9 step 1.
2. Task 10 step 1.
3. Task 9 steps 2–5.
4. Task 10 steps 2–6.

Commit each task's code with its own message once its in-game steps pass.

**Files:**
- Create: `src/Voice/RevivalVoice.cs`
- Modify: `src/DirectVoice.cs`
  - The hello gains a third byte, `revival` (1 when Revival is active here and the capture pipeline
    started). Old two-byte hellos read as 0.
  - Expose `internal sealed class PeerView { ulong SteamId; int Actor; string Name; bool Direct; bool Revival; }`
    and `internal static List<PeerView> Peers()`.
  - In `SendEvent_Prefix`, peers with `RevivalVoice.SendsTo(steamId)` are left out of both `Direct` and
    `ViaHost`.
  - In `Deliver`, skip when `RevivalPlayback.Live(actor)`.
- Modify: `src/Plugin.cs`, to call `RevivalVoice.Update` after `DirectVoice.Update` and patch a prefix on
  `VoIpManager.OnPhotonEvent` (`GMFCCIFGKDN`). The prefix drops the event, releasing the `PooledReader` as
  `PvpRelay` does, when `__0 == 125 && RevivalPlayback.Live(__2)`.

**Interfaces:**
- **Consumes:** `DirectVoice.Peers()`, `VoiceCapture.TryTakePacket/Stream/PacketLossPercent`, `VoiceWire`,
  `NetSim`, `RevivalPlayback.Receive`.
- **Produces:**

```csharp
internal static class RevivalVoice {
  internal static bool Active { get; }                 // Steam mode && Engine == Revival
  internal static bool SendsTo(ulong steamId);         // peer is Direct && Revival (thread-safe snapshot, read by the send hook)
  internal static void Update();                       // main thread
}
```

**`Update` does:**
1. Refresh the snapshot of Revival peers from `DirectVoice.Peers()`.
2. Drain `VoiceCapture.TryTakePacket` and `SendP2PPacket` each packet to every Revival peer on channel 4.
3. Receive on channel 4: up to 400 packets per frame, only from Steam IDs in `Peers()`, through
   `NetSim.Parse(DebugNetSim)` if it's set. Voice packets go to `RevivalPlayback.Receive(actor, packet,
   nowMs)`. Report packets update that peer's reported loss.
4. Every 1 s, send each Revival peer a report with that speaker's `JitterStats.LossPercent` and `JitterMs`
   from `RevivalPlayback`.
5. Set `VoiceCapture.PacketLossPercent = clamp(2 × max reported loss + 5, 5, 30)`.
6. Every 30 s, log one line per peer:
   - mode (Revival / game voice, and why: no mod / old mod / not direct)
   - packets sent and received
   - the reported loss

- [ ] **Step 1: Implement, and build.**
- [ ] **Step 2: Run a session with one modded crewmate.** Expected:
  - The log shows `Revival voice: <name> now on Revival voice`.
  - Each side hears the other clearly, once and not doubled.
  - The 30 s reports show packets flowing both ways.
- [ ] **Step 3: Check the fallback.** Set `Engine = Game` on one side only. Expected: both fall back to game
  voice for each other within about 6 s (the hello timeout), still single and clear.
- [ ] **Step 4: Check a rejoin.** The crewmate leaves and rejoins. Expected: within about 2 s of their
  first words they're heard on Revival, with no double voice and no silence stuck past one burst.
- [ ] **Step 5: Commit** "Revival voice: sent straight between modded crewmates on Steam channel 4, game
  voice off between them, loss reports back to the speaker".

## Task 10: Playback

**Files:**
- Create: `src/Voice/RevivalPlayback.cs`
- Modify: `src/VoiceJitter.cs`. Add `internal static void Bypass(WwiseAudioPlayer player, DaikonForge.VoIP.DHEDECPPLGP<float> samples)`,
  which calls `BufferAudio` with `handingOver = true`, so the cushion leaves Revival audio alone.

**Interfaces:**
- **Consumes:** `JitterBuffer`, `OpusDecoder`, `Resampler` (only if `PlaybackRate` ≠ the decode rate, which
  Task 1 decides; normally unused), `VoiceJitter.Bypass`, `PvpRelay.Hears` (Task 11; until then, always
  true).
- **Produces:**

```csharp
internal static class RevivalPlayback {
  internal static void Receive(int actor, VoicePacketIn packet, double nowMs); // main thread
  internal static bool Live(int actor);                // a Revival packet from actor within the last 1000 ms
  internal static JitterStats Stats(int actor);        // for reports
  internal static void Update();                       // main thread: push decoded audio to players
  internal static string Describe(int actor);          // the 30 s report line
}
```

**Design:**
- **Per actor:** a `Speaker` holds a `JitterBuffer`, an `OpusDecoder(PlaybackRate)`, and an output queue
  (`Queue<float[]>` of frames, locked).
- **One playout thread** for all speakers ticks on a `Stopwatch`. Every 20 ms of elapsed time, it calls
  `Pop` on each speaker that has a buffer and enqueues the frame. It catches up if late, but by at most 5
  ticks.
- **Main thread, in `Update`:** for each speaker with queued frames, find its `WwiseAudioPlayer` the way
  `DirectVoice.Deliver` finds the voip player. Use `_voipPlayers[actor].VoiceController`'s
  `_audioOutputDevice`; look up the field name in `names.map`.
  - If there's no player, discard the queue.
  - Otherwise gate it, as `UDPNetworkVoiceController.VoIPEventHandler` does: `VoIpManager.CanHear(viewID)`,
    `NovaPlayerManager.GetInfoForPlayer(player) != null`, the controller's `Mute` false, and
    `PvpRelay.Hears(actor)`.
  - Then apply volume = `GetChannelVolume(viewID) × GetVolumeForPlayer(player)`.
  - On the first frame of a burst, call `SetSampleRate(PlaybackRate)` and set `Equalize = false`. Restore
    `Equalize` when `Live` turns false.
  - Push frames with `VoiceJitter.Bypass` while the player's queued-ahead audio is below 60 ms
    (`PlayerQueueMs`), counted from what we pushed minus the player's played position (`LECNEIIHLJM`
    − (`_played` + `timeSamples`), per `decompiled/_/WwiseAudioPlayer.cs:247-290`). If the output queue
    holds more than 160 ms when a new burst starts, drop the oldest frames down to 60 ms.
- **Every 30 s,** one line per speaker, with all `JitterStats` fields, for example: `Revival voice from
  <name>: last 30 s, received 1480, lost 12 (redundancy 9, FEC 2, concealed 1), late 3, underruns 0,
  jitter 18 ms, buffer 60 ms`.

- [ ] **Step 1: Implement, and build.**
- [ ] **Step 2: Run a session with one crewmate, `DebugNetSim = "loss=5,reorder=2,jitter=50"` on your
  side.** Expected:
  - They sound clean.
  - The report shows losses recovered mostly by redundancy and FEC.
  - Concealed frames make up under 1% of received.
  - Lip sync and positional sound are there.
  - The escape-menu volume slider and mute act on them.
- [ ] **Step 3: Change to `"loss=15,burst=2,jitter=80"`.** Expected: still intelligible. Note the
  reported buffer.
- [ ] **Step 4: Check hitches.** Temporarily bind F12 to `Thread.Sleep(300)` on the main thread (remove it
  before committing) and press it mid-sentence. Expected: one short glitch at most. By the next burst, the
  reported buffer is back near target.
- [ ] **Step 5: Check a scene change.** Talk across briefing room → bridge → briefing room. Expected: no
  exceptions in the log, and voice resumes in each scene.
- [ ] **Step 6: Commit** "Revival voice: playback through the game's voice player with the adaptive buffer;
  volume, mute and who-can-hear-whom honoured".

## Task 11: PvP filter shared with Revival voice

**Files:**
- Modify: `src/PvpRelay.cs`. Extract the decision in `VoiceChat_Prefix` and `LobbyVoice` (lines 79-103)
  into `internal static bool Hears(int actor)`. It returns false exactly where the prefix currently drops
  the packet. The prefix calls it, keeping its one-time log line and the release of the reader.
- Modify: `src/Voice/RevivalPlayback.cs`, to call `PvpRelay.Hears(actor)` in its gate.

- [ ] **Step 1: Do the extraction, and build.**
- [ ] **Step 2: Run a PvP test (`src/Missions/PvpTest.cs`) with one player per ship.** Expected:
  - On the bridge, the other ship's player isn't heard (the log line appears once).
  - In the PvP lobby with TABLE CHAT, players at the other table aren't heard.
  - With ROOM chat, they are heard.
  - The same holds with `Engine = Game` on both, to show the extraction kept the old behaviour.
- [ ] **Step 3: Commit** "PvP: who hears whom decided in one place, for game voice and Revival voice".

## Task 12: Words and release readiness

**Files:**
- Modify: `README.md`. Under the voice chat feature, describe the new voice, `Engine = Game` to go back,
  and the `native/rnnoise.dll` part of the install.
- Modify: `docs/index.html`, only where voice chat is described (match the existing tone).
- Modify: the memory file `next-release-notes.md`
  (`/home/patrick/.claude/projects/-home-patrick-stbc/memory/`). Add a "Voice chat" section for the next
  release notes: what changed, everyone needs the new version to benefit, and `Engine = Game` to opt out.

- [ ] **Step 1: Write the text.**
- [ ] **Step 2: Run a full session check with two or more modded crewmates, default settings, no
  `DebugNetSim`, about 30 minutes of normal play.** Expected: no chop reports, and the 30 s reports show
  `concealed` near 0 and `underruns` rare.
- [ ] **Step 3: Commit** "README and site: Revival voice chat". Releasing stays the user's step
  (`tools/release.sh`, one push, per memory).

---

## Self-review notes

- **Spec coverage:**

  | Spec item | Task |
  |---|---|
  | Capture (NS, AGC, VAD, pre-roll, 48 kHz, one recording) | Task 8 |
  | Encoding | Tasks 3 and 8 |
  | Transport, redundancy, reports, `PACKET_LOSS_PERC` tuning | Task 9 |
  | Receive buffer | Task 5 |
  | Playback (lip sync, 3D, volume, mute, `CanHear`, Equalize off) | Task 10 |
  | PvP filter | Task 11 |
  | Settings | Task 8 (`Plugin.cs`) |
  | Diagnostics | Tasks 9 and 10 |
  | Fallback and handover | Task 9 |
  | Native-library fallback | Task 7 |
  | Proton | Task 7 step 4 |
  | First proof | Task 1 |
  | Success check | Task 10 steps 2–3, Task 12 step 2 |

- **Background receive thread:** the spec leaves it to measurement. Task 10 step 4 is that measurement.
  Main-thread receive stays unless the hitch check fails.
