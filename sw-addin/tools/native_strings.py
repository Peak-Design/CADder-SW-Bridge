"""Names for unsymbolized blender.exe frames: disassembles the code around
each address inside the live process and prints every string literal it
references rip-relative (format strings, RNA names, type_info names).

    python funcstrings.py PID ADDR_OR_OFFSET... [--before N --after N]

Offsets (< 0x10000000) are taken relative to the blender.exe base.
"""
import ctypes
import ctypes.wintypes as w
import sys

import capstone

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
k32.OpenProcess.restype = w.HANDLE
k32.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
k32.ReadProcessMemory.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]
psapi.EnumProcessModulesEx.argtypes = [w.HANDLE, ctypes.POINTER(w.HMODULE), w.DWORD,
                                       ctypes.POINTER(w.DWORD), w.DWORD]
psapi.GetModuleFileNameExW.argtypes = [w.HANDLE, w.HMODULE, w.LPWSTR, w.DWORD]


class MODULEINFO(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p), ("SizeOfImage", w.DWORD),
                ("EntryPoint", ctypes.c_void_p)]


psapi.GetModuleInformation.argtypes = [w.HANDLE, w.HMODULE, ctypes.POINTER(MODULEINFO), w.DWORD]


def modules(h):
    arr = (w.HMODULE * 1024)()
    need = w.DWORD(0)
    psapi.EnumProcessModulesEx(h, arr, ctypes.sizeof(arr), ctypes.byref(need), 0x3)
    out = []
    for i in range(need.value // ctypes.sizeof(w.HMODULE)):
        buf = ctypes.create_unicode_buffer(1024)
        psapi.GetModuleFileNameExW(h, arr[i], buf, 1024)
        mi = MODULEINFO()
        psapi.GetModuleInformation(h, arr[i], ctypes.byref(mi), ctypes.sizeof(mi))
        out.append((buf.value, mi.lpBaseOfDll, mi.SizeOfImage))
    return out


def read(h, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)):
        return b""
    return buf.raw[:got.value]


def string_at(h, addr):
    data = read(h, addr, 200)
    if not data:
        return None
    # ASCII
    end = data.find(b"\0")
    s = data[:end if end >= 0 else len(data)]
    if len(s) >= 5 and all(32 <= c < 127 or c in (9, 10, 13) for c in s):
        return s.decode("ascii")
    # UTF-16
    try:
        u = data.decode("utf-16-le", "strict").split("\0")[0]
        if len(u) >= 5 and all(32 <= ord(c) < 127 for c in u):
            return "L" + u
    except Exception:
        pass
    return None


def main():
    pid = int(sys.argv[1])
    args = sys.argv[2:]
    before, after = 0x600, 0x300
    if "--before" in args:
        before = int(args[args.index("--before") + 1], 0)
    if "--after" in args:
        after = int(args[args.index("--after") + 1], 0)
    addrs = [int(a, 0) for a in args if not a.startswith("--") and a not in
             (args[args.index("--before") + 1] if "--before" in args else "",
              args[args.index("--after") + 1] if "--after" in args else "")]
    h = k32.OpenProcess(0x1F0FFF, False, pid)
    mods = modules(h)
    blender = [m for m in mods if m[0].lower().endswith("blender.exe")][0]
    base, size = blender[1], blender[2]
    print("blender.exe base 0x%x size 0x%x" % (base, size))
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    for a in addrs:
        if a < 0x10000000:
            a += base
        start = a - before
        code = read(h, start, before + after)
        print("\n=== 0x%x (blender.exe+0x%x) ===" % (a, a - base))
        seen = set()
        for ins in md.disasm(code, start):
            for op in ins.operands:
                if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                    tgt = ins.address + ins.size + op.mem.disp
                    if base <= tgt < base + size and tgt not in seen:
                        seen.add(tgt)
                        s = string_at(h, tgt)
                        if s:
                            print("  %s0x%x %s -> %r" % ("* " if ins.address <= a else "  ",
                                                          ins.address - base, ins.mnemonic, s[:120]))


if __name__ == "__main__":
    main()
