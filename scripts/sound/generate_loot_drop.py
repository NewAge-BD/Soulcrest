"""Synthesises Soulcrest's standard loot-drop cue: a short, upward "plop".

Original Soulcrest synthesis (user request 2026-10-28): a sine voice glides from about 480 Hz to
1060 Hz, settles slightly, and decays within roughly 0.2 s. A quiet second harmonic adds body and a
very short filtered noise click marks the onset. No recorded or third-party audio is used.

Usage: python scripts/sound/generate_loot_drop.py [output.wav]
The default output is the embedded resource src/Soulcrest.App/Assets/loot-drop-standard.wav.
"""
import math
import struct
import sys
import wave
from pathlib import Path

SAMPLE_RATE = 48_000
DURATION = 0.21          # seconds, including the silent lead-in
LEAD_IN = 0.004          # prompt start, well inside the first 50 ms
PEAK = 0.36              # matches the previous cue's level (about -8.9 dBFS)
SEED = 20261028


def pitch(t: float) -> float:
    """S-shaped upward jump after a short low start, then a slight settle, like a popping bubble."""
    rise = 1 / (1 + math.exp(-(t - 0.0045) / 0.0018))
    settle = 1 - math.exp(-max(0.0, t - 0.012) / 0.02)
    return 520 + 600 * rise - 110 * settle


def envelope(t: float) -> float:
    """Short bright body, then a quiet ringing tail."""
    attack = min(1.0, t / 0.0015)
    body = math.exp(-max(0.0, t - 0.006) / 0.0055)
    tail = 0.09 * math.exp(-max(0.0, t - 0.006) / 0.022)
    return attack * (body + tail)


def render() -> list[float]:
    count = round(DURATION * SAMPLE_RATE)
    lead = round(LEAD_IN * SAMPLE_RATE)
    phase = 0.0
    state = SEED
    noise_lp = 0.0
    out = [0.0] * count
    for index in range(lead, count):
        t = (index - lead) / SAMPLE_RATE
        phase += 2 * math.pi * pitch(t) / SAMPLE_RATE
        voice = math.sin(phase) + 0.18 * math.sin(2 * phase + 0.6)
        # Deterministic white noise, low-passed, only during the first 6 ms.
        state = (1103515245 * state + 12345) & 0x7FFFFFFF
        white = state / 0x3FFFFFFF - 1
        noise_lp += 0.25 * (white - noise_lp)
        click = noise_lp * 0.35 * math.exp(-t / 0.0015)
        out[index] = (voice * envelope(t) + click)
    # Soft end: a 20 ms cosine fade guarantees a silent last sample.
    fade = round(0.02 * SAMPLE_RATE)
    for offset in range(fade):
        out[count - fade + offset] *= 0.5 * (1 + math.cos(math.pi * offset / (fade - 1)))
    out[-1] = 0.0
    scale = PEAK / max(abs(value) for value in out)
    return [value * scale for value in out]


def write(path: Path, samples: list[float]) -> None:
    frames = bytearray()
    for value in samples:
        pcm = max(-32767, min(32767, round(value * 32767)))
        frames += struct.pack("<hh", pcm, pcm)  # identical channels, like the previous cue
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as file:
        file.setnchannels(2)
        file.setsampwidth(2)
        file.setframerate(SAMPLE_RATE)
        file.writeframes(bytes(frames))


def main() -> None:
    default = Path(__file__).resolve().parents[2] / "src" / "Soulcrest.App" / "Assets" / "loot-drop-standard.wav"
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else default
    samples = render()
    write(target, samples)
    print(f"{target}: {len(samples)} frames, {len(samples) / SAMPLE_RATE:.4f} s")


if __name__ == "__main__":
    main()
