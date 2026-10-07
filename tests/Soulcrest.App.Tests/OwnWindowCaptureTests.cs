using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CaptureDesktopFactAttribute : FactAttribute
{
    public CaptureDesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_TEST_OWN_WINDOW_CAPTURE") != "1")
            Skip = "Opt-in: captures only a temporary test window on an interactive Windows desktop.";
    }
}

public sealed class OwnWindowCaptureTests
{
    [CaptureDesktopFact]
    public async Task ReceivesPixelsBeforeAndAfterWindowResize()
    {
        var ready = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Thread(() =>
        {
            using var form = new Form { Text = "Soulcrest capture regression test", FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(80, 80),
                Size = new System.Drawing.Size(320, 200), BackColor = Color.Red, ShowInTaskbar = false };
            form.Shown += (_, _) => ready.TrySetResult(form);
            // WGC may wait for new damage after FramePool.Recreate. A game keeps
            // presenting; keep this test window painting too, including after resize.
            using var repaint = new System.Windows.Forms.Timer { Interval = 30 };
            repaint.Tick += (_, _) => { form.Invalidate(); form.Update(); };
            repaint.Start();
            Application.Run(form);
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        var form = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            nint handle = 0;
            form.Invoke(() => handle = form.Handle);
            using var capture = (MonitorCapture)Activator.CreateInstance(typeof(MonitorCapture),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [Rectangle.Empty, (nint)0, handle], null)!;
            await ExpectColor(capture, red: true);
            form.Invoke(() => { form.Size = new System.Drawing.Size(480, 260); form.BackColor = Color.Blue; });
            await ExpectColor(capture, red: false);
            Assert.True(capture.FramesArrived >= 2);
        }
        finally
        {
            form.BeginInvoke(() => form.Close());
            Assert.True(ui.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private static async Task ExpectColor(MonitorCapture capture, bool red)
    {
        for (var i = 0; i < 60; i++)
        {
            using var picture = capture.CaptureMat(capture.Bounds);
            if (picture is not null)
            {
                var pixel = picture.At<Vec3b>(picture.Height / 2, picture.Width / 2);
                if (red ? pixel.Item2 > 200 && pixel.Item0 < 30 : pixel.Item0 > 200 && pixel.Item2 < 30)
                    return;
            }
            await Task.Delay(50);
        }
        Assert.Fail($"No {(red ? "red" : "blue after resize")} frame received. WGC frames: {capture.FramesArrived}; {MonitorCapture.LastProblem}");
    }
}
