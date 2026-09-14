import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)
decoder.detail = True

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

start = 0x1822D9AFD
end = 0x1822DA400  # just past target 0x1822DA3FC
off = va_to_off(start)
length = end - start

print(f"=== disassembly {hex(start)} .. {hex(end)} (known-fired -> target), looking for branches ===")
for ins in decoder.disasm(data[off:off+length], start):
    tag = ""
    if ins.mnemonic.startswith("j") or ins.mnemonic in ("call", "ret"):
        tag = "  <-- CONTROL FLOW"
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{tag}")
