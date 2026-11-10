# Revival voice: checks before merging

Nothing below has been run yet. Close the game before deploying any build: replacing the DLL under a running
game freezes it.

## 1. Build RNNoise (once)

```bash
sudo systemctl start docker
tools/rnnoise/build.sh
```

The script checks the tarball's SHA-256, the DLL's imports and its three exports. If it fails, the
libtool flags in the script are the first suspect. If `rnnoise_create`'s signature in 0.2 differs from
`src/Voice/RnNoise.cs`, fix the binding.

## 2. Blocking: 48 kHz through Wwise (plan Task 1)

Nothing tells Wwise's audio-input plugin the rate, so check:

- (a) At startup, the log shows `capturing … at 48000 Hz`. If it shows another rate, the resampler covers it.
- (b) Game voice still works both ways with a crewmate on `Engine = Game`.
- (c) Voice commands still work.
- (d) With both on Revival (the default): the crewmate sounds right, at normal pitch and speed, from your
  avatar's position, with lips moving.

If (d) is high or low pitched, or too fast or slow, set `VoiceFormat.PlaybackRate`
(`shared/Voice/VoiceWire.cs`) to the rate the player plays correctly, for example 24000. Opus then decodes
straight to it. Record the outcome in `specs/2026-11-08-voice-chat-design.md` under "Spike results".

## 3. Alone: hear yourself

Set `[Voice] DebugLoopback = true`, talk for 10 s, then listen to `BepInEx/voice-loopback.wav`:

- clear
- fan and keyboard noise reduced
- quiet speech lifted
- first syllables not clipped

## 4. With one crewmate (both on this build)

- The log shows `Revival voice: <name> now on Revival voice`. Each of you hears the other once, not doubled.
- **Mute** in the game's menu while talking: you stop being heard. Unmute: you're heard again.
- **Change mic** in the menu: capture follows, and voice commands still work while talking.
- Set `Engine = Game` on one side: both fall back to game voice within a few seconds, single and clear.
- The crewmate leaves and rejoins: heard again within about 2 s of speaking, with no double voice.
- **Volume slider and mute** in the escape menu act on them.
- **Scenes:** briefing room → bridge → briefing room, with voice in each and no exceptions in the log.
- **Handover while talking:** switch one side's Engine mid-sentence. Voice carries on (after up to 1 s of
  gap) with no `MissingReferenceException` in the log.
- **`DebugNetSim = "loss=5,reorder=2,jitter=50"`** on your side: they sound clean. The 30 s report shows
  losses recovered mostly by redundancy and FEC, with concealed well under 1% of received.
- **`DebugNetSim = "loss=15,burst=2,jitter=80"`:** still understandable.

## 5. PvP

One player per ship:

- On the bridge, the other ship isn't heard.
- In the lobby with TABLE CHAT, the other table isn't heard. With ROOM chat, it is.
- The same holds with `Engine = Game` on both.

## 6. A real session

About 30 minutes with two or more crewmates, default settings, no `DebugNetSim`:

- no reports of chop
- the 30 s reports show concealed near 0 and underruns rare
- the buffer and actual delay stay low, without creeping up over the session
