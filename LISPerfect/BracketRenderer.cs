using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace LISPerfect
{
    public class BracketRenderer : IBackgroundRenderer
    {
        private readonly TextEditor _editor;
        private static readonly System.Windows.Media.Brush HighlightBrush =
            new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 100, 180, 255));

        static BracketRenderer()
        {
            HighlightBrush.Freeze();
        }

        public BracketRenderer(TextEditor editor) => _editor = editor;

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var doc = _editor.Document;
            if (doc == null || doc.TextLength == 0) return;

            int caret = _editor.TextArea.Caret.Offset;
            int? matchA = FindMatchAt(doc, caret);
            int? matchB = caret > 0 ? FindMatchAt(doc, caret - 1) : null;

            HighlightIfMatch(textView, drawingContext, doc, matchA, caret);
            HighlightIfMatch(textView, drawingContext, doc, matchB, caret - 1);
        }

        private void HighlightIfMatch(TextView view, DrawingContext ctx,
                                      TextDocument doc, int? match, int origin)
        {
            if (match == null) return;
            DrawBox(view, ctx, origin);
            DrawBox(view, ctx, match.Value);
        }

        private void DrawBox(TextView view, DrawingContext ctx, int offset)
        {
            var seg = new TextSegment { StartOffset = offset, Length = 1 };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, seg))
            {
                ctx.DrawRectangle(HighlightBrush, null,
                    new Rect(rect.Location, new Size(rect.Width, rect.Height)));
            }
        }

        private static int? FindMatchAt(TextDocument doc, int offset)
        {
            if (offset < 0 || offset >= doc.TextLength) return null;
            char c = doc.GetCharAt(offset);
            int dir;
            char target;
            if (c == '(') { dir = +1; target = ')'; }
            else if (c == ')') { dir = -1; target = '('; }
            else return null;

            int depth = 0;
            for (int i = offset; i >= 0 && i < doc.TextLength; i += dir)
            {
                char ch = doc.GetCharAt(i);
                if (ch == '(' || ch == ')')
                {
                    if (InsideStringOrComment(doc, i)) continue;
                    if (ch == c) depth++;
                    else if (ch == target)
                    {
                        depth--;
                        if (depth == 0) return i;
                    }
                }
            }
            return null;
        }

        private static bool InsideStringOrComment(TextDocument doc, int offset)
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
            return inString;
        }
    }
}