namespace Cortex.Contained.Channels.Voice.Tests;

public class GlobalHotkeyListenerTests
{
    // Win32 modifier constants (matching GlobalHotkeyListener)
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    #region Basic Parsing

    [Fact]
    public void TryParseHotkey_SingleLetter_ReturnsTrue()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("A", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(0u, modifiers);
        Assert.Equal((uint)'A', vk);
    }

    [Fact]
    public void TryParseHotkey_SingleDigit_ReturnsTrue()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("5", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(0u, modifiers);
        Assert.Equal((uint)'5', vk);
    }

    [Fact]
    public void TryParseHotkey_FunctionKey_ReturnsTrue()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("F5", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(0u, modifiers);
        Assert.Equal(0x74u, vk);
    }

    [Fact]
    public void TryParseHotkey_Space_ReturnsTrue()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Space", out _, out var vk);

        Assert.True(result);
        Assert.Equal(0x20u, vk);
    }

    #endregion

    #region Modifier Combinations

    [Fact]
    public void TryParseHotkey_CtrlSpace_ParsesCorrectly()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Ctrl+Space", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_CONTROL, modifiers);
        Assert.Equal(0x20u, vk);
    }

    [Fact]
    public void TryParseHotkey_AltShiftV_ParsesCorrectly()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Alt+Shift+V", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_ALT | MOD_SHIFT, modifiers);
        Assert.Equal((uint)'V', vk);
    }

    [Fact]
    public void TryParseHotkey_ControlAltF12_ParsesCorrectly()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Control+Alt+F12", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_CONTROL | MOD_ALT, modifiers);
        Assert.Equal(0x7Bu, vk);
    }

    [Fact]
    public void TryParseHotkey_CtrlShiftAltA_ParsesAllModifiers()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Ctrl+Shift+Alt+A", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_CONTROL | MOD_SHIFT | MOD_ALT, modifiers);
        Assert.Equal((uint)'A', vk);
    }

    [Fact]
    public void TryParseHotkey_WinSpace_ParsesCorrectly()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Win+Space", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_WIN, modifiers);
        Assert.Equal(0x20u, vk);
    }

    [Fact]
    public void TryParseHotkey_WindowsA_ParsesCorrectly()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Windows+A", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_WIN, modifiers);
        Assert.Equal((uint)'A', vk);
    }

    [Fact]
    public void TryParseHotkey_CtrlWinF1_ParsesCorrectly()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Ctrl+Win+F1", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_CONTROL | MOD_WIN, modifiers);
        Assert.Equal(0x70u, vk);
    }

    #endregion

    #region Case Insensitivity

    [Fact]
    public void TryParseHotkey_LowercaseModifiers_ParsesCorrectly()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("ctrl+space", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_CONTROL, modifiers);
        Assert.Equal(0x20u, vk);
    }

    [Fact]
    public void TryParseHotkey_MixedCaseKey_ParsesCorrectly()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("CTRL+f5", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_CONTROL, modifiers);
        Assert.Equal(0x74u, vk);
    }

    #endregion

    #region Whitespace Handling

    [Fact]
    public void TryParseHotkey_SpacesAroundPlus_ParsesCorrectly()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Ctrl + Space", out var modifiers, out var vk);

        Assert.True(result);
        Assert.Equal(MOD_CONTROL, modifiers);
        Assert.Equal(0x20u, vk);
    }

    #endregion

    #region Special Keys

    [Theory]
    [InlineData("Enter", 0x0Du)]
    [InlineData("Return", 0x0Du)]
    [InlineData("Tab", 0x09u)]
    [InlineData("Escape", 0x1Bu)]
    [InlineData("Esc", 0x1Bu)]
    [InlineData("Backspace", 0x08u)]
    [InlineData("Delete", 0x2Eu)]
    [InlineData("Del", 0x2Eu)]
    [InlineData("Insert", 0x2Du)]
    [InlineData("Home", 0x24u)]
    [InlineData("End", 0x23u)]
    [InlineData("PageUp", 0x21u)]
    [InlineData("PgUp", 0x21u)]
    [InlineData("PageDown", 0x22u)]
    [InlineData("PgDn", 0x22u)]
    [InlineData("Up", 0x26u)]
    [InlineData("Down", 0x28u)]
    [InlineData("Left", 0x25u)]
    [InlineData("Right", 0x27u)]
    [InlineData("Pause", 0x13u)]
    [InlineData("CapsLock", 0x14u)]
    [InlineData("NumLock", 0x90u)]
    [InlineData("ScrollLock", 0x91u)]
    [InlineData("PrintScreen", 0x2Cu)]
    [InlineData("PrtSc", 0x2Cu)]
    [InlineData("LWin", 0x5Bu)]
    [InlineData("RWin", 0x5Cu)]
    public void TryParseHotkey_SpecialKeys_ParseCorrectly(string keyName, uint expectedVk)
    {
        var result = GlobalHotkeyListener.TryParseHotkey(keyName, out _, out var vk);

        Assert.True(result);
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("F1", 0x70u)]
    [InlineData("F2", 0x71u)]
    [InlineData("F3", 0x72u)]
    [InlineData("F4", 0x73u)]
    [InlineData("F5", 0x74u)]
    [InlineData("F6", 0x75u)]
    [InlineData("F7", 0x76u)]
    [InlineData("F8", 0x77u)]
    [InlineData("F9", 0x78u)]
    [InlineData("F10", 0x79u)]
    [InlineData("F11", 0x7Au)]
    [InlineData("F12", 0x7Bu)]
    [InlineData("F13", 0x7Cu)]
    [InlineData("F14", 0x7Du)]
    [InlineData("F15", 0x7Eu)]
    [InlineData("F16", 0x7Fu)]
    public void TryParseHotkey_FunctionKeys_ParseCorrectly(string keyName, uint expectedVk)
    {
        var result = GlobalHotkeyListener.TryParseHotkey(keyName, out _, out var vk);

        Assert.True(result);
        Assert.Equal(expectedVk, vk);
    }

    #endregion

    #region Invalid Input

    [Fact]
    public void TryParseHotkey_Null_ReturnsFalse()
    {
        var result = GlobalHotkeyListener.TryParseHotkey(null!, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseHotkey_EmptyString_ReturnsFalse()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("", out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseHotkey_WhitespaceOnly_ReturnsFalse()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("   ", out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseHotkey_UnknownKey_ReturnsFalse()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Ctrl+FooBar", out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseHotkey_UnknownModifier_ReturnsFalse()
    {
        var result = GlobalHotkeyListener.TryParseHotkey("Meta+A", out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseHotkey_ModifierOnly_ReturnsFalse()
    {
        // "Ctrl+" — last part after split is empty, so parts array has only "Ctrl"
        // and the key part is "Ctrl" which would be parsed as the key itself
        // Actually: "Ctrl" alone — it's treated as the key, not modifier. "Ctrl" is not A-Z single char, not a special key.
        var result = GlobalHotkeyListener.TryParseHotkey("Ctrl+", out _, out _);

        // After split with RemoveEmptyEntries, parts = ["Ctrl"]
        // parts[^1] = "Ctrl", which is not a valid key name → returns false
        Assert.False(result);
    }

    #endregion

    #region Letters and Digits

    [Theory]
    [InlineData("A", 'A')]
    [InlineData("Z", 'Z')]
    [InlineData("M", 'M')]
    public void TryParseHotkey_Letters_MapToAscii(string input, char expected)
    {
        var result = GlobalHotkeyListener.TryParseHotkey(input, out _, out var vk);

        Assert.True(result);
        Assert.Equal((uint)expected, vk);
    }

    [Theory]
    [InlineData("0", '0')]
    [InlineData("9", '9')]
    public void TryParseHotkey_Digits_MapToAscii(string input, char expected)
    {
        var result = GlobalHotkeyListener.TryParseHotkey(input, out _, out var vk);

        Assert.True(result);
        Assert.Equal((uint)expected, vk);
    }

    #endregion
}
