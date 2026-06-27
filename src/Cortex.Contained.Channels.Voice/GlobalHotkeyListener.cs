using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Listens for a global Windows hotkey (e.g. "Ctrl+Space") using
/// <c>RegisterHotKey</c> / <c>UnregisterHotKey</c> Win32 APIs.
/// Fires <see cref="HotkeyPressed"/> each time the key combination is pressed.
/// </summary>
/// <remarks>
/// For push-to-talk, the user presses the hotkey once to start listening
/// and presses it again to stop. The <see cref="VoiceChannel"/> handles toggling.
/// </remarks>
public sealed partial class GlobalHotkeyListener : IDisposable
{
    // Win32 interop
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x4A01; // arbitrary unique ID

    // Modifier flags for RegisterHotKey
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const uint PM_REMOVE = 0x0001;

#pragma warning disable SYSLIB1054 // Use LibraryImport — DllImport avoids requiring AllowUnsafeBlocks
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private readonly ILogger logger;
    private readonly uint modifiers;
    private readonly uint vk;
    private readonly string hotkeyText;
    private readonly Thread? messageThread;
    private readonly CancellationTokenSource cts = new();
    private volatile bool registered;

    /// <summary>Fired each time the hotkey is pressed.</summary>
    public event Action? HotkeyPressed;

    /// <summary>Whether the hotkey was successfully registered.</summary>
    public bool IsRegistered => this.registered;

    public GlobalHotkeyListener(string hotkeyText, ILogger logger)
    {
        this.logger = logger;
        this.hotkeyText = hotkeyText;

        if (!TryParseHotkey(hotkeyText, out this.modifiers, out this.vk))
        {
            this.LogHotkeyParseError(hotkeyText);
            return;
        }

        this.messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "GlobalHotkey",
        };
        this.messageThread.SetApartmentState(ApartmentState.STA);
        this.messageThread.Start();
    }

    private void MessageLoop()
    {
        // Register on this thread — hotkey messages go to the thread's message queue
        if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, this.modifiers | MOD_NOREPEAT, this.vk))
        {
            this.LogHotkeyRegisterFailed(this.hotkeyText, Marshal.GetLastPInvokeError());
            return;
        }

        this.registered = true;
        this.LogHotkeyRegistered(this.hotkeyText);

        var ct = this.cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Pump the message queue for WM_HOTKEY
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_HOTKEY && msg.wParam == (nint)HOTKEY_ID)
                    {
                        OnHotkeyDown();
                    }
                }

                Thread.Sleep(30); // ~33 Hz polling
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
            this.registered = false;
        }
    }

    private void OnHotkeyDown()
    {
        this.LogHotkeyDown(this.hotkeyText);

        try
        {
            HotkeyPressed?.Invoke();
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            this.LogHotkeyCallbackError("pressed", ex.Message);
        }
#pragma warning restore CA1031
    }

    public void Dispose()
    {
        this.cts.Cancel();
        this.messageThread?.Join(2000);
        this.cts.Dispose();
    }

    // ── Hotkey string parsing ────────────────────────────────────

    /// <summary>
    /// Parses a hotkey string like "Ctrl+Space", "Alt+Shift+V", "F5" into
    /// Win32 modifier flags and a virtual key code.
    /// </summary>
    internal static bool TryParseHotkey(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= MOD_CONTROL;
                    break;
                case "ALT":
                    modifiers |= MOD_ALT;
                    break;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    return false; // unknown modifier
            }
        }

        // Last part is the key itself
        var keyName = parts[^1].ToUpperInvariant();
        vk = KeyNameToVirtualKey(keyName);
        return vk != 0;
    }

    private static uint KeyNameToVirtualKey(string name) => name switch
    {
        "SPACE" => 0x20,
        "ENTER" or "RETURN" => 0x0D,
        "TAB" => 0x09,
        "ESCAPE" or "ESC" => 0x1B,
        "BACKSPACE" or "BACK" => 0x08,
        "DELETE" or "DEL" => 0x2E,
        "INSERT" or "INS" => 0x2D,
        "HOME" => 0x24,
        "END" => 0x23,
        "PAGEUP" or "PGUP" => 0x21,
        "PAGEDOWN" or "PGDN" => 0x22,
        "UP" => 0x26,
        "DOWN" => 0x28,
        "LEFT" => 0x25,
        "RIGHT" => 0x27,
        "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
        "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
        "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
        "F13" => 0x7C, "F14" => 0x7D, "F15" => 0x7E, "F16" => 0x7F,
        "CAPSLOCK" => 0x14,
        "NUMLOCK" => 0x90,
        "SCROLLLOCK" => 0x91,
        "PAUSE" => 0x13,
        "PRINTSCREEN" or "PRTSC" => 0x2C,
        "LWIN" => 0x5B,
        "RWIN" => 0x5C,
        // Single letter A-Z
        _ when name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z' => (uint)name[0],
        // Single digit 0-9
        _ when name.Length == 1 && name[0] >= '0' && name[0] <= '9' => (uint)name[0],
        _ => 0,
    };

    // ── LoggerMessage ────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse hotkey string: \"{HotkeyText}\"")]
    private partial void LogHotkeyParseError(string hotkeyText);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to register global hotkey \"{HotkeyText}\" (Win32 error {ErrorCode})")]
    private partial void LogHotkeyRegisterFailed(string hotkeyText, int errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Global hotkey registered: {HotkeyText}")]
    private partial void LogHotkeyRegistered(string hotkeyText);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Hotkey pressed: {HotkeyText}")]
    private partial void LogHotkeyDown(string hotkeyText);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hotkey {EventName} callback error: {ErrorMessage}")]
    private partial void LogHotkeyCallbackError(string eventName, string errorMessage);
}
