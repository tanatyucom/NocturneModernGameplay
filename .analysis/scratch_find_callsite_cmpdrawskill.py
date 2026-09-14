import struct
import pefile

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

def off_to_va(off):
    return image_base + pe.get_rva_from_offset(off)

target = 0x1822D97C0

# scan for E8 rel32 call instructions whose target == cmpDrawSkill entry
hits = []
for i in range(len(data) - 5):
    if data[i] == 0xE8:
        rel = struct.unpack_from("<i", data, i + 1)[0]
        call_va = off_to_va(i) + 5 + rel
        if call_va == target:
            hits.append(off_to_va(i))

print(f"found {len(hits)} direct call sites to 0x{target:X}:")
for h in hits:
    print(f"  0x{h:X}")
