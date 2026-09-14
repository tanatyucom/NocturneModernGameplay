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

# CursorPosShiftWriteWatchTrace (hardware WRITE breakpoint on
# CursorPos.Shift, +0x14) caught the two writers that set Shift=8 (the
# value HighlightTargetGateTrace already CONFIRMED explains the missing
# highlight - see 01_CURRENT_STATE.md / PLAN.md). Both are logged as
# "writerNextInsnVa" - the RIP AFTER the writing instruction (a data
# breakpoint traps post-write) - so the actual write instruction is
# whatever immediately precedes that address. Disassemble backward from
# each to find it.
targets = {
    "native seq8 path (bridgeActive=False)": 0x182288AC5,
    "bridge seq21 path (bridgeActive=True)": 0x182289317,
    # also grab the plain +1 increment writer and the 7->0 wraparound
    # writer for direct comparison (neither ever produces 8):
    "normal +1 increment (0..7)": 0x1822EDE25,
    "7->0 wraparound": 0x1822EDEE0,
}

for label, next_insn_va in targets.items():
    start = next_insn_va - 0x60
    length = 0x80
    off = va_to_off(start)
    print(f"=== {label}: writerNextInsnVa=0x{next_insn_va:X} ===")
    for ins in decoder.disasm(data[off: off + length], start):
        marker = "  <== writerNextInsnVa (trap RIP, AFTER the write)" if ins.address == next_insn_va else ""
        print(f"{ins.address:016X}  {ins.bytes.hex():24} {ins.mnemonic:8} {ins.op_str}{marker}")
        if ins.address > next_insn_va:
            break
    print()
