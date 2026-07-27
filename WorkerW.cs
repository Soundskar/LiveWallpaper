using System.Runtime.InteropServices;

namespace LiveWallpaper;

/// <summary>
/// Implements the Progman -> WorkerW technique used to render a window
/// behind the desktop icons (the same trick Wallpaper Engine / Lively use).
/// </summary>
internal static class WorkerW
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint WM_SPAWN_WORKERW = 0x052C;
    private const uint SMTO_NORMAL = 0x0000;

    /// <summary>
    /// Attaches the given window handle behind the desktop icons and sizes it
    /// to fill the primary monitor (in physical pixels).
    /// </summary>
    public static bool AttachToDesktop(IntPtr windowHandle)
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return false;

        // Ask Progman to spawn a WorkerW window behind the desktop icons.
        SendMessageTimeout(progman, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);

        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);

        // Windows 11 layout: the desktop icons (SHELLDLL_DefView) live directly under
        // Progman. We must become a Progman child ordered *just below* the icons — that
        // keeps the video above the static wallpaper but below the icons, so clicks
        // still reach the desktop.
        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            SetParent(windowHandle, progman);
            MoveWindow(windowHandle, 0, 0, width, height, true);
            Native.SetWindowPos(windowHandle, defView, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            return true;
        }

        // Windows 10 layout: the icons live under a WorkerW; draw into the sibling
        // WorkerW that sits behind them.
        IntPtr workerw = FindWorkerW();
        SetParent(windowHandle, workerw != IntPtr.Zero ? workerw : progman);
        MoveWindow(windowHandle, 0, 0, width, height, true);
        return true;
    }

    /// <summary>Detaches the wallpaper window from the desktop (re-parents to null).</summary>
    public static void DetachFromDesktop(IntPtr windowHandle) => SetParent(windowHandle, IntPtr.Zero);

    private static IntPtr FindWorkerW()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            IntPtr shellView = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                // The sibling WorkerW right after this window is the one we draw into.
                found = FindWindowEx(IntPtr.Zero, hWnd, "WorkerW", null);
                return false; // stop enumerating
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
