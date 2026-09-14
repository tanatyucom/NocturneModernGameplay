import struct
exec(open(r"C:\SMT3Modding\NocturneModernGameplay\.analysis\resolve_name_to_va.py").read().split("import sys")[0])

target = 0x1822DA3FC

# Pass 1: scan ALL Assembly-CSharp types (not just a filtered list) for the
# method whose VA is the closest one <= target, and also collect the next
# method VA after it (closest > target) to know the enclosing method's span.
arr_off = methodptr_array_offset(acs_module)

best_below = None   # (va, type_name, method_name, methodIndex)
best_above = None   # (va, type_name, method_name, methodIndex)

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
        if va <= target:
            if best_below is None or va > best_below[0]:
                md = parse_methoddef(mi)
                best_below = (va, get_string(td["nameIndex"]), get_string(md["nameIndex"]), mi)
        else:
            if best_above is None or va < best_above[0]:
                md = parse_methoddef(mi)
                best_above = (va, get_string(td["nameIndex"]), get_string(md["nameIndex"]), mi)

print("target VA:", hex(target))
print("closest managed method VA <= target:", best_below)
if best_below:
    print(f"  distance target - best_below = 0x{target - best_below[0]:X}")
print("closest managed method VA > target:", best_above)
if best_above:
    print(f"  distance best_above - target = 0x{best_above[0] - target:X}")
