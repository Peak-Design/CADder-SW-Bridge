"""Native stack of chosen threads in a live process, via dbghelp.

    python stackwalk.py PID [TID ...]      (no TID: every thread)

Frames print as module+offset, with a symbol name where dbghelp finds
one (exports, or PDBs from _NT_SYMBOL_PATH). Threads are suspended for
the walk and resumed after.
"""
import ctypes
import ctypes.wintypes as w
import os
import sys

os.environ.setdefault(
    "_NT_SYMBOL_PATH", r"srv*C:\symbols*https://msdl.microsoft.com/download/symbols")

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
dbghelp = ctypes.WinDLL("dbghelp", use_last_error=True)

PROCESS_ALL_ACCESS = 0x1F0FFF
THREAD_ALL_ACCESS = 0x1F03FF
CONTEXT_ALL = 0x10003F
IMAGE_FILE_MACHINE_AMD64 = 0x8664
AddrModeFlat = 3


class M128A(ctypes.Structure):
    _fields_ = [("Low", ctypes.c_uint64), ("High", ctypes.c_int64)]


class CONTEXT(ctypes.Structure):
    _align_ = 16
    _fields_ = (
        [("P%dHome" % i, ctypes.c_uint64) for i in range(1, 7)]
        + [("ContextFlags", ctypes.c_uint32), ("MxCsr", ctypes.c_uint32)]
        + [(n, ctypes.c_uint16) for n in ("SegCs", "SegDs", "SegEs", "SegFs", "SegGs", "SegSs")]
        + [("EFlags", ctypes.c_uint32)]
        + [("Dr%d" % i, ctypes.c_uint64) for i in (0, 1, 2, 3, 6, 7)]
        + [(n, ctypes.c_uint64) for n in ("Rax", "Rcx", "Rdx", "Rbx", "Rsp", "Rbp", "Rsi", "Rdi",
                                          "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15", "Rip")]
        + [("FltSave", ctypes.c_byte * 512)]
        + [("VectorRegister", M128A * 26), ("VectorControl", ctypes.c_uint64)]
        + [(n, ctypes.c_uint64) for n in ("DebugControl", "LastBranchToRip", "LastBranchFromRip",
                                          "LastExceptionToRip", "LastExceptionFromRip")]
    )


class ADDRESS64(ctypes.Structure):
    _fields_ = [("Offset", ctypes.c_uint64), ("Segment", ctypes.c_uint16), ("Mode", ctypes.c_uint32)]


class KDHELP64(ctypes.Structure):
    _fields_ = [("raw", ctypes.c_uint64 * 14)]


class STACKFRAME64(ctypes.Structure):
    _fields_ = [("AddrPC", ADDRESS64), ("AddrReturn", ADDRESS64), ("AddrFrame", ADDRESS64),
                ("AddrStack", ADDRESS64), ("AddrBStore", ADDRESS64),
                ("FuncTableEntry", ctypes.c_void_p), ("Params", ctypes.c_uint64 * 4),
                ("Far", w.BOOL), ("Virtual", w.BOOL), ("Reserved", ctypes.c_uint64 * 3),
                ("KdHelp", KDHELP64)]


class SYMBOL_INFO(ctypes.Structure):
    _fields_ = [("SizeOfStruct", ctypes.c_uint32), ("TypeIndex", ctypes.c_uint32),
                ("Reserved", ctypes.c_uint64 * 2), ("Index", ctypes.c_uint32), ("Size", ctypes.c_uint32),
                ("ModBase", ctypes.c_uint64), ("Flags", ctypes.c_uint32), ("Value", ctypes.c_uint64),
                ("Address", ctypes.c_uint64), ("Register", ctypes.c_uint32), ("Scope", ctypes.c_uint32),
                ("Tag", ctypes.c_uint32), ("NameLen", ctypes.c_uint32), ("MaxNameLen", ctypes.c_uint32),
                ("Name", ctypes.c_char * 512)]


k32.OpenProcess.restype = w.HANDLE
k32.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
k32.OpenThread.restype = w.HANDLE
k32.OpenThread.argtypes = [w.DWORD, w.BOOL, w.DWORD]
k32.SuspendThread.argtypes = [w.HANDLE]
k32.ResumeThread.argtypes = [w.HANDLE]
k32.GetThreadContext.argtypes = [w.HANDLE, ctypes.c_void_p]
k32.K32GetModuleFileNameExW.argtypes = [w.HANDLE, ctypes.c_void_p, w.LPWSTR, w.DWORD]
dbghelp.SymInitialize.argtypes = [w.HANDLE, w.LPCSTR, w.BOOL]
dbghelp.SymSetOptions.argtypes = [w.DWORD]
dbghelp.SymGetModuleBase64.restype = ctypes.c_uint64
dbghelp.SymGetModuleBase64.argtypes = [w.HANDLE, ctypes.c_uint64]
dbghelp.SymFunctionTableAccess64.restype = ctypes.c_void_p
dbghelp.SymFunctionTableAccess64.argtypes = [w.HANDLE, ctypes.c_uint64]
dbghelp.SymFromAddr.argtypes = [w.HANDLE, ctypes.c_uint64, ctypes.POINTER(ctypes.c_uint64), ctypes.c_void_p]
dbghelp.StackWalk64.argtypes = [w.DWORD, w.HANDLE, w.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]

