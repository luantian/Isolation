#!/usr/bin/env python3
"""
Unlock build artifacts by finding and releasing file locks.
"""
import ctypes
import os
import sys
import subprocess
from ctypes import wintypes
import time

# Windows API constants
PROCESS_ALL_ACCESS = 0x1F0FFF
TH32CS_SNAPPROCESS = 0x00000002
TH32CS_SNAPMODULE = 0x00000008

class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", ctypes.c_char * 260),
    ]

def find_processes_with_module(module_name_substring):
    """Find processes that have loaded a module with the given name substring."""
    kernel32 = ctypes.windll.kernel32
    
    # Take snapshot of all processes
    snapshot = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snapshot == -1:
        return []
    
    processes = []
    pe32 = PROCESSENTRY32()
    pe32.dwSize = ctypes.sizeof(PROCESSENTRY32)
    
    try:
        # Get first process
        if kernel32.Process32First(snapshot, ctypes.byref(pe32)):
            while True:
                exe_name = pe32.szExeFile.decode('utf-8', errors='ignore').lower()
                if module_name_substring.lower() in exe_name:
                    processes.append({
                        'pid': pe32.th32ProcessID,
                        'name': exe_name
                    })
                
                # Get next process
                if not kernel32.Process32Next(snapshot, ctypes.byref(pe32)):
                    break
    finally:
        kernel32.CloseHandle(snapshot)
    
    return processes

def check_file_lock(file_path):
    """Check if a file is locked by trying to open it."""
    try:
        with open(file_path, 'a'):
            return False, None
    except PermissionError as e:
        return True, str(e)
    except Exception as e:
        return True, str(e)

def kill_process(pid, name):
    """Kill a process by PID."""
    try:
        result = subprocess.run(
            ['taskkill', '/F', '/PID', str(pid)],
            capture_output=True,
            text=True,
            encoding='gbk'
        )
        return result.returncode == 0
    except Exception as e:
        print(f"Error killing process {pid}: {e}")
        return False

def main():
    assets_file = r"F:\workspace\cechuang\projects\Isolation\src\IsolationLeakage.App\obj\project.assets.json"
    
    print("=" * 60)
    print("Build Artifact Unlocker")
    print("=" * 60)
    print()
    
    # Check if file exists and is locked
    if not os.path.exists(assets_file):
        print(f"File does not exist: {assets_file}")
        print("No lock to release.")
        return 0
    
    print(f"Checking lock on: {assets_file}")
    is_locked, error_msg = check_file_lock(assets_file)
    
    if not is_locked:
        print("✓ File is NOT locked. Build should succeed.")
        return 0
    
    print(f"✗ File IS locked: {error_msg}")
    print()
    
    # List of processes that commonly lock build artifacts
    suspects = [
        'devenv',           # Visual Studio
        'msbuild',          # MSBuild
        'dotnet',           # dotnet CLI
        'vbcscompiler',     # VB/C# compiler server
        'vbcslauncher',     # VB/C# launcher
        'servicehub',       # ServiceHub (VS background)
    ]
    
    print("Searching for processes that might be locking build artifacts...")
    print()
    
    found_processes = []
    for suspect in suspects:
        procs = find_processes_with_module(suspect)
        for proc in procs:
            found_processes.append(proc)
    
    if not found_processes:
        print("No suspect processes found.")
        print()
        print("Common solutions:")
        print("1. Close Visual Studio if it's open")
        print("2. Restart your computer")
        print("3. Run: dotnet build-server shutdown")
        return 1
    
    print(f"Found {len(found_processes)} suspect process(es):")
    for proc in found_processes:
        print(f"  - PID {proc['pid']}: {proc['name']}")
    print()
    
    # Check if running in force mode
    force_mode = '--force' in sys.argv
    
    if force_mode:
        print("Running in force mode, terminating processes automatically...")
    else:
        print("Do you want to terminate these processes to release the lock?")
        print("(This will close Visual Studio or other development tools)")
        response = input("Type 'yes' to confirm: ").strip().lower()
        
        if response != 'yes':
            print("Aborted. No processes were terminated.")
            print("Please manually close Visual Studio or the listed processes.")
            return 1
    
    print()
    print("Terminating processes...")
    killed = 0
    for proc in found_processes:
        print(f"  Killing {proc['name']} (PID {proc['pid']})...", end=" ")
        if kill_process(proc['pid'], proc['name']):
            print("✓")
            killed += 1
        else:
            print("✗ (may have already exited)")
    
    print()
    print(f"Terminated {killed} process(es).")
    
    # Wait a moment for handles to be released
    print("Waiting for file handles to be released...")
    time.sleep(2)
    
    # Check if file is still locked
    is_locked, error_msg = check_file_lock(assets_file)
    if is_locked:
        print("✗ File is still locked. You may need to:")
        print("  1. Close Visual Studio manually if it's still running")
        print("  2. Restart your computer")
        return 1
    else:
        print("✓ File lock released successfully!")
        print()
        print("You can now run: dotnet build")
        return 0

if __name__ == "__main__":
    sys.exit(main())
