# Revival voice chat: design

Date: 2026-11-08. Status: reviewed, awaiting the user's approval.

## Why

Players say in-game voice is choppy: low quality, some players jittery or nearly impossible to hear because
it keeps breaking up. The same players sound fine to each other on Discord. The aim is voice that sounds and
holds up close to Discord.

The mod already reduced the transport problems. Direct voice (`src/DirectVoice.cs`) sends frames straight
between players over Steam instead of through the host, and an adaptive cushion (`src/VoiceJitter.cs`) holds
back the start of each burst. What's left is in the game's own voice pipeline (DaikonForge VoIP with Opus,
decompiled under `decompiled/DaikonForge.VoIP`, `decompiled/_/VoIpManager.cs`,
`decompiled/_/WwiseMicrophoneInputDevice.cs`, `decompiled/_/WwiseAudioPlayer.cs`):

- **60 ms Opus frames** (`VoIpManager.OpusFrameLengthMilliseconds = 60`). Each lost or late packet loses a
  syllable.
- **24 kbps at 24 kHz** (`LEPEJAMKOJC(24000)`, `WindowsVoipInputMode = SuperWide`), with the Opus encoder in
  VoIP mode.
- **In-band FEC is off.** The encoder never sets `ForwardErrorCorrection`, so a lost frame is only guessed
  (`GenerateMissingFrame`, Opus PLC).
- **No reordering.** `VoicePacketRingBuffer` is FIFO, and `VoiceControllerBase.DecodeThreadUpdateFunc` drops
  any packet whose index is below the next one expected. Now that voice travels unreliably, a packet that
  arrives late counts as lost: one guessed frame, then the real one thrown away.
- **Mic path.** The Unity `Microphone` is clamped to 24 kHz and resampled by linear interpolation
  (`ResampleWithInterpolation`). There's no noise suppression or gain control. Voice detection is a single
  amplitude threshold (`AudioInputDeviceBase.IsTalking`), which clips quiet speakers and word starts.

## Decision

