using System.Windows;

namespace TransIt.Models;

public class OcrLine
{
    public List<OcrWord> Words { get; set; } = [];
    public string FullText { get; set; } = string.Empty;
    public Rect BoundingRect { get; set; }
}
