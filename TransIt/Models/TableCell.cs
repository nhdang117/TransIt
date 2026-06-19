using System.Windows;

namespace TransIt.Models;

public class TableCell
{
    public int Row { get; set; }
    public int Col { get; set; }
    public Rect BoundingRect { get; set; } // bitmap-relative physical pixels
    public string Text { get; set; } = "";
}
