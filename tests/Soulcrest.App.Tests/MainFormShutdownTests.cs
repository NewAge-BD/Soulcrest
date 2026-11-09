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
                var watchdog = AttachWatchdog(form);
                ((ServiceProvider)form.Services).Dispose();
                var closing = typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                closing.Invoke(form, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                closing.Invoke(form, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                form.Dispose();
                form.Dispose();
                Assert.Equal(1, watchdog.Stops);
                completed.SetResult();
            }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task MainOwnsNoRetiredNativeHudsButKeepsEditorSourcesAndAudioAvailable()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(); // unshown: no tracker, WebView or real audio starts
                Assert.DoesNotContain(typeof(MainForm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field =>
                    field.FieldType == typeof(Soulcrest.App.Overlay.LootOverlayForm)
                    || field.FieldType == typeof(Soulcrest.App.Overlay.AbyssRankOverlayForm));
                Assert.Null(form.Services.GetService<Soulcrest.App.Overlay.ILootOverlaySource>());
                Assert.Null(form.Services.GetService<UnifiedLootOverlaySource>());
                Assert.NotNull(form.Services.GetRequiredService<Soulcrest.App.Overlay.IAbyssRankOverlaySource>());
                Assert.NotNull(form.Services.GetRequiredService<RareDropAlertService>());
                Assert.NotNull(form.Services.GetRequiredService<AbyssRankSoundPlayer>());
                Assert.NotNull(form.Services.GetRequiredService<Soulcrest.App.OverlayEditorKit.IOverlayEditorPresentationSource>());
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
                var watchdog = AttachWatchdog(form);
                form.FormClosing += (_, e) => e.Cancel = true;
                var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
                typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [args]);
                Assert.True(args.Cancel);
                Assert.Equal(0, watchdog.Stops);
                Assert.NotNull(form.Services.GetRequiredService<TrackerService>());
                form.Dispose();
                Assert.Equal(1, watchdog.Stops);
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
                var watchdog = AttachWatchdog(form);
                var checkedOrder = false;
                form.Controls.Add(new DisposeProbe(() =>
                {
                    Assert.Equal(1, watchdog.Stops);
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

    [Fact]
    public async Task AppCloseWaitsForAnAlreadyPendingPlacementCommitBeforeDisposingServices()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                using var form = new MainForm();
                var placement = form.Services.GetRequiredService<OverlayPlacementService>();
                using var preview = new Soulcrest.App.Overlay.OverlayPreviewRenderer(new Soulcrest.Core.Pets.PetCatalog([]));
                using var surface = new Soulcrest.App.Overlay.OverlayPlacementForm(form.Services.GetRequiredService<SettingsService>(), placement, preview);
                typeof(Soulcrest.App.Overlay.OverlayPlacementForm).GetField("_pendingSave", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(surface, pending.Task);
                typeof(Soulcrest.App.Overlay.OverlayPlacementForm).GetField("_savingEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(surface, true);
                typeof(MainForm).GetField("_placementWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, surface);
                var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
                typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [args]);
                Assert.True(args.Cancel);
                Assert.False((bool)typeof(MainForm).GetField("_closing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!);
                Assert.NotNull(form.Services.GetRequiredService<TrackerService>());
                form.Dispose(); // test teardown does not stand in for the user's Close action
                pending.SetResult();
                Application.DoEvents();
                completed.SetResult();
            }
            catch (Exception error) { pending.TrySetResult(); completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static WatchdogProbe AttachWatchdog(MainForm form)
    {
        var watchdog = new WatchdogProbe();
        typeof(MainForm).GetField("_watchdog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, watchdog);
        return watchdog;
    }

    private sealed class WatchdogProbe : IDisposable
    {
        internal int Stops { get; private set; }
        public void Dispose() => Stops++;
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
