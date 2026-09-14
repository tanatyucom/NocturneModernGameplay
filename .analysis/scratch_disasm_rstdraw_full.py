import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

start = 0x182282B70  # rstdraw.rstDraw entry per metadata
end = 0x182283130    # a bit past our known case bodies
off = va_to_off(start)
length = end - start

lines = []
for ins in decoder.disasm(data[off:off+length], start):
    lines.append(ins)

print(f"=== full rstDraw {hex(start)}..{hex(end)}, flagging indirect jmp/switch-table hints ===")
for ins in lines:
    ops = ins.op_str
    tag = ""
    if ins.mnemonic == "jmp" and not ops.startswith("0x"):
        tag = "  <-- INDIRECT JMP (switch dispatch?)"
    if "jmp" in ins.mnemonic and "qword ptr" in ops:
        tag = "  <-- JMP TABLE?"
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{tag}")
