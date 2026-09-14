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

# SkillCurObjNativeCallerProbe (breakpoint now on cmpUpdate.cmpSetupObject's
# own entry, VA 0x182620A80) captured a real-machine 60s window where
# indices 0,1,2,3,4,5,6 (7 of 8 normal skillCurObj[] rows) ALL called
# cmpSetupObject(true) from the exact same return address, staticVa
# 0x1822D9C6B - while index=7 never appeared with value=True at all during
# the whole window, despite the user's input sequence (4->5->6->7->6, then
# to the hidden entry) passing through it. This scan backs up from
# 0x1822D9C6B to find the start of its containing function and the
# branch/condition that decides which index gets this call.
start = 0x1822D9800
length = 0x600
off = va_to_off(start)

markers = {
    0x1822D9C6B: "<== SkillCurObjNativeCallerProbe return address (indices 0,1,2,3,4,5,6 ALL converge here)",
}

for ins in decoder.disasm(data[off: off + length], start):
    marker = markers.get(ins.address, "")
    line = f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}"
    if marker:
        line += f"  {marker}"
    print(line)
