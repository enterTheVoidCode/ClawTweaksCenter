using System;
using System.Windows;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Center's entry point, written out instead of the one WPF generates from App.xaml.
    ///
    /// It exists for ONE reason: the library starts a copy of Center every half minute to read the
    /// Steam friends list (<see cref="Library.SteamFriends"/>), and the generated entry point shows
    /// the splash screen before any of our code runs. Every friends refresh flashed the splash over
    /// the library (reported on the device 2026-09-11). Here the reader argument is checked first,
    /// and only a real start gets the splash.
    ///
    /// Selected by &lt;StartupObject&gt; in the csproj. The splash image is an ordinary Resource now
    /// rather than a SplashScreen build item - that build item is what generated the early splash.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // Ahead of the splash, Velopack's hook, the single-instance gate and every window: none
            // of them may run in a process that only exists to talk to steamclient64.dll for a second.
            if (Array.Exists(args, a => a.Equals(Library.SteamFriends.ChildArg, StringComparison.Ordinal)))
                return Library.SteamFriends.RunChild();

            // The same two lines WPF generates for a SplashScreen build item: shown before the App
            // instance exists, closed by itself once the first window appears.
            new SplashScreen("assets/branding/splash.png").Show(true);

            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
