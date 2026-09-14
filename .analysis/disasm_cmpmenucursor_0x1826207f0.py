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

# cmpUpdate.cmpMenuCursor(Int32 idx, GameObject CursorObj, GameObject[]
# CursorList), VA 0x1826207F0 per 01_CURRENT_STATE.md / PLAN.md
# (HIDDEN_SKILL_ENTRY). SkillCurObjNativeCallerProbe's 2026-09-14 run just
# converged EVERY captured index (0,4,5,6,7,8) on the exact same return
# address, staticVa=0x182620AFD - identical to the address that earlier
# session had already resolved as inside this function. This scan confirms
# whether 0x182620AFD really lies inside cmpMenuCursor's body and what
# instruction/call is actually there.
start = 0x1826207F0
length = 0x400
off = va_to_off(start)

markers = {
    0x182620811: "<== idx vs CursorList.Length cmp",
    0x182620815: "<== jge out-of-range early return",
    0x18262081F: "<== call cmpSetupObject (early in-range path)",
    0x182620AFD: "<== SkillCurObjNativeCallerProbe return address (ALL indices converged here)",
}

for ins in decoder.disasm(data[off: off + length], start):
    marker = markers.get(ins.address, "")
    line = f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}"
    if marker:
        line += f"  {marker}"
    print(line)
