using System.Runtime.InteropServices;

namespace WinTtsAs.Core;

/// <summary>Plain-text access to the Windows clipboard.</summary>
public static class ClipboardText
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int OpenAttempts = 10;
    private const int OpenRetryMs = 10;

    public static uint SequenceNumber() => GetClipboardSequenceNumber();

    public static bool TryGetText(out string? text)
    {
        text = null;

        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(OpenRetryMs);
                continue;
            }

            try
            {
                var handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    text = Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                return text is not null;
            }
            finally
            {
                CloseClipboard();
            }
        }

        return false;
    }

    public static bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(OpenRetryMs);
                continue;
            }

            try
            {
                if (!EmptyClipboard())
                {
                    return false;
                }

                var bytes = (nuint)((text.Length + 1) * sizeof(char));
                var memory = GlobalAlloc(GmemMoveable, bytes);
                if (memory == IntPtr.Zero)
                {
                    return false;
                }

                var pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return false;
                }

                try
                {
                    var characters = text.ToCharArray();
                    Marshal.Copy(characters, 0, pointer, characters.Length);
                    Marshal.WriteInt16(pointer, characters.Length * sizeof(char), 0);
                }
                finally
                {
                    GlobalUnlock(memory);
                }

                if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return false;
                }

                // The clipboard owns the memory now.
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
