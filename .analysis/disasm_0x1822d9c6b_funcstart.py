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

# Back up from 0x1822D9800 (mid-function context around the converged
# cmpSetupObject(true) caller at 0x1822D9C6B) looking for the previous
# function boundary (int3 padding after a ret), to find this function's
# real start VA and its prologue/parameter setup.
start = 0x1822D9400
length = 0x420
off = va_to_off(start)

for ins in decoder.disasm(data[off: off + length], start):
    line = f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}"
    print(line)
