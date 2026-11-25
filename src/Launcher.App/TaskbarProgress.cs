using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Launcher.App;

/// <summary>
/// The update's progress on the launcher's taskbar button (Windows 7 and later), like a browser download.
/// Does nothing on Linux and macOS, or if Windows refuses.
/// </summary>
static class TaskbarProgress
{
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3 (only the two used here, in vtable order)
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int state);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    class TaskbarInstance { }

    const int TBPF_NOPROGRESS = 0, TBPF_NORMAL = 2;
    static ITaskbarList3? _list;
    static bool _failed, _shown;

    /// <summary>value 0..1 shows the bar; a negative value hides it.</summary>
    public static void Set(Window window, double value)
    {
        if (_failed || !OperatingSystem.IsWindows()) return;
        try
        {
            IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            if (_list is null)
            {
                _list = (ITaskbarList3)new TaskbarInstance();
                _list.HrInit();
            }
            if (value < 0)
            {
                if (_shown) _list.SetProgressState(hwnd, TBPF_NOPROGRESS);
                _shown = false;
                return;
            }
            if (!_shown) _list.SetProgressState(hwnd, TBPF_NORMAL);
            _shown = true;
            _list.SetProgressValue(hwnd, (ulong)(Math.Clamp(value, 0, 1) * 1000), 1000);
        }
        catch
        {
            _failed = true;   // no taskbar progress, nothing else changes
        }
    }
}
