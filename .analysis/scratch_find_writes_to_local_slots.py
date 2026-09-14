import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

func_start = 0x1822DB3A0  # cmpDrawStatusComEx2 entry, per IL2CPP methodPointers
call_site = 0x1822DB846
off = va_to_off(func_start)
length = call_site - func_start

targets = ["rsp + 0xd0", "rsp + 0xd8", "rsp + 0x108"]

print(f"=== scanning {hex(func_start)} .. {hex(call_site)} for writes/reads to caller slots 0xD0/0xD8/0x108 ===")
for ins in decoder.disasm(data[off:off+length], func_start):
    ops = ins.op_str.lower()
    if any(t in ops for t in targets):
        print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}")
