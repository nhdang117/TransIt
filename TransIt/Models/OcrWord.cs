using System.Windows;

namespace TransIt.Models;

public class OcrWord
{
    public string Text { get; set; } = string.Empty;
    public Rect BoundingRect { get; set; }
}