FTA = ctypes.WINFUNCTYPE(ctypes.c_void_p, w.HANDLE, ctypes.c_uint64)
GMB = ctypes.WINFUNCTYPE(ctypes.c_uint64, w.HANDLE, ctypes.c_uint64)
fta_cb = FTA(lambda h, a: dbghelp.SymFunctionTableAccess64(h, a))
gmb_cb = GMB(lambda h, a: dbghelp.SymGetModuleBase64(h, a))


def thread_ids(pid):
    import subprocess
    out = subprocess.check_output(
        ["powershell", "-NoProfile", "-Command",
         "(Get-Process -Id %d).Threads | ForEach-Object { $_.Id }" % pid], text=True)
    return [int(x) for x in out.split()]


def describe(hproc, addr):
    base = dbghelp.SymGetModuleBase64(hproc, addr)
    mod = "?"
    if base:
        buf = ctypes.create_unicode_buffer(1024)
        if k32.K32GetModuleFileNameExW(hproc, ctypes.c_void_p(base), buf, 1024):
            mod = os.path.basename(buf.value)
    sym = SYMBOL_INFO()
    sym.SizeOfStruct = 88
    sym.MaxNameLen = 500
    disp = ctypes.c_uint64(0)
    name = ""
    if dbghelp.SymFromAddr(hproc, addr, ctypes.byref(disp), ctypes.byref(sym)):
        name = " %s+0x%x" % (sym.Name.decode("ascii", "replace"), disp.value)
    return "%s+0x%x%s" % (mod, addr - base if base else addr, name)


def walk(hproc, tid, max_frames=64):
    hthr = k32.OpenThread(THREAD_ALL_ACCESS, False, tid)
    if not hthr:
        print("  thread %d: open failed (%d)" % (tid, ctypes.get_last_error()))
        return
    try:
        if k32.SuspendThread(hthr) == 0xFFFFFFFF:
            print("  thread %d: suspend failed" % tid)
            return
        try:
            ctx = CONTEXT()
            ctx.ContextFlags = CONTEXT_ALL
            if not k32.GetThreadContext(hthr, ctypes.byref(ctx)):
                print("  thread %d: GetThreadContext failed (%d)" % (tid, ctypes.get_last_error()))
                return
            fr = STACKFRAME64()
            fr.AddrPC.Offset = ctx.Rip
            fr.AddrPC.Mode = AddrModeFlat
            fr.AddrFrame.Offset = ctx.Rbp
            fr.AddrFrame.Mode = AddrModeFlat
            fr.AddrStack.Offset = ctx.Rsp
            fr.AddrStack.Mode = AddrModeFlat
            print("Thread %d  rip=0x%x rsp=0x%x" % (tid, ctx.Rip, ctx.Rsp))
            for _ in range(max_frames):
                ok = dbghelp.StackWalk64(IMAGE_FILE_MACHINE_AMD64, hproc, hthr, ctypes.byref(fr),
                                         ctypes.byref(ctx), None, fta_cb, gmb_cb, None)
                if not ok or fr.AddrPC.Offset == 0:
                    break
                print("    " + describe(hproc, fr.AddrPC.Offset))
        finally:
            k32.ResumeThread(hthr)
    finally:
        k32.CloseHandle(hthr)


def main():
    pid = int(sys.argv[1])
    tids = [int(x) for x in sys.argv[2:]] or thread_ids(pid)
    hproc = k32.OpenProcess(PROCESS_ALL_ACCESS, False, pid)
    if not hproc:
        sys.exit("OpenProcess failed: %d" % ctypes.get_last_error())
    dbghelp.SymSetOptions(0x2 | 0x4 | 0x10)       # UNDNAME | DEFERRED_LOADS | LOAD_LINES
    if not dbghelp.SymInitialize(hproc, None, True):
        sys.exit("SymInitialize failed: %d" % ctypes.get_last_error())
    for tid in tids:
        walk(hproc, tid)
        sys.stdout.flush()


if __name__ == "__main__":
    main()
