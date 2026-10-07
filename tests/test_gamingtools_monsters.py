import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path
import cv2
import numpy as np
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts'))
from build_tester_data import soul_drops, to_pixel, read_tile_pack, write_tiles

class GamingToolsMonstersTests(unittest.TestCase):
    def test_loot_identity_overrides_misleading_monster_name(self):
        npc={'id':'1','name':'Abyss Klaw','loot':[{'item':{'id':'42','name':'Soul: Klaw Scout (Bound)'},
                                             'windows':[{'chancePercent':100}]}]}
        drops=soul_drops(npc,{'Klaw':'2','Klaw Scout':'3'})
        self.assertEqual(drops[0]['petId'],'3')
        self.assertEqual(drops[0]['sourceRefs'],['https://aion2.gaming.tools/npcs/1'])
        npc['loot']=[]
        self.assertEqual(soul_drops(npc,{'Klaw':'2'}),[])

    def test_multiple_souls_remain_visible_for_conflict_handling(self):
        npc={'id':'1','name':'Any','loot':[{'item':{'id':'a','name':'Soul: Slime (Bound)'}},
                                        {'item':{'id':'b','name':'Soul: Swarm (Bound)'}}]}
        self.assertEqual(len(soul_drops(npc,{'Slime':'1','Swarm':'2'})),2)

    def test_coordinate_projection_uses_native_image_extent(self):
        config={'imageWidth':5120,'imageHeight':5120,'bounds':{'minX':-10,'maxX':30,'minY':20,'maxY':60}}
        self.assertEqual(to_pixel(config,-10,20),(0,0))
        self.assertEqual(to_pixel(config,30,60),(5120,5120))
        self.assertEqual(to_pixel(config,10,40),(2560,2560))
        with self.assertRaises(ValueError):to_pixel({**config,'transformType':'unknown'},0,0)

    def test_gtpk_preserves_full_resolution_and_rejects_invalid_offsets(self):
        pixels=np.zeros((512,512,3),dtype=np.uint8);pixels[:256,:256]=[30,70,200]
        ok,encoded=cv2.imencode('.webp',pixels,[cv2.IMWRITE_WEBP_QUALITY,101]);self.assertTrue(ok)
        header=struct.pack('<12I',0x4b505447,2,1,512,0,0,512,512,24,48,0,0)
        entry=struct.pack('<B3xIIQI',0,0,0,72,len(encoded))
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);pack=root/'map.gtpk';pack.write_bytes(header+entry+encoded.tobytes())
            metadata=write_tiles(pack,root/'tiles')
            self.assertEqual(metadata['width'],512)
            self.assertEqual(len(list((root/'tiles').glob('*.webp'))),5)
            self.assertTrue(np.array_equal(cv2.imread(str(root/'tiles/1_0_0.webp')),pixels[:256,:256]))
            pack.write_bytes(header+struct.pack('<B3xIIQI',0,0,0,1,len(encoded))+encoded.tobytes())
            with self.assertRaises(ValueError):read_tile_pack(pack)

if __name__=='__main__':unittest.main()
