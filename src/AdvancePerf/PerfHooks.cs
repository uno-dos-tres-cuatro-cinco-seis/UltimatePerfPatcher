using System;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace AdvancePerf
{
    /// <summary>
    /// Call targets injected into "8BitDo Ultimate Software V2.dll" by the patcher.
    /// </summary>
    public static class PerfHooks
    {
        // Written on the UI thread, read by the polling threads.
        private static volatile bool windowShown = true;

        /// <summary>
        /// Replaces MainWindow's unconditional StartReadKey() call in its constructor.
        /// The global low-level keyboard hook is only needed while the user works inside the
        /// app (macro recording, Ctrl multi-select in the LED views), so it is installed while
        /// the window is active and removed when it loses focus.
        /// </summary>
        public static void GateKeyboardHook(object mainWindow)
        {
            var window = (Window)mainWindow;
            var type = mainWindow.GetType();
            var start = type.GetMethod("StartReadKey", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            var stop = type.GetMethod("StopReadKey", BindingFlags.Instance | BindingFlags.Public);

            window.Activated += (s, e) => start.Invoke(mainWindow, null);
            window.Deactivated += (s, e) =>
            {
                try { stop.Invoke(mainWindow, new object[] { null, null }); }
                catch (TargetInvocationException) { /* StopReadKey throws when unhooking fails; keep running */ }
            };
            window.StateChanged += (s, e) => UpdateShown(window);
            window.IsVisibleChanged += (s, e) => UpdateShown(window);
        }

        private static void UpdateShown(Window window)
        {
            windowShown = window.IsVisible && window.WindowState != WindowState.Minimized;
        }

        private static int settingsSaved;

        /// <summary>
        /// Replaces Settings1.Default.Save() inside LanguageTools.IsChineseSimple(). That method is
        /// called for every UI string (694 call sites) and, until the user picks a language manually,
        /// rewrote user.config on each call. The detected language cannot change while the
        /// process runs, so persisting it once is enough.
        /// </summary>
        public static void SaveSettingsOnce(object settings)
        {
            if (Interlocked.Exchange(ref settingsSaved, 1) == 0)
            {
                settings.GetType().GetMethod("Save", Type.EmptyTypes).Invoke(settings, null);
            }
        }

        /// <summary>
        /// Replaces Thread.Sleep(ms) at the head of the device polling loops. While the window is
        /// hidden (tray) or minimized the loop runs 5x less often, but wakes up within 100 ms
        /// once the window is shown again.
        /// </summary>
        public static void PollSleep(int milliseconds)
        {
            Thread.Sleep(milliseconds);
            for (int waited = 0; !windowShown && waited < 4 * milliseconds; waited += 100)
            {
                Thread.Sleep(100);
            }
        }
    }
}
