using System.Reflection;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class MainFormShutdownTests
{
    [Fact]
    public async Task ClosingAfterProviderDisposalAndRepeatedDisposeAreSafe()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(); // not shown: no game capture or WebView startup
                ((ServiceProvider)form.Services).Dispose();
                var closing = typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                closing.Invoke(form, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                closing.Invoke(form, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                form.Dispose();
                form.Dispose();
                completed.SetResult();
            }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task CancelledCloseKeepsServicesAvailable()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();
                form.FormClosing += (_, e) => e.Cancel = true;
                var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
                typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [args]);
                Assert.True(args.Cancel);
                Assert.NotNull(form.Services.GetRequiredService<TrackerService>());
                form.Dispose();
                completed.SetResult();
            }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    [Fact]
    public async Task ControlsDisposeBeforeProviderAndReentrantCloseDoesNotRunShutdownAgain()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();
                var checkedOrder = false;
                form.Controls.Add(new DisposeProbe(() =>
                {
                    Assert.NotNull(form.Services.GetRequiredService<TrackerService>());
                    typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(form, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                    form.Dispose(); // reentrant disposal must be a no-op
                    checkedOrder = true;
                }));
                form.Dispose();
                Assert.True(checkedOrder);
                Assert.Throws<ObjectDisposedException>(() => form.Services.GetRequiredService<TrackerService>());
                completed.SetResult();
            }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class DisposeProbe(Action check) : Control
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) check();
            base.Dispose(disposing);
        }
    }

}
