import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

func_start = 0x1822D97C0   # cmpDrawStatus.cmpDrawSkill, per IL2CPP methodPointers
target = 0x1822DA3FC
next_method = 0x1822DB3A0  # cmpDrawStatus.cmpDrawStatusComEx2, next VA in methodPointers

# 1) confirm prologue shape at func_start
off0 = va_to_off(func_start)
print("=== prologue at func_start ===")
count = 0
for ins in decoder.disasm(data[off0:off0+64], func_start):
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}")
    count += 1
    if count >= 12:
        break

# 2) scan the whole region [func_start, next_method) for ret followed by
#    a run of >=3 int3 (0xCC) bytes -- that would indicate a real function
#    boundary INSIDE what metadata claims is a single method (e.g. a
#    hot/cold split producing two physically separate stretches).
print("\n=== scanning for ret+int3-padding boundaries between func_start and next_method ===")
off1 = va_to_off(func_start)
off2 = va_to_off(next_method)
window = data[off1:off2]
i = 0
found_any = False
while i < len(window) - 3:
    if window[i] == 0xC3 and window[i+1] == 0xCC and window[i+2] == 0xCC and window[i+3] == 0xCC:
        va = func_start + i
        print(f"ret+CCCC boundary candidate at VA 0x{va:X} (offset from func_start: 0x{i:X}, target is at 0x{target-func_start:X})")
        found_any = True
    i += 1
if not found_any:
    print("no ret+int3x3 boundary found between func_start and next_method -- region is one contiguous block")

print(f"\nfunc_start=0x{func_start:X} target=0x{target:X} next_method=0x{next_method:X}")
print(f"target offset from func_start: 0x{target-func_start:X}  ({target-func_start} bytes)")
print(f"region size func_start..next_method: 0x{next_method-func_start:X} bytes")
