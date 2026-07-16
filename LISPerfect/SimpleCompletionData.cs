using System;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace LISPerfect
{
    /// <summary>
    /// A completion suggestion: a text label to display, description tooltip,
    /// and the string that gets inserted when the user picks it.
    /// </summary>
    public class SimpleCompletionData : ICompletionData
    {
        public SimpleCompletionData(string text, string description)
        {
            Text = text;
            Description = description;
        }

        public System.Windows.Media.ImageSource? Image => null;
        public string Text { get; }
        public object Content => new System.Windows.Controls.TextBlock
        {
            Text = Text,
            Margin = new System.Windows.Thickness(-16, 0, 0, 0)
        };
        public object Description { get; }
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment, Text);
        }
    }
}