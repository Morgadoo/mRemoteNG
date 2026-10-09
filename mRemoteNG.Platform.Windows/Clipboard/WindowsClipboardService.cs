using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace mRemoteNG.Platform.Windows.Clipboard;

/// <summary>
/// Windows clipboard implementation using the Win32 clipboard API (CF_UNICODETEXT).
/// </summary>
/// <remarks>
/// Uses user32/kernel32 directly rather than System.Windows.Forms.Clipboard so this assembly
/// runs on the plain .NET runtime the Avalonia app ships with (no WindowsDesktop framework).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardService : IClipboardService
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public string? GetText()
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText) || !OpenClipboardWithRetry())
            return null;

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero) return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!OpenClipboardWithRetry())
            throw new InvalidOperationException("The clipboard is in use by another application.");

        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * sizeof(char);
            var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
            if (handle == IntPtr.Zero)
                throw new OutOfMemoryException("GlobalAlloc failed.");

            var pointer = GlobalLock(handle);
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            // On success the system owns the memory; only free it if the hand-off failed.
            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                throw new InvalidOperationException("SetClipboardData failed.");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Clear()
    {
        if (!OpenClipboardWithRetry()) return;
        try
        {
            EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    public bool ContainsText() => IsClipboardFormatAvailable(CfUnicodeText);

    /// <summary>Another process may briefly hold the clipboard open; retry a few times.</summary>
    private static bool OpenClipboardWithRetry()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
