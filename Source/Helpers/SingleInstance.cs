using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PayloadPanda.Helpers;

/// <summary>
/// One running app per data folder. Two instances would each keep their own tabs in memory
/// and overwrite the other's tabs.json, so a second launch hands over to the first — which
/// comes to the foreground — and exits. Keyed on the data folder, so a run pointed at
/// another folder (PAYLOADPANDA_DATA_DIR) is independent.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _activateWait;

    public SingleInstance(string dataFolder)
    {
        // Kernel object names can't contain backslashes; hash the (case-insensitive) path.
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataFolder.ToUpperInvariant())))[..16];
        _mutex = new Mutex(initiallyOwned: true, $@"Local\PayloadPanda-{id}", out var createdNew);
        IsFirst = createdNew;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\PayloadPanda-{id}-activate");
    }

    /// <summary>True when no other instance is using this data folder.</summary>
    public bool IsFirst { get; }

    /// <summary>First instance: run <paramref name="callback"/> (on a pool thread) whenever another launch hands over.</summary>
    public void OnActivateRequested(Action callback)
    {
        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            _activate, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Later instance: ask the first one to come to the foreground.</summary>
    public void SignalFirst()
    {
        // Windows only lets the process that owns the foreground (this one, just launched
        // by the user) pass that right on; without it the first instance could only flash.
        AllowSetForegroundWindow(AsfwAny);
        _activate.Set();
    }

    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore); // back to its previous normal or maximized state
        SetForegroundWindow(hwnd);
    }

    public void Dispose()
    {
        _activateWait?.Unregister(null);
        _activate.Dispose();
        _mutex.Dispose();
    }

    private const int AsfwAny = -1;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
