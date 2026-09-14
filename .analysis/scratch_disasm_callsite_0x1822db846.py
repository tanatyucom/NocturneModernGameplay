import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

call_site = 0x1822DB846
start = call_site - 0x300
end = call_site + 8
off = va_to_off(start)
length = end - start

print(f"=== disassembly {hex(start)} .. call site {hex(call_site)} (cmpDrawSkill call, ComEx2 arg setup) ===")
for ins in decoder.disasm(data[off:off+length], start):
    ops = ins.op_str
    tag = ""
    if "rsp + 0x28" in ops or "rsp + 0x30" in ops or "rsp + 0x38" in ops or "rsp + 0x40" in ops or "rsp + 0x48" in ops or "rsp + 0x20" in ops:
        tag = "  <-- STACK ARG SLOT (arg5..arg10 region)"
    if ins.address == call_site:
        tag = "  <-- THE CALL"
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{tag}")
