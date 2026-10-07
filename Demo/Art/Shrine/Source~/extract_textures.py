import json, struct
p = r"Shrine_Wayside.glb"
d = open(p,'rb').read()
magic, ver, length = struct.unpack('<III', d[:12])
off=12; chunks=[]
while off < length:
    clen, ctype = struct.unpack('<II', d[off:off+8]); chunks.append((ctype, off+8, clen)); off += 8+clen
j = json.loads(d[chunks[0][1]:chunks[0][1]+chunks[0][2]].decode('utf-8'))
bin_off = chunks[1][1]
names = {0:'basecolor.png', 1:'metallicroughness.png'}
for i,im in enumerate(j['images']):
    bv = j['bufferViews'][im['bufferView']]
    o = bin_off + bv.get('byteOffset',0)
    open(names[i],'wb').write(d[o:o+bv['byteLength']])
    print("wrote", names[i], bv['byteLength'])
# png dims
import struct as s
for n in names.values():
    b=open(n,'rb').read(33)
    w,h = s.unpack('>II', b[16:24])
    print(n, w, 'x', h, 'bitdepth', b[24], 'colortype', b[25])
