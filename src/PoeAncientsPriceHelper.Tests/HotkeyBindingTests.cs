using PoeAncientsPriceHelper;
using SharpHook.Data;

namespace PoeAncientsPriceHelper.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("VcF7", KeyCode.VcF7, HotkeyModifiers.None)]
    [InlineData("VcA", KeyCode.VcA, HotkeyModifiers.None)]
    [InlineData("Ctrl+Shift+VcP", KeyCode.VcP, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift)]
    [InlineData("Ctrl+Shift+P", KeyCode.VcP, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift)]
    [InlineData("Alt+VcD", KeyCode.VcD, HotkeyModifiers.Alt)]
    public void Parse_ValidName_ReturnsBinding(string stored, KeyCode expectedKey, HotkeyModifiers expectedModifiers)
    {
        Assert.Equal(new HotkeyBinding(expectedKey, expectedModifiers), HotkeyBinding.Parse(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("12345")]
    public void Parse_InvalidName_FallsBackToDefault(string? stored)
    {
        Assert.Equal(HotkeyBinding.Default, HotkeyBinding.Parse(stored));
    }

    [Theory]
    [InlineData(KeyCode.VcF5, HotkeyModifiers.None)]
    [InlineData(KeyCode.VcP, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift)]
    [InlineData(KeyCode.VcPageUp, HotkeyModifiers.None)]
    [InlineData(KeyCode.VcNumPad0, HotkeyModifiers.None)]
    public void StorageRoundTrips(KeyCode key, HotkeyModifiers modifiers)
    {
        var binding = new HotkeyBinding(key, modifiers);
        Assert.Equal(binding, HotkeyBinding.Parse(HotkeyBinding.ToStorage(binding)));
    }

    [Theory]
    [InlineData(KeyCode.VcF5, HotkeyModifiers.None, "F5")]
    [InlineData(KeyCode.VcA, HotkeyModifiers.None, "A")]
    [InlineData(KeyCode.Vc1, HotkeyModifiers.None, "1")]
    [InlineData(KeyCode.VcP, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, "Ctrl+Shift+P")]
    public void Display_StripsVcPrefix(KeyCode key, HotkeyModifiers modifiers, string expected)
    {
        Assert.Equal(expected, HotkeyBinding.Display(new HotkeyBinding(key, modifiers)));
    }

    [Theory]
    [InlineData(KeyCode.VcEscape, true)]
    [InlineData(KeyCode.VcLeftControl, true)]
    [InlineData(KeyCode.VcRightControl, true)]
    // F3/F4 are ordinary rebindable keys, but the defaults no longer use function keys.
    [InlineData(KeyCode.VcF3, false)]
    [InlineData(KeyCode.VcF4, false)]
    [InlineData(KeyCode.VcF5, false)]
    [InlineData(KeyCode.VcP, false)]
    public void IsReserved_FlagsOnlyFixedGestures(KeyCode key, bool reserved)
    {
        Assert.Equal(reserved, HotkeyBinding.IsReserved(key));
    }
}
