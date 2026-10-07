using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Soulcrest.App.Capture;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class RegionSelectorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EscapeOrFocusLossCancelsSelection(bool escape)
    {
        RunSta(() =>
        {
            using var image = new Bitmap(100, 100);
            using var form = Create(image);
            Set(form, "_activated", true);
            Set(form, "_start", new Point(10, 10));
            if (escape)
                Assert.Equal(true, Call(form, "ProcessCmdKey", new Message(), Keys.Escape));
            else
                Call(form, "OnDeactivate", EventArgs.Empty);
            Assert.Null(form.Result);
            Assert.Equal(DialogResult.Cancel, form.DialogResult);
            Assert.False(form.Capture);
        });
    }

    [Fact]
    public void SuccessfulSelectionSurvivesDeactivationWhileClosing()
    {
        RunSta(() =>
        {
            using var image = new Bitmap(100, 100);
            using var form = Create(image);
            Set(form, "_activated", true);
            Set(form, "_start", new Point(10, 10));
            Set(form, "_selection", new Rectangle(10, 10, 30, 40));
            Call(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 40, 50, 0));
            Call(form, "OnDeactivate", EventArgs.Empty);
            Assert.Equal(new Rectangle(-90, 10, 30, 40), form.Result);
            Assert.Equal(DialogResult.OK, form.DialogResult);
        });
    }

    private static RegionSelectorForm Create(Bitmap image) => (RegionSelectorForm)Activator.CreateInstance(
        typeof(RegionSelectorForm), BindingFlags.Instance | BindingFlags.NonPublic, null,
        [image, new Rectangle(-100, 0, 100, 100), "test"], null)!;
    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Call(object target, string method, params object[] args) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
