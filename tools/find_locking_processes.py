#!/usr/bin/env python3
"""
Find all processes that might be locking build artifacts.
"""
import psutil
import os
import sys

LOCKED_FILE = r"F:\workspace\cechuang\projects\Isolation\src\IsolationLeakage.App\obj\project.assets.json"
PROJECT_DIR = r"F:\workspace\cechuang\projects\Isolation"

# Suspicious process names
SUSPICIOUS_PROCESSES = [
    'dotnet', 'msbuild', 'vbcscompiler', 'servicehub', 
    'devenv', 'visualstudio', 'IsolationLeakage',
    'ServiceHub.Host.CLR', 'ServiceHub.VSDetouredHost'
]

def is_locking_file(pid):
    """Check if a process has the locked file open."""
    try:
        proc = psutil.Process(pid)
        # Check open files
        for file in proc.open_files():
            if LOCKED_FILE in file.path or PROJECT_DIR in file.path:
                return True, file.path
    except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
        pass
    return False, None

def main():
    print("Scanning all processes for build artifact locks...")
    print()
    
    found = []
    
    # Method 1: Check known suspicious process names
    for proc in psutil.process_iter(['pid', 'name', 'exe', 'cwd']):
        try:
            pid = proc.info['pid']
            name = proc.info['name'].lower() if proc.info['name'] else ''
            exe = proc.info['exe'].lower() if proc.info['exe'] else ''
            cwd = proc.info['cwd'] if proc.info['cwd'] else ''
            
            # Check if process name matches suspicious patterns
            is_suspicious = any(s.lower() in name or s.lower() in exe 
                               for s in SUSPICIOUS_PROCESSES)
            
            # Check if process working directory is in our project
            is_in_project = PROJECT_DIR.lower() in cwd.lower()
            
            # Check if process has our locked file open
            has_file, file_path = is_locking_file(pid)
            
            if is_suspicious or is_in_project or has_file:
                found.append({
                    'pid': pid,
                    'name': proc.info['name'],
                    'exe': proc.info['exe'],
                    'cwd': cwd,
                    'reason': [],
                    'has_file': has_file,
                    'file_path': file_path
                })
                
                if is_suspicious:
                    found[-1]['reason'].append('suspicious name')
                if is_in_project:
                    found[-1]['reason'].append('working in project dir')
                if has_file:
                    found[-1]['reason'].append(f'has file: {file_path}')
                    
        except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
            pass
    
    if not found:
        print("No locking processes found.")
        return 0
    
    print(f"Found {len(found)} potential locking process(es):\n")
    
    for proc in found:
        print(f"PID: {proc['pid']}")
        print(f"  Name: {proc['name']}")
        print(f"  Exe: {proc['exe']}")
        print(f"  CWD: {proc['cwd']}")
        print(f"  Reasons: {', '.join(proc['reason'])}")
        if proc['has_file']:
            print(f"  📁 Has locked file: {proc['file_path']}")
        print()
    
    # Offer to kill processes
    if '--force' in sys.argv:
        print("Force mode enabled. Killing all found processes...")
        for proc in found:
            try:
                p = psutil.Process(proc['pid'])
                p.kill()
                print(f"  ✓ Killed PID {proc['pid']} ({proc['name']})")
            except Exception as e:
                print(f"  ✗ Failed to kill PID {proc['pid']}: {e}")
    else:
        print("Run with --force to kill these processes")
    
    return 0

if __name__ == "__main__":
    sys.exit(main())
