import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

start = 0x1822D97C0
end = 0x1822D9AFD
off = va_to_off(start)
length = end - start

print(f"=== full prologue+setup {hex(start)} .. {hex(end)}, looking for ebp/esi/rbp/rsi defs ===")
for ins in decoder.disasm(data[off:off+length], start):
    ops = ins.op_str
    interesting = any(r in ops.split(",")[0] for r in ("ebp", "rbp", "bpl", "esi", "rsi", "sil")) and ins.mnemonic.startswith("mov")
    tag = "  <-- WRITES ebp/rbp/esi/rsi" if interesting else ""
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{tag}")
