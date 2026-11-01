using Xunit;

namespace LanBridge.Core.Tests;

public class SidebarAndHotkeyTests
{
    [Fact]
    public void Defaults_parse_and_do_not_conflict()
    {
        Assert.Null(HotkeyParser.Conflict(HotkeyParser.DefaultSend, HotkeyParser.DefaultSidebar));
        Assert.True(HotkeyParser.TryParse(HotkeyParser.DefaultSend, out var send, out _));
        Assert.True(HotkeyParser.TryParse(HotkeyParser.DefaultSidebar, out var sidebar, out _));
        Assert.Equal("Ctrl+Alt+V", send.Canonical);
        Assert.Equal("Ctrl+Alt+L", sidebar.Canonical);
        Assert.Equal('V', send.VirtualKey);
        Assert.Equal(HotkeyParser.ModControl | HotkeyParser.ModAlt | HotkeyParser.ModNoRepeat, send.Modifiers);
    }

    [Fact]
    public void Identical_hotkeys_conflict_and_bare_keys_are_rejected()
    {
        Assert.Equal("两个快捷键不能相同。", HotkeyParser.Conflict("Ctrl+Alt+V", "ctrl+alt+v"));
        Assert.False(HotkeyParser.TryParse("V", out _, out var error));
        Assert.Contains("Ctrl", error);
        Assert.False(HotkeyParser.TryParse("", out _, out _));
    }

    [Fact]
    public void Sidebar_hides_to_a_handle_and_scales_with_dpi()
    {
        var hidden = SidebarLayout.Place(100, 20, 1600, 900, shown: false);
        var shown = SidebarLayout.Place(100, 20, 1600, 900, shown: true);
        Assert.Equal(18, hidden.Width);
        Assert.Equal(100 + 1600 - 18, hidden.Left);
        Assert.Equal(392, shown.Width);
        Assert.Equal(100 + 1600 - 392, shown.Left);
        Assert.Equal(20, shown.Top);
        Assert.Equal(900, shown.Height);
        Assert.Equal(20 + (900 - 84) / 2, SidebarLayout.HandleTop(20, 900));
        Assert.True(SidebarLayout.NearRightEdge(1690, 100, 1600, 40));
        Assert.False(SidebarLayout.NearRightEdge(1400, 100, 1600, 40));

        var dips = SidebarLayout.ToDips(192, 0, 384, 216, 2);
        Assert.Equal((96, 0, 192, 108), dips);
        Assert.Equal(TimeSpan.Zero, SidebarLayout.Duration(reducedMotion: true));
        Assert.Equal(TimeSpan.FromMilliseconds(220), SidebarLayout.Duration(reducedMotion: false));
    }

    [Fact]
    public void History_labels_distinguish_direction_and_peer()
    {
        Assert.Equal("来自电脑", HistoryLabels.OriginOnPc("out"));
        Assert.Equal("来自电脑", HistoryLabels.OriginOnPc("to_phone"));
        Assert.Equal("来自手机", HistoryLabels.OriginOnPc("in"));
        Assert.True(HistoryLabels.VisibleForPeer(null, "phone-a", 1));
        Assert.False(HistoryLabels.VisibleForPeer(null, "phone-a", 2));
        Assert.True(HistoryLabels.VisibleForPeer("phone-b", "phone-b", 2));
        Assert.False(HistoryLabels.VisibleForPeer("phone-a", "phone-b", 2));
        Assert.True(HistoryLabels.CanCopy("clipboard"));
        Assert.False(HistoryLabels.CanCopy("file"));
        Assert.True(HistoryLabels.CanOpenFile("file", @"C:\in\a.png", exists: true));
        Assert.False(HistoryLabels.CanOpenFile("file", @"C:\in\a.png", exists: false));
    }

    [Fact]
    public void Per_phone_clipboard_toggle_overrides_the_global_switch()
    {
        var settings = new AppSettings { ClipboardSyncEnabled = true };
        Assert.True(settings.ClipboardEnabledFor("phone-a"));
        settings.SetClipboardEnabled("phone-a", false);
        Assert.False(settings.ClipboardEnabledFor("phone-a"));
        Assert.True(settings.ClipboardEnabledFor("phone-b"));
        var clone = settings.Clone();
        clone.SetClipboardEnabled("phone-b", false);
        Assert.True(settings.ClipboardEnabledFor("phone-b"));
        Assert.Equal("Ctrl+Alt+V", clone.HotkeySend);
        Assert.Equal("Ctrl+Alt+L", clone.HotkeySidebar);
    }
}
