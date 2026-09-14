import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

func_start = 0x1822DB3A0  # cmpDrawStatusComEx2 entry
scan_end = 0x1822DB846    # the cmpDrawSkill call site itself
off = va_to_off(func_start)
length = scan_end - func_start

print(f"=== all instructions in {hex(func_start)}..{hex(scan_end)} touching [rsp+0x60] ===")
for ins in decoder.disasm(data[off:off+length], func_start):
    ops = ins.op_str.lower()
    if "rsp + 0x60" in ops or "rsp + 0x60]" in ops:
        first_operand = ops.split(",")[0]
        kind = "WRITE" if "rsp + 0x60" in first_operand else "read"
        print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}   [{kind}]")