Replace the game's voice between modded players with our own pipeline, called **Revival voice** in this
document. Every player in these sessions runs the mod (the user's call), so there's no compatibility
constraint between modded players.

## Goals

- Voice between modded players that stays smooth on a typical home connection with a few percent of packets
  lost, late or out of order: no audible chop under conditions where Discord holds up.
- Clear, full-sounding speech: Opus fullband (48 kHz) at about Discord's bitrate.
- Quiet and soft-spoken players come through at a normal level, and background noise (fans, keyboards, VR
  headset hiss) is suppressed.
- Low enough delay for bridge callouts: about 150 ms or less mouth-to-ear on a good link, while smoothness
  takes priority on a bad one.
- Everything the game does with voice still works: lip sync, positional voice on the bridge, per-player
  volume and muting (`src/PlayerVolumes.cs`), who-can-hear-whom (`VoIpManager.CanHear`, PvP table and room
  channels), and voice commands reading the mic (`src/VoiceCommands.cs`).
- Per-player numbers in the log every 30 s, so a choppy session can be diagnosed from the logs players send.

## Non-goals

- Self-hosted server and Cloud modes. In those, voice stays the game's own (with the current cushion).
  Revival voice runs only in Steam mode (the default), where direct links already exist. The user confirmed
  every choppy session was in Steam mode.
- Talking to players without the mod. They keep the game's voice path (direct voice via the host, as now).
- Echo cancellation. Nobody has reported echo, and most players are in VR headsets. The design shouldn't
  rule it out later.
- New voice UI beyond what's needed (see the settings section).

## Anti-goals: what would count as failure even if it worked

- Players hear someone twice (game voice and Revival voice) or not at all during the handover.
- The game freezes or hitches. Encoding, decoding and noise suppression must stay off the main thread, as the
  game's own encode and decode threads are.
- Voice commands or the mic check stop working because the mic was taken from them. This has already
  happened once (commit c903fa8): only one recording of the mic, shared.
- A crash on a machine without the new native library. If it's missing or fails to load, fall back to the
  game's voice and log why.

## How it works

### Who uses it

Each modded game advertises Revival voice support, and its version, in the direct-voice hello on Steam
channel 3, or in its own hello on a new channel. That's the builder's call. For each crewmate:

- **Both sides support it and the direct link is confirmed both ways:** send Revival voice, and stop the
  game's voice frames to that crewmate. `DirectVoice.SendEvent_Prefix` already shows how to remove
  crewmates from the game's send.
- **Otherwise:** the game's voice as now.

Switching happens per crewmate with no gap and no double audio. Crewmates' game voice is dropped on receipt
only while a Revival stream from that player is live.

### Capture

- 48 kHz mono from the microphone the game's voice chat uses, honouring its device choice and mute. One
  recording is shared with voice chat and voice commands. Raising the game's own mic clip to 48 kHz
  (`WindowsVoipInputMode` to the 48 kHz mode, or the Microphone.Start frequency) versus capturing
  separately is the builder's call. The hard constraint is never calling `Microphone.Start`/`End` on a mic
  someone else is recording.
- Processing, in order, on a worker thread:
  1. **Noise suppression with RNNoise** (480-sample frames at 48 kHz, matching 10 ms).
  2. **Automatic gain:** a slow-moving gain toward a target speech level, capped so noise isn't pumped up,
     computed only while speech is detected.
  3. **Voice activity detection** from RNNoise's speech probability, with about 60 ms pre-roll (audio from
     just before speech starts is sent too) and about 300 ms hang time. The pre-roll is kept short on
     purpose: it adds its own length to the delay of the whole burst, since the receiver doesn't speed
     audio up. That makes it 60 ms, not 200 ms (Claude's call; snappy callouts over perfect first
     consonants).

     This replaces the amplitude threshold for deciding when to send. `VoIpManager.LocalPlayerIsTalking`
     (used for the talking indicator, and possibly lip sync on others' screens) should report the new
     decision.
- When the player is muted in the game, nothing is sent.

### Encoding

- Opus through the game's own `opus.dll`, which exports the full libopus API including `opus_encoder_ctl`,
  called directly by P/Invoke with our own settings:
  - 48 kHz mono, 20 ms frames, `OPUS_APPLICATION_VOIP`
  - Bitrate about 48 kbps, VBR on
  - `OPUS_SET_INBAND_FEC(1)` and `OPUS_SET_PACKET_LOSS_PERC` tracking the loss each listener reports back
    (start at 10, floor 5, cap about 30)
  - DTX off while VAD is driving transmission (VAD already stops sending in silence)
- Each packet carries a sequence number, a sample timestamp and a stream ID that changes when the speaker
  restarts their mic or rejoins. Layout is the builder's call.

### Transport

- Steam P2P (the same `SteamNetworking` API `DirectVoice` uses), unreliable and no delay, on its own channel
  or a new message type on channel 3.
- **Redundancy:** each packet also carries the previous frame's Opus data (low-bitrate copy or the full
  frame; the builder chooses). One lost packet is then always fully recovered, and in-band FEC covers the
  rest. This is what holds up against short bursts of loss, which is what Steam relays tend to produce.
- Listeners report their loss and jitter back to each speaker about once a second, so the speaker can tune
  `PACKET_LOSS_PERC` and redundancy.
- Receive on the main thread as now, or on a background thread if hitches on the receiving side prove to
  matter. Builder's call, but it must be decided from measurements (the report below), and Steam calls off
  the main thread must be shown to be safe with this Steamworks build.

### Receive buffer

One per speaker, replacing `VoiceJitter`'s cushion for Revival streams:

- **Reorders** by sequence number. Duplicates and packets older than what's already played are dropped.
- **Adaptive target delay** from measured arrival jitter, for example a high percentile of recent
  inter-arrival deviation plus one frame. It starts around 60 ms, is bounded at roughly 40–300 ms, and only
  changes depth in silence or by tiny steps, so speech isn't warped. The algorithm is the builder's call;
  WebRTC NetEq and Opus's own guidance are the reference designs.
- **When a frame is missing at play time,** in this order:
  1. Decode it from the redundant copy in a later packet that has already arrived.
  2. Decode with FEC from the next packet (`opus_decode(..., decode_fec=1)`).
  3. Opus PLC. After about 100 ms of PLC with nothing arriving, fade out instead of buzzing.
- Decoding happens off the main thread.

### Playback

