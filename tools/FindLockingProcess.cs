using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

class FindLockingProcess
{
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);
    
    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);
    
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);
    
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint pSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgaProcessInfo, ref uint lpdwRebootReasons);
    
    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public FILETIME ProcessStartTime;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }
    
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }
    
    const int ERROR_MORE_DATA = 234;
    
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: FindLockingProcess <filename>");
            return;
        }
        
        string filePath = args[0];
        Console.WriteLine("Checking locks on: " + filePath);
        Console.WriteLine();
        
        uint handle;
        string key = Guid.NewGuid().ToString();
        int res = RmStartSession(out handle, 0, key);
        
        if (res != 0)
        {
            Console.WriteLine("Failed to start Restart Manager session. Error: " + res);
            return;
        }
        
        try
        {
            string[] resources = new string[] { filePath };
            res = RmRegisterResources(handle, (uint)resources.Length, resources, 0, null, 0, null);
            
            if (res != 0)
            {
                Console.WriteLine("Failed to register resources. Error: " + res);
                return;
            }
            
            uint pnProcInfoNeeded = 0, pnProcInfo = 0, lpdwRebootReasons = 0;
            RM_PROCESS_INFO[] processInfo = new RM_PROCESS_INFO[0];
            
            res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);
            
            if (res == ERROR_MORE_DATA || pnProcInfoNeeded > 0)
            {
                processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;
                
                res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);
                
                if (res == 0)
                {
                    Console.WriteLine("Found " + pnProcInfo + " process(es) locking the file:");
                    Console.WriteLine();
                    
                    for (int i = 0; i < pnProcInfo; i++)
                    {
                        var pi = processInfo[i];
                        Console.WriteLine("Process " + (i + 1) + ":");
                        Console.WriteLine("  Name: " + pi.strAppName);
                        Console.WriteLine("  PID: " + pi.Process.dwProcessId);
                        Console.WriteLine("  Service: " + pi.strServiceShortName);
                        Console.WriteLine("  Type: " + pi.ApplicationType);
                        Console.WriteLine();
                    }
                }
                else
                {
                    Console.WriteLine("RmGetList failed with error: " + res);
                }
            }
            else if (res == 0 && pnProcInfo == 0)
            {
                Console.WriteLine("No processes are locking this file.");
            }
            else
            {
                Console.WriteLine("RmGetList failed with error: " + res);
            }
        }
        finally
        {
            RmEndSession(handle);
        }
    }
}
