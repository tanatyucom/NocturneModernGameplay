import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

func_start = 0x1822DBC30  # cmpDrawStatusComEx entry
call_site = 0x1822DBCF7
off = va_to_off(func_start)
length = call_site - func_start + 8

print(f"=== full cmpDrawStatusComEx {hex(func_start)} .. call site {hex(call_site)} ===")
for ins in decoder.disasm(data[off:off+length], func_start):
    ops = ins.op_str
    tag = ""
    if "rsp + 0x20" in ops or "rsp + 0x28" in ops or "rsp + 0x30" in ops or "rsp + 0x38" in ops or "rsp + 0x40" in ops or "rsp + 0x48" in ops or "rsp + 0x50" in ops or "rsp + 0x58" in ops:
        tag = "  <-- STACK ARG SLOT"
    if ins.address == call_site:
        tag = "  <-- THE CALL (comEx2)"
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{tag}")
