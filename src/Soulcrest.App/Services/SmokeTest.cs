namespace Soulcrest.App.Services;

/// <summary>
/// "--smoke-test": start the real app, wait until the Blazor UI and the map rendered, then exit.
/// Exit code 0 = ok, 3 = timeout/failure. The result is also written to smoke-test.txt in the data folder.
/// </summary>
public static class SmokeTest
{
    public static bool Enabled { get; set; }
    public static volatile bool UiRendered;
    public static volatile bool MapShown;
    public static volatile string? Error;
    public static string Capture = "nicht gestartet";
    public static string OpenCv = "nicht geprüft";

    public static string Describe() =>
        $"ui={UiRendered} map={MapShown} capture={Capture} opencv={OpenCv} error={Error ?? "-"}";

    /// <summary>Loads the native OpenCV library and checks image matching once (map tracking), as a single-file build must.</summary>
    public static bool CheckOpenCv()
    {
        try
        {
            using var image = new OpenCvSharp.Mat(96, 96, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.All(40));
            OpenCvSharp.Cv2.Circle(image, new OpenCvSharp.Point(48, 48), 30, OpenCvSharp.Scalar.White, 3);
            OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(20, 20, 25, 18), OpenCvSharp.Scalar.All(200), -1);
            using var gray = new OpenCvSharp.Mat();
            using var result = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(image, gray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
            OpenCvSharp.Cv2.MatchTemplate(gray, gray, result, OpenCvSharp.TemplateMatchModes.CCoeffNormed);
            OpenCvSharp.Cv2.MinMaxLoc(result, out double _, out double score);
            OpenCv = score > 0.99 ? $"ok ({score:0.000})" : "FEHLER: kein Treffer";
            return score > 0.99;
        }
        catch (Exception exception)
        {
            OpenCv = "FEHLER: " + exception.GetBaseException().Message;
            return false;
        }
    }
}
