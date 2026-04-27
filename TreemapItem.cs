using System.Windows;
using System.Windows.Media;

namespace UltraTree;

public sealed class TreemapItem
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Brush Fill { get; set; } = Brushes.SteelBlue;

    public string Label
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Path))
                return "";

            int i = Path.LastIndexOf('\\');
            return i >= 0 && i < Path.Length - 1 ? Path[(i + 1)..] : Path;
        }
    }

    public string Tooltip => $"{Path}\n{Utils.FormatBytes(Bytes)}";

    public Visibility LabelVisibility =>
        Width >= 80 && Height >= 22 ? Visibility.Visible : Visibility.Collapsed;
}