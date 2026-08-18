using System;
using System.Linq;
using System.Windows;
using System.Windows.Shell;
using SoundSync.Services;

namespace SoundSync
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // A launch that only carries a command - from the taskbar jump list - hands it to
            // the window that is already open and gets out of the way.
            string? command = e.Args.FirstOrDefault(
                a => string.Equals(a, SingleInstanceCommands.ResetWindowArgument, StringComparison.OrdinalIgnoreCase));

            if (command != null && SingleInstanceCommands.SendToRunningInstance(command))
            {
                Shutdown();
                return;
            }

            BuildJumpList();
            base.OnStartup(e);
        }

        /// <summary>
        /// Fills the right-click menu on the taskbar button. Windows starts a fresh copy with
        /// the argument; App.OnStartup forwards it to the running one.
        /// </summary>
        private static void BuildJumpList()
        {
            try
            {
                string exe = Environment.ProcessPath ?? string.Empty;
                if (string.IsNullOrEmpty(exe)) return;

                var resetTask = new JumpTask
                {
                    Title = "Reset window size",
                    Description = "Forget the saved size and position and fit the window to this monitor again.",
                    ApplicationPath = exe,
                    Arguments = SingleInstanceCommands.ResetWindowArgument,
                    IconResourcePath = exe
                };

                var jumpList = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
                jumpList.JumpItems.Add(resetTask);

                JumpList.SetJumpList(Current, jumpList);
                jumpList.Apply();
            }
            catch
            {
                // No jump list is a cosmetic loss; the tray icon menu offers the same thing.
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SingleInstanceCommands.StopListening();
            base.OnExit(e);
        }
    }
}
