namespace PenBridge.Mac.Settings;

/// <summary>The area of the target display the pen maps to; exactly one applies at a time (#247).</summary>
public enum MappingArea
{
    /// <summary>The whole display (or the whole desktop for All displays).</summary>
    FullDisplay,

    /// <summary>A picked window, followed as it moves (ADR-0036).</summary>
    Window,

    /// <summary>A region of the display (ADR-0035).</summary>
    Region,
}
