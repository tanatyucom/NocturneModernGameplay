import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

path = r"C:\Program Files (x86)\Steam\steamapps\common\smt3hd\GameAssembly.dll"
pe = pefile.PE(path)
data = open(path, "rb").read()
image_base = pe.OPTIONAL_HEADER.ImageBase
decoder = Cs(CS_ARCH_X86, CS_MODE_64)

def va_to_off(va):
    return pe.get_offset_from_rva(va - image_base)

target = 0x18138BE10

# 1) backward scan for int3 padding to find likely function start
off = va_to_off(target)
window = data[off-0x100:off]
i = len(window) - 1
func_start_off = None
while i >= 1:
    if window[i] == 0xCC and window[i-1] == 0xCC:
        j = i
        while j >= 0 and window[j] == 0xCC:
            j -= 1
        func_start_off = j + 1
        break
    i -= 1
if func_start_off is not None:
    func_start_va = target - (len(window) - func_start_off)
else:
    func_start_va = target - 0x100

print(f"candidate function start (int3-scan): 0x{func_start_va:X}")

print("\n=== disassembly around candidate start through target+0x60 ===")
off2 = va_to_off(func_start_va)
length = (target + 0x60) - func_start_va
for ins in decoder.disasm(data[off2:off2+length], func_start_va):
    tag = "  <-- TARGET" if ins.address == target else ""
    print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{tag}")
