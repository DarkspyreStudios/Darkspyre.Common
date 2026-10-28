using Avalonia.Controls;
using Darkspyre.Agent.Common.UI.Controls;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>How a transcript's tool rows take part in its selection and copy.</summary>
internal static class TranscriptMarks
{
    /// <summary>
    /// Follows whether <paramref name="row"/> is open. While it is open, <paramref name="title"/>, which sits in the row's
    /// toggle, joins a drag, and a right-click on it opens the selection menu. While it is closed, the title stays out of a
    /// drag, so a drag across messages skips the collapsed row, and a right-click on <paramref name="header"/> opens the
    /// row's own menu, whose Copy gives the row's line.
    /// </summary>
    internal static void ToolRow(Control title, Control header, Expander row)
    {
        ContextMenu collapsed = RowCopy.Menu(title);
        void Mark()
        {
            TextSelection.SetMode(title, row.IsExpanded ? TextSelectionMode.Include : TextSelectionMode.Skip);
            header.ContextMenu = row.IsExpanded ? null : collapsed;
        }
        Mark();
        row.PropertyChanged += (_, args) =>
        {
            if (args.Property == Expander.IsExpandedProperty) Mark();
        };
    }
}
