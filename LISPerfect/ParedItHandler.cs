using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace LISPerfect
{
    /// <summary>
    /// Handles auto-close of parentheses and quotes in the AvalonEdit editor.
    /// Attached via PreviewTextInput so we intercept typed characters before
    /// they reach the editor's own handling. Respects the enabled flag from
    /// settings, and disables itself inside comments.
    /// </summary>
    public class ParedItHandler
    {
        private readonly TextEditor _editor;
        private readonly Settings _settings;

        public ParedItHandler(TextEditor editor, Settings settings)
        {
            _editor = editor;
            _settings = settings;
            _editor.PreviewTextInput += OnPreviewTextInput;
        }

        private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!_settings.ParedItEnabled) return;
            if (e.Text.Length != 1) return;

            char c = e.Text[0];
            var doc = _editor.Document;
            int caret = _editor.CaretOffset;

            // Don't do anything in comments — auto-close is meaningless there.
            if (InsideLineComment(doc, caret)) return;

            switch (c)
            {
                case '(':
                    AutoCloseParen(doc, caret);
                    e.Handled = true;
                    break;

                case ')':
                    if (NextChar(doc, caret) == ')')
                    {
                        // Skip past existing close-paren instead of inserting a redundant one.
                        _editor.CaretOffset = caret + 1;
                        e.Handled = true;
                    }
                    // Otherwise, let normal input handle it.
                    break;

                case '"':
                    if (NextChar(doc, caret) == '"')
                    {
                        // Skip past existing quote.
                        _editor.CaretOffset = caret + 1;
                        e.Handled = true;
                    }
                    else
                    {
                        AutoCloseQuote(doc, caret);
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void AutoCloseParen(TextDocument doc, int caret)
        {
            doc.Insert(caret, "()");
            _editor.CaretOffset = caret + 1;
        }

        private void AutoCloseQuote(TextDocument doc, int caret)
        {
            doc.Insert(caret, "\"\"");
            _editor.CaretOffset = caret + 1;
        }

        private static char NextChar(TextDocument doc, int offset)
        {
            if (offset >= doc.TextLength) return '\0';
            return doc.GetCharAt(offset);
        }

        /// <summary>
        /// Returns true if the given offset is inside a `; ...` line comment.
        /// Doesn't handle #| ... |# block comments — those are rare in typical Lisp.
        /// </summary>
        private static bool InsideLineComment(TextDocument doc, int offset)
        {
            var line = doc.GetLineByOffset(offset);
            int lineStart = line.Offset;
            bool inString = false;
            for (int i = lineStart; i < offset; i++)
            {
                char ch = doc.GetCharAt(i);
                if (ch == '"' && (i == lineStart || doc.GetCharAt(i - 1) != '\\'))
                    inString = !inString;
                else if (ch == ';' && !inString)
                    return true;
            }
            return false;
        }
    }
}