using System.Windows;

namespace TransIt.Models;

public enum LayoutCategory { Text, Title, List, Table, Figure }

public class LayoutRegion
{
    public LayoutCategory Category { get; set; }
    public Rect BoundingRect { get; set; } // physical pixels, same unit as OcrLine.BoundingRect
    public float Confidence { get; set; }
}
