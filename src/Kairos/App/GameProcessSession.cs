using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kairos.App;

/// <summary>A query-only process handle; polling never enumerates all running programs.</summary>
internal sealed class GameProcessSession(int processId, string path, SafeProcessHandle handle) : IGameProcessSession
{
    public int ProcessId => processId;
    public string ExecutablePath => path;
    public bool IsAlive => !handle.IsClosed && !handle.IsInvalid && WaitForSingleObject(handle, 0) == 258 /* WAIT_TIMEOUT */;

    public static IGameProcessSession? TryOpen(int processId)
    {
        // Query identity and wait for exit only. Do not request process modification/debug access.
        var handle = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */ | 0x100000 /* SYNCHRONIZE */, false, processId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        bool retained = false;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size))
                return null;
            var session = new GameProcessSession(processId, path.ToString(), handle);
            if (!session.IsAlive)
                return null;
            retained = true;
            return session;
        }
        finally
        {
            if (!retained)
                handle.Dispose();
        }
    }

    public void Dispose() => handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
