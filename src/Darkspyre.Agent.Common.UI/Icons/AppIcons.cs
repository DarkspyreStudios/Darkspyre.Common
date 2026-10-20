
namespace Darkspyre.Agent.Common.UI.Icons;

/// <summary>Artwork owned by Darkspyre and drawn by Icon.</summary>
public static class AppIcons
{
    /// <summary>A square with a header bar: a docked panel.</summary>
    public static readonly IconSource Panel = IconSource.Fill("""
        <rect x="2" y="2" width="12" height="12" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <path d="M2 5.5h12" fill="none" stroke="currentColor" stroke-width="1.2"/>
        """, "0 0 16 16");

    /// <summary>The check glyph's SVG path data in a 16 by 16 view box.</summary>
    public const string CheckGlyph = """
        <path d="M3 8.5l3.2 3L13 4.5" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>
        """;

    /// <summary>The copy glyph's SVG path data in a 16 by 16 view box.</summary>
    public const string CopyGlyph = """
        <rect x="5" y="4.5" width="9" height="10" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <path d="M11 4.5V3A1.5 1.5 0 009.5 1.5h-6A1.5 1.5 0 002 3v8a1.5 1.5 0 001.5 1.5H5" fill="none" stroke="currentColor" stroke-width="1.2"/>
        """;

    /// <summary>A check mark: a chosen answer on an ask card.</summary>
    public static readonly IconSource Check = IconSource.Fill(CheckGlyph, "0 0 16 16");


