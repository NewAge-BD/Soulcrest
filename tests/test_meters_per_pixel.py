import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from build_tester_data import meters_per_pixel


class MetersPerPixelTests(unittest.TestCase):
    def test_altgard_bounds_give_one_metre_per_pixel(self):
        # Altgard: 8192 px for +-408000 world units (centimetres), tile pack max zoom 4 -> 8192 px image.
        config = {'bounds': {'minX': -408000, 'maxX': 408000, 'minY': -408000, 'maxY': 408000},
                  'imageWidth': 8192, 'imageHeight': 8192, 'maximumZoom': 4}
        self.assertAlmostEqual(meters_per_pixel(config, 8192), 0.996094, places=5)

    def test_half_the_pixels_double_the_metres(self):
        config = {'bounds': {'minX': -408000, 'maxX': 408000, 'minY': -408000, 'maxY': 408000},
                  'imageWidth': 8192, 'imageHeight': 8192, 'maximumZoom': 4}
        self.assertAlmostEqual(meters_per_pixel(config, 4096), 2 * 0.996094, places=5)


if __name__ == '__main__':
    unittest.main()
