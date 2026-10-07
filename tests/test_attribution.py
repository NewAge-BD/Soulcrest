import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from build_tester_data import ATTRIBUTION


class AttributionTests(unittest.TestCase):
    def test_map_footer_links_to_gaming_tools(self):
        # Condition of the data permission (gaming.tools, 2026-10-06): a visible, clickable link.
        self.assertIn('<a href="https://aion2.gaming.tools/"', ATTRIBUTION)
        self.assertIn('>aion2.gaming.tools</a>', ATTRIBUTION)


if __name__ == '__main__':
    unittest.main()
