namespace Soulcrest.Core.Text;

/// <summary>One OCR word with its bounds in region pixels.</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>One OCR line with its bounds in region pixels.</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height)
{
    public IReadOnlyList<OcrWord> Words { get; init; } = [];
}
