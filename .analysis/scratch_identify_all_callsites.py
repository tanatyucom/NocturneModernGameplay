import struct
exec(open(r"C:\SMT3Modding\NocturneModernGameplay\.analysis\resolve_name_to_va.py").read().split("import sys")[0])

candidates_com = [0x1822486DE, 0x18242175C, 0x1824250E5, 0x1824253D2, 0x182426CBE]
candidates_comex = [0x182173BF0, 0x1821C7A82, 0x1821C7CCF, 0x1821C7F89, 0x1821C8803,
                     0x182282440, 0x18228289D, 0x1822829D4, 0x182282A58, 0x182282B36,
                     0x182421401, 0x18251F767, 0x182522858]

arr_off = methodptr_array_offset(acs_module)
all_methods = []
for ti in range(acs["typeStart"], acs["typeStart"] + acs["typeCount"]):
    td = parse_typedef(ti)
    if td["methodStart"] == -1 or td["method_count"] == 0:
        continue
    for mi in range(td["methodStart"], td["methodStart"] + td["method_count"]):
        local_idx = mi - ACS_BASE
        if local_idx < 0 or local_idx >= acs_module["methodPointerCount"]:
            continue
        va = struct.unpack_from("<Q", data, arr_off + local_idx * 8)[0]
        if va == 0:
            continue
        md = parse_methoddef(mi)
        all_methods.append((va, get_string(td["nameIndex"]), get_string(md["nameIndex"])))
all_methods.sort()
import bisect
vas = [m[0] for m in all_methods]

def identify(c):
    idx = bisect.bisect_right(vas, c) - 1
    if idx >= 0:
        va, tname, mname = all_methods[idx]
        return f"{tname}.{mname} @ 0x{va:X} (+0x{c-va:X})"
    return "?"

print("=== callers of cmpDrawStatusCom ===")
for c in candidates_com:
    print(f"  0x{c:X} -> {identify(c)}")

print("\n=== callers of cmpDrawStatusComEx (direct) ===")
for c in candidates_comex:
    print(f"  0x{c:X} -> {identify(c)}")
