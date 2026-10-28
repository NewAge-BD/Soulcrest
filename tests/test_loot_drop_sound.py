import importlib.util
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("generate_loot_drop", ROOT / "scripts" / "sound" / "generate_loot_drop.py")
generator = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(generator)


class LootDropSoundTests(unittest.TestCase):
    def test_generator_reproduces_the_embedded_cue_byte_for_byte(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp) / "cue.wav"
            generator.write(target, generator.render())
            embedded = ROOT / "src" / "Soulcrest.App" / "Assets" / "loot-drop-standard.wav"
            self.assertEqual(embedded.read_bytes(), target.read_bytes())

    def test_cue_starts_promptly_and_ends_silently(self):
        samples = generator.render()
        self.assertEqual(0.0, samples[0])
        self.assertEqual(0.0, samples[-1])
        onset = next(index for index, value in enumerate(samples) if abs(value) > 0.01)
        self.assertLess(onset / generator.SAMPLE_RATE, 0.05)
        self.assertAlmostEqual(generator.PEAK, max(abs(value) for value in samples), places=6)


if __name__ == "__main__":
    unittest.main()