    /// <summary>Three lines: the control that opens and closes the project sidebar.</summary>
    public static readonly IconSource Menu = IconSource.Fill("""
        <path d="M2.5 4h11M2.5 8h11M2.5 12h11" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>A plus sign: the control that adds an item, such as a project.</summary>
    public static readonly IconSource Plus = IconSource.Fill("""
        <path d="M8 3v10M3 8h10" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>A page with a folded corner and two text lines: a document.</summary>
    public static readonly IconSource Document = IconSource.Fill("""
        <path d="M3 1.5h6l4 4v9H3zM9 1.5v4h4M5.5 8h5M5.5 10.5h5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>Lines with a plus mark and a minus mark: a file diff.</summary>
    public static readonly IconSource Diff = IconSource.Fill("""
        <path d="M2.5 3.5h11M2.5 8h11M2.5 12.5h11M5 1.5v4M3 3.5h4M9 10.5v4" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>A pulse line: activity.</summary>
    public static readonly IconSource Activity = IconSource.Fill("""
        <path d="M1.5 8h3l2-4.5 3 9 2-4.5h3" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>A speech bubble with a question mark: the Prompts panel.</summary>
    public static readonly IconSource Prompt = IconSource.Fill("""
        <path d="M2.5 3.5h11v7.5h-6l-3 2.5v-2.5h-2z" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
        <path d="M6.6 5.9a1.4 1.4 0 1 1 2 1.3c-.4.2-.6.5-.6.9" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        <circle cx="8" cy="9.4" r=".7" fill="currentColor"/>
        """, "0 0 16 16");

    /// <summary>The application mark: an ouroboros around a prompt chevron, cut from resources/icon-source.png.</summary>
    public static readonly IconSource Brand = IconSource.Fill("""
        <image href="brand-mark.png" width="128" height="128"/>
        """, "0 0 128 128");

    /// <summary>A failure recorded in the transcript: a triangle with an exclamation mark.</summary>
    public static readonly IconSource Warning = IconSource.Fill("""
        <path d="M8 1.5a1 1 0 0 1 .87.5l6.5 11.25A1 1 0 0 1 14.5 14.75h-13a1 1 0 0 1-.87-1.5L7.13 2A1 1 0 0 1 8 1.5Zm0 4a.75.75 0 0 0-.75.75v3.5a.75.75 0 0 0 1.5 0v-3.5A.75.75 0 0 0 8 5.5Zm0 6.25a.9.9 0 1 0 0 1.8.9.9 0 0 0 0-1.8Z"/>
        """, "0 0 16 16");

    /// <summary>A right-pointing chevron in a 10 by 10 view box, for expand and collapse toggles.</summary>
    public static readonly IconSource Chevron = IconSource.Fill("""
        <path d="M3 1.5L6.5 5 3 8.5" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/>
        """, "0 0 10 10");

    /// <summary>A gear of two rings and spokes: settings.</summary>
    public static readonly IconSource Settings = IconSource.Fill("""
        <circle cx="8" cy="8" r="2" fill="none" stroke="currentColor" stroke-width="1.2" />
        <circle cx="8" cy="8" r="4.4" fill="none" stroke="currentColor" stroke-width="1.2" />
        <path d="M8 1.6v2M8 12.4v2M1.6 8h2M12.4 8h2M3.5 3.5l1.4 1.4M11.1 11.1l1.4 1.4M3.5 12.5l1.4-1.4M11.1 4.9l1.4-1.4" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" />
        """, "0 0 16 16");

    /// <summary>The System Dashboard: a two-by-two grid of rounded squares.</summary>
    public static readonly IconSource Dashboard = IconSource.Fill("""
        <rect x="1.5" y="1.5" width="5.5" height="5.5" rx="1.2"/>
        <rect x="9" y="1.5" width="5.5" height="5.5" rx="1.2"/>
        <rect x="1.5" y="9" width="5.5" height="5.5" rx="1.2"/>
        <rect x="9" y="9" width="5.5" height="5.5" rx="1.2"/>
        """, "0 0 16 16");

    /// <summary>A square stacked in front of a second square: background work.</summary>
    public static readonly IconSource BackgroundWork = IconSource.Fill("""
        <rect x="5.5" y="1.5" width="9" height="9" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <path d="M3.5 4.5h-.5a1.5 1.5 0 00-1.5 1.5v7a1.5 1.5 0 001.5 1.5h7a1.5 1.5 0 001.5-1.5v-.5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>A paper clip: attach a file.</summary>
    public static readonly IconSource Attachment = IconSource.Fill("""
        <path d="M10.5 4.5l-5 5a2 2 0 002.8 2.8l5.3-5.3a3.4 3.4 0 00-4.8-4.8L3 7.9a4.8 4.8 0 006.8 6.8l4.7-4.7" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>A folder outline.</summary>
    public static readonly IconSource Folder = IconSource.Fill("""
        <path d="M1.5 3.5h4l1.2 1.5h7.8v7h-13z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>Three nodes joined by a forking line: a source-control branch.</summary>
    public static readonly IconSource Branch = IconSource.Fill("""
        <path d="M4.5 3.5v9M4.5 5.5h4a3 3 0 003-3" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/>
        <circle cx="4.5" cy="2.6" r="1.4" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <circle cx="4.5" cy="13.4" r="1.4" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <circle cx="11.5" cy="2.6" r="1.4" fill="none" stroke="currentColor" stroke-width="1.2"/>
        """, "0 0 16 16");

    /// <summary>A filled right-pointing triangle: send the message.</summary>
    public static readonly IconSource Send = IconSource.Fill("""
        <path d="M6 4.5l5 3.5-5 3.5z"/>
        """, "0 0 16 16");

    /// <summary>A filled rounded square: stop the running turn.</summary>
    public static readonly IconSource Stop = IconSource.Fill("""
        <rect x="5" y="5" width="6" height="6" rx="1"/>
        """, "0 0 16 16");

    /// <summary>A pencil: edit.</summary>
    public static readonly IconSource Edit = IconSource.Fill("""
        <path d="M3 11.8V13h1.2l7.7-7.7-1.2-1.2zM10 4.8l1.2 1.2 1-1a.85.85 0 000-1.2l-.1-.1a.85.85 0 00-1.2 0z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>An X (Lucide <c>x</c>): remove or close.</summary>
    public static readonly IconSource Close = IconSource.Fill("""
        <path d="M4 4l8 8M12 4l-8 8" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>A down arrow (Lucide <c>arrow-down</c>): move to the newest content.</summary>
    public static readonly IconSource ArrowDown = IconSource.Fill("""
        <path d="M8 3v10M3.5 8.5L8 13l4.5-4.5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>Two overlapping pages: copy to the clipboard.</summary>
    public static readonly IconSource Copy = IconSource.Fill(CopyGlyph, "0 0 16 16");

    /// <summary>A padlock: read-only access.</summary>
    public static readonly IconSource ReadOnly = IconSource.Fill("""
        <rect x="3.5" y="7" width="9" height="6.5" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.4"/>
        <path d="M5.5 7V5a2.5 2.5 0 015 0v2" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>A shield outline: workspace access.</summary>
    public static readonly IconSource Workspace = IconSource.Fill("""
        <path d="M8 1.8l5 1.8v3.6c0 3.1-2 5.6-5 7-3-1.4-5-3.9-5-7V3.6z" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>A shield with an exclamation mark: full access.</summary>
    public static readonly IconSource FullAccess = IconSource.Fill("""
        <path d="M8 1.8l5 1.8v3.6c0 3.1-2 5.6-5 7-3-1.4-5-3.9-5-7V3.6z" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/>
        <path d="M8 4.6v4.1M8 11.2v.2" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>A prompt chevron and cursor line in a window: a command terminal.</summary>
    public static readonly IconSource Terminal = IconSource.Fill("""
        <rect x="1.5" y="2.5" width="13" height="11" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <path d="M4 6l2 2-2 2M7.5 10.5h4" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>A hammer: building and build output.</summary>
    public static readonly IconSource Hammer = IconSource.Fill("""
        <path d="M2.5 2.5h8l3 2.5v2h-11z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        <path d="M7.5 7v7" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>Two overlapping speech bubbles: a conversation between participants.</summary>
    public static readonly IconSource Chat = IconSource.Fill("""
        <path d="M1.5 2.5h8.5v5.5h-4.5l-2.5 2v-2h-1.5z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        <path d="M11.5 5.5h3v5.5h-1.5v2l-2.5-2h-3.5v-1.5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>An envelope: the conversation's message count.</summary>
    public static readonly IconSource Envelope = IconSource.Fill("""
        <rect x="1.5" y="3.5" width="13" height="9" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <path d="M2 4.5l6 4.5 6-4.5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>A coin: tokens used.</summary>
    public static readonly IconSource Coin = IconSource.Fill("""
        <circle cx="8" cy="8" r="6.2" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <circle cx="8" cy="8" r="3.8" fill="none" stroke="currentColor" stroke-width="1.2"/>
        """, "0 0 16 16");

    /// <summary>An arrow leaving an open box: export.</summary>
    public static readonly IconSource Share = IconSource.Fill("""
        <path d="M5.5 6.5h-2v7.5h9V6.5h-2" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        <path d="M8 10V1.8M5.3 4.5L8 1.8l2.7 2.7" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>A down arrow pressing onto a flat line: compact the conversation.</summary>
    public static readonly IconSource Compact = IconSource.Fill("""
        <path d="M2.5 13.5h11" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/>
        <path d="M8 2v8.5M4.8 7.3L8 10.5l3.2-3.2" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>A window divided near its foot: the bottom dock.</summary>
    public static readonly IconSource BottomPanel = IconSource.Fill("""
        <rect x="2" y="2" width="12" height="12" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <path d="M2 10.5h12" fill="none" stroke="currentColor" stroke-width="1.2"/>
        """, "0 0 16 16");

    /// <summary>Three dots in a column: a menu of choices.</summary>
    public static readonly IconSource MoreVertical = IconSource.Fill("""
        <circle cx="8" cy="3.5" r="1.3" fill="currentColor"/>
        <circle cx="8" cy="8" r="1.3" fill="currentColor"/>
        <circle cx="8" cy="12.5" r="1.3" fill="currentColor"/>
        """, "0 0 16 16");

    /// <summary>A stack of two server units with status lights.</summary>
    public static readonly IconSource Server = IconSource.Fill("""
        <rect x="2" y="2.5" width="12" height="4.5" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <rect x="2" y="9" width="12" height="4.5" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <circle cx="4.75" cy="4.75" r=".7" fill="currentColor"/>
        <circle cx="4.75" cy="11.25" r=".7" fill="currentColor"/>
        """, "0 0 16 16");

    /// <summary>A plug with two prongs and its cable: a model connection whose provider has no logo of its own.</summary>
    public static readonly IconSource Connector = IconSource.Fill("""
        <path d="M6 1.5v3M10 1.5v3" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        <path d="M4 4.5h8v3a4 4 0 01-8 0z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        <path d="M8 11.5v3" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        """, "0 0 16 16");
}
