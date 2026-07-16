using System;
using ICSharpCode.AvalonEdit.Document;

namespace LISPerfect
{
    /// <summary>
    /// Represents one open tab: its document (text + undo history),
    /// its associated file path (if saved), and its dirty state.
    /// The MainWindow's single AvalonEdit swaps between these Documents
    /// as the user changes tabs.
    /// </summary>
    public class EditorTab
    {
        public TextDocument Document { get; }
        public string? FilePath { get; set; }
        public bool IsDirty { get; set; }
        public int SavedCaretOffset { get; set; }
        public double SavedVerticalOffset { get; set; }

        public EditorTab(string? filePath = null, string initialText = "")
        {
            FilePath = filePath;
            Document = new TextDocument(initialText);
            IsDirty = false;
        }

        public string DisplayName
        {
            get
            {
                string name = FilePath != null
                    ? System.IO.Path.GetFileName(FilePath)
                    : "Untitled";
                return IsDirty ? "*" + name : name;
            }
        }
    }
}