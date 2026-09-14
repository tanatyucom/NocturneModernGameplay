import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

start = 0x1800E6930
off = va_to_off(start)

print(f"=== disassembly at {hex(start)} (0x1800E6930, r13's producer call) ===")
count = 0
for ins in decoder.disasm(data[off:off+400], start):
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}")
    count += 1
    if count >= 60 or ins.mnemonic == "ret":
        break