Through the speaker's existing `WwiseAudioPlayer` via `BufferAudio`, so lip sync, the 3D position on the
bridge, per-player volume, mute and `CanHear` all still apply. Revival voice should set what the game's own
receive path would have set (`SetSampleRate`, the received counters) so the player behaves as for game
voice.

Whether that player and Wwise's audio-input plugin accept 48 kHz must be verified first. If not, resample
the decoded audio to the player's rate with a proper resampler (not linear interpolation), or have Opus
decode straight to that rate (it can decode at 24 kHz). This is the first thing to prove (see risks).

- **PvP per-ship voice** is filtered by a prefix on the game's voice-received event (`src/PvpRelay.cs`,
  `VoiceChat_Prefix`), not by `CanHear`. Revival voice bypasses that event, so it must apply the same filter
  itself.
- **The player's own volume equalising** (`WwiseAudioPlayer.Equalize`) should be off for Revival streams so it
  doesn't fight the speaker-side auto-gain (Claude's call; the builder can verify by ear).

### Settings (BepInEx config, `[Voice]`)

- `Engine = Revival | Game`, default `Revival` from the first release (the user's decision). `Game` turns all
  of this off. Because it's on for everyone at once, the release must have the full safeguards: the
  per-crewmate handover in both directions, the PvP filter, falling back cleanly when the native library is
  missing, and loading under Proton, all proven before release.
- `NoiseSuppression = true`
- `AutoGain = true`
- `Bitrate = 48` (kbps)
- The existing `JitterBufferMs` and `DirectVoice` stay and still govern the game-voice fallback.

### Diagnostics

Every 30 s, per crewmate, in the same style as the existing reports:

- packets received, lost, late, recovered from redundancy, recovered from FEC, concealed (PLC)
- current buffer target and actual delay
- measured jitter
- whether the stream is Revival or game voice, and why if it's game voice

Also for your own mic: speech time, the gain applied and the noise-suppression level.

## Constraints

- BepInEx/Harmony plugin, C#, same build as the rest of `src/`. The game is a 64-bit Windows Unity build,
  also run under Proton.
- RNNoise must ship as a 64-bit Windows native DLL alongside the plugin, built from upstream source by us
  (for example with mingw-w64 in the build tools) rather than taken from an unknown binary. It must be
  added to the installer (`docs/install.*`) and the release packaging.
- No DLL swaps while the game runs (they freeze it). Testing needs the game closed between builds.
- Obfuscated names come from `tools/xref` names.map, as elsewhere in `src/`.

## Risks and the first proof

1. **Wwise playback at 48 kHz.** Prove first that a 48 kHz (or resampled) stream fed through
   `WwiseAudioPlayer.BufferAudio` plays cleanly, with lip sync. Two clients on one machine are enough
   (`stbc-world/run/inst1`, `inst2`).
2. **Shared mic at 48 kHz** without breaking voice commands or the mic check.
3. **RNNoise native build** loading in the game under Windows and Proton.

If (1) fails outright, the fallback is decoding at the player's native rate. That's still a large
improvement, since loss handling and bitrate fix the chop.

## Success check

- Two local instances with artificial loss and jitter injected in the receive path (a debug setting): 5%
  random loss, 2% reordering and 50 ms jitter should sound clean; 15% loss in bursts should stay
  intelligible.
- A real session with the players who complained: no reports of chop, and the 30 s reports show losses
  being recovered rather than concealed.

## Left to the builder

- Packet layout, channel choice, and whether the hello is extended or new
- The jitter-buffer algorithm, within the bounds above
- The form of redundancy (full previous frame or low-bitrate copy)
- Whether to receive on a background thread (decided by measurement)
- Raising the game's mic clip to 48 kHz versus a separate shared capture (keeping the one-recording rule)
- Code structure: new files under `src/Voice/` are suggested, with `DirectVoice` and `VoiceJitter` kept for
  the fallback

## Calls made by Claude (not discussed with the user)

- Steam mode only. Self-hosted and Cloud keep game voice.
- RNNoise for noise suppression, with no echo cancellation for now.
- 48 kbps, 20 ms frames, redundancy plus FEC.
- The design document lives in `specs/` at the repo root, because `docs/` is the published website.
