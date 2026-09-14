import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

start = 0x1822DB3A0
end = 0x1822DB420
off = va_to_off(start)
length = end - start

print(f"=== comEx2 {hex(start)}..{hex(end)}, leading up to [rsp+0x60] write ===")
for ins in decoder.disasm(data[off:off+length], start):
    tag = "  <-- WRITE [rsp+0x60] (source of cmpDrawSkill's r13)" if ins.address == 0x1822DB41A else ""
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{tag}")
