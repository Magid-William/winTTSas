using System.Runtime.InteropServices;

namespace WinTtsAs.Core;

/// <summary>
/// Reads the focused window's selection by briefly sending Ctrl+C and
/// restoring the previous plain-text clipboard afterwards.
/// </summary>
public static class SelectionReader
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkC = 0x43;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const int ModifierWaitMs = 1500;
    private const int ClipboardWaitMs = 500;

    public static async Task<string?> TryReadAsync(CancellationToken cancellationToken)
    {
        var target = GetForegroundWindow();

        await WaitForModifiersUpAsync(cancellationToken).ConfigureAwait(false);

        var hadText = ClipboardText.TryGetText(out var savedText);
        var savedSequence = ClipboardText.SequenceNumber();

        if (target != IntPtr.Zero && GetForegroundWindow() != target)
        {
            // Best effort: the window the hotkey was pressed in should get the copy.
            SetForegroundWindow(target);
        }

        if (!SendCtrlC())
        {
            // UIPI blocks synthetic input into elevated windows.
            return null;
        }

        var changed = await WaitForClipboardChangeAsync(savedSequence, cancellationToken)
            .ConfigureAwait(false);
        ClipboardText.TryGetText(out var selection);

        if (hadText)
        {
            ClipboardText.TrySetText(savedText ?? "");
        }

        return changed ? selection : null;
    }

    private static async Task WaitForModifiersUpAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + ModifierWaitMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!AnyModifierDown())
            {
                return;
            }

            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitForClipboardChangeAsync(
        uint sequence,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + ClipboardWaitMs;
        while (Environment.TickCount64 < deadline)
        {
            if (ClipboardText.SequenceNumber() != sequence)
            {
                return true;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static bool AnyModifierDown() =>
        IsDown(VkControl) ||
        IsDown(VkMenu) ||
        IsDown(VkShift) ||
        IsDown(VkLWin) ||
        IsDown(VkRWin);

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool SendCtrlC()
    {
        var inputs = new[]
        {
            KeyInput(VkControl, down: true),
            KeyInput(VkC, down: true),
            KeyInput(VkC, down: false),
            KeyInput(VkControl, down: false)
        };

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>())
            == (uint)inputs.Length;
    }

    private static Input KeyInput(int virtualKey, bool down) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = (ushort)virtualKey,
                Flags = down ? 0 : KeyEventKeyUp
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
