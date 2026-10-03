import gzip, json, struct, sys
from collections import Counter

seen = set()
with open(sys.argv[1], 'rb') as f:
    assert f.read(8) == b'LCREPL01'
    while h := f.read(8):
        size, _ = struct.unpack('<ii', h)
        r = json.loads(gzip.decompress(f.read(size)))
        t = r.get('Time', 0)
        if r['Kind'] == 'header':
            print('HEADER', r['Header'].get('Metadata'))
        if r['Kind'] == 'world':
            for g in r['World'].get('Geometry', []):
                if g.get('EntityId') and g['Id'] not in seen:
                    seen.add(g['Id'])
                    print('GEO', t, {k:g.get(k) for k in ['Id','Name','EntityId','MeshName','Position','Scale','AnimatorPath','PrefabKey','PrefabRendererPath','RootBonePath']})
        if r['Kind'] == 'frame' and int(t) in [0,1,10,20,40,55,56,60] and t % 1 < .11:
            for e in r['Frame'].get('Entities', []):
                if e.get('Kind') in ['player','enemy']:
                    print('FRAME', round(t,2), {k:e.get(k) for k in ['Id','Name','Position','Scale','Bones']})
        if r['Kind'] == 'event':
            e = r['Event']
            if e.get('Category') == 'animation' and e.get('Name') == 'state' and (not e.get('Data',{}).get('layer') or e['Data']['layer']=='0'):
                print('ANIM', round(t,2), e.get('EntityId'), e['Data'])
