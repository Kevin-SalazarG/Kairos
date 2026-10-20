using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Kairos.Ui;

namespace Kairos.Tests;

public static class WindowLifetimeTests
{
    [Test]
    public static void Replacing_a_window_detaches_the_closed_view_without_clearing_its_replacement()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            MainWindow? old = null, current = null;
            bool oldClosed = false, currentClosed = false;
            var vm = new SettingsViewModel
            {
                RequestAsync = static (_, _) => Task.FromResult<string?>(null),
            };
            try
            {
                // Match the real resources without showing a window or starting the engine.
                app = (Application)typeof(SettingsApp).GetMethod("CreateApplication", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, null)!;
                old = new MainWindow(vm);
                var oldPage = (ContentControl)old.FindName("PageHost");
                object oldContent = oldPage.Content;
                current = new MainWindow(vm);
                Func<bool> currentInUse = vm.InUse;
                string[] events = ["PropertyChanged", "ShareRequested", "PictureRequested", "NavigateRequested"];
                foreach (string name in events)
                    Assert.Equal(1, OwnedHandlers(vm, old, name), $"the old window starts attached to {name}");

                old.Close();
                oldClosed = true;
                foreach (string name in events)
                {
                    Assert.Equal(0, OwnedHandlers(vm, old, name), $"the closed window must release {name}");
                    Assert.Equal(1, OwnedHandlers(vm, current, name), $"the replacement must keep {name}");
                }
                Assert.True(ReferenceEquals(currentInUse, vm.InUse), "closing the old view must preserve the replacement's update guard");

                vm.VoiceSetUpCommand.Execute(null);
                Assert.True(ReferenceEquals(oldContent, oldPage.Content), "the closed view must not react to later navigation");
                var currentPage = (ContentControl)current.FindName("PageHost");
                Assert.True(currentPage.Content is Kairos.Ui.Pages.SmartSavePage, "navigation must still reach the replacement");

                current.Close();
                currentClosed = true;
                foreach (string name in events)
                    Assert.Equal(0, OwnedHandlers(vm, current, name), $"the final closed window must release {name}");
                Assert.True(!RefersTo(vm.InUse.Target, current) && !vm.InUse(), "the final update guard must not retain the closed window");
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                if (!oldClosed) old?.Close();
                if (!currentClosed) current?.Close();
                vm.Stop();
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new Exception(error.ToString());
    }

    static int OwnedHandlers(SettingsViewModel vm, MainWindow owner, string name) =>
        ((Delegate?)typeof(SettingsViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm))?
            .GetInvocationList().Count(handler => RefersTo(handler.Target, owner)) ?? 0;

    static bool RefersTo(object? target, object instance) => ReferenceEquals(target, instance) || target != null &&
        target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => ReferenceEquals(field.GetValue(target), instance));
}
