import struct
exec(open(r"C:\SMT3Modding\NocturneModernGameplay\.analysis\resolve_name_to_va.py").read().split("import sys")[0])

candidates = [0x1821C8C48, 0x1821C8D1F, 0x1821CCADD, 0x1822DBCF7, 0x1822DBDFB]

arr_off = methodptr_array_offset(acs_module)

# build full sorted (va, type, method) list once
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

for c in candidates:
    idx = bisect.bisect_right(vas, c) - 1
    if idx >= 0:
        va, tname, mname = all_methods[idx]
        print(f"call site 0x{c:X} -> containing method {tname}.{mname} @ 0x{va:X} (+0x{c-va:X})")
    else:
        print(f"call site 0x{c:X} -> no containing method found")
