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

    /// <summary>Loads the native OpenCV library and runs SIFT once (pet window scan), as a single-file build must.</summary>
    public static bool CheckOpenCv()
    {
        try
        {
            using var matcher = new Soulcrest.Ocr.PetWindow.PortraitMatcher();
            using var image = new OpenCvSharp.Mat(96, 96, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.All(40));
            OpenCvSharp.Cv2.Circle(image, new OpenCvSharp.Point(48, 48), 30, OpenCvSharp.Scalar.White, 3);
            OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(20, 20, 25, 18), OpenCvSharp.Scalar.All(200), -1);
            matcher.AddReference("probe", image);
            var match = matcher.Match(image);
            OpenCv = match.PetId == "probe" ? $"ok ({match.Score})" : "FEHLER: kein Treffer";
            return match.PetId == "probe";
        }
        catch (Exception exception)
        {
            OpenCv = "FEHLER: " + exception.GetBaseException().Message;
            return false;
        }
    }
}
