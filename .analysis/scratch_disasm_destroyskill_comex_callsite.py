import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

func_start = 0x1822824D0  # rstdraw.rstDrawSeqDestroySkill entry
call_site = 0x18228289D
off = va_to_off(func_start)
length = call_site - func_start + 8

print(f"=== rstDrawSeqDestroySkill {hex(func_start)} .. call site {hex(call_site)} (into cmpDrawStatusComEx) ===")
for ins in decoder.disasm(data[off:off+length], func_start):
    ops = ins.op_str
    tag = ""
    if any(f"rsp + {h}" in ops for h in ["0x20","0x28","0x30","0x38","0x40","0x48","0x50","0x58","0x60"]):
        tag = "  <-- STACK ARG SLOT"
    if ins.address == call_site:
        tag = "  <-- THE CALL (comEx)"
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{tag}")
