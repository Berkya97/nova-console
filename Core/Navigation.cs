using Nova.Models;

namespace Nova.Core;

/// <summary>Klavye ve oyun kolundan gelen tüm girdiler bu soyut eylemlere çevrilir.</summary>
public enum NavAction { Left, Right, Up, Down, Confirm, Back, Options, TabPrev, TabNext, Menu, PageUp, PageDown, Search, Control }

public enum InputKind { Keyboard, Gamepad }

public interface INavigable
{
    /// <summary>Eylemi işler; bir şey değiştiyse true döner (ses efekti buna göre çalınır).</summary>
    bool Handle(NavAction action);

    /// <summary>Alt bardaki tuş ipuçları.</summary>
    IEnumerable<(NavAction, string)> Hints { get; }
}

public interface IScreen : INavigable
{
    TileItem? Selected { get; }
}

/// <summary>Ekranın üstünde açılan katman (menü, görüntüleyici, arama...).</summary>
public interface IOverlay : INavigable
{
    event Action? Closed;
}

/// <summary>Fiziksel klavyeden yazı kabul eden katman.</summary>
public interface ITextTarget
{
    void TypeText(string text);
    void Backspace();
    /// <summary>Fiziksel klavyede Enter.</summary>
    void Submit();
}
