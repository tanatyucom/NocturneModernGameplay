import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

func_start = 0x1822DB3A0
scan_end = 0x1822DB846  # up to the cmpDrawSkill call site
off = va_to_off(func_start)
length = scan_end - func_start

targets = ["rsp + 0xd0", "rsp + 0xd8", "rsp + 0x108"]

print(f"=== ALL instructions in {hex(func_start)}..{hex(scan_end)} touching 0xD0/0xD8/0x108 (any access) ===")
for ins in decoder.disasm(data[off:off+length], func_start):
    ops = ins.op_str.lower()
    if any(t in ops for t in targets):
        # is it a write (destination operand) or read?
        first_operand = ops.split(",")[0]
        kind = "WRITE" if any(t in first_operand for t in targets) else "read"
        print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}   [{kind}]")
