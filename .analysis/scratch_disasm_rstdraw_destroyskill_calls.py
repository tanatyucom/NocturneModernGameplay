import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

start = 0x182283060
end = 0x182283130
off = va_to_off(start)
length = end - start

print(f"=== rstDraw around the two rstDrawSeqDestroySkill call sites {hex(start)}..{hex(end)} ===")
for ins in decoder.disasm(data[off:off+length], start):
    tag = ""
    if ins.address in (0x182283102, 0x182283115):
        tag = "  <-- CALL rstDrawSeqDestroySkill"
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{tag}")
