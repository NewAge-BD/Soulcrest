using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Two test classes saved the same map cache at the same time and one failed on the shared "path.tmp"
/// (GitHub build 0.7.3). Parallel writers of one cache must all succeed and leave a readable file.
/// </summary>
public sealed class MapReferenceSaveTests
{
    [Fact]
    public void ParallelWritersOfTheSameCacheAllSucceed()
    {
        using var gray = new Mat(512, 512, MatType.CV_8UC1);
        Cv2.Randu(gray, new Scalar(0), new Scalar(255));
        Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), 0);
        using var reference = MapReference.Build("test", gray, 1.0);
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-tests-mapref-save", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "test.bin");
        try
        {
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => reference.Save(path));

            using var loaded = MapReference.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(reference.Points.Length, loaded.Points.Length);
            Assert.Equal(["test.bin"], Directory.GetFiles(directory).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
