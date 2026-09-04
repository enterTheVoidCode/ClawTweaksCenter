using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// The icon for a library tab: the real store logo where one can be had, a Segoe glyph otherwise.
    ///
    /// WHY THE LOGOS ARE EXTRACTED AND NOT SHIPPED. Steam's and Epic's marks are their trademarks. The
    /// obvious route - copy the PNGs out of Playnite's plugin folders, or off the web - would put
    /// somebody else's brand assets in our installer, and that is the same unanswered licensing
    /// question that has kept Assets/ButtonIcons out of the public repo to this day. Reading the icon
    /// out of the launcher the user has already installed sidesteps it completely: nothing is
    /// redistributed, the icon always matches the version actually on the machine, and a store that is
    /// not installed has no tab worth a logo anyway.
    ///
    /// Decided with the user on 2026-09-04, against shipping Playnite's copies.
    /// </summary>
    internal static class StoreIcons
    {
        // Resolved at most once per group per session. An extraction is a file read plus a GDI handle;
        // the tab strip rebuilds on every tab change, every count update and every immersive dim, so
        // without this it would be doing that several times a second.
        //
        // A MISS IS CACHED TOO - that is what the ContainsKey test is for rather than a null check.
        // "Epic is not installed" is a stable answer, and re-deriving it on every rebuild would mean
        // hitting the registry forever on exactly the machines where it can never succeed.
        private static readonly Dictionary<LibraryGroup, ImageSource> Cache =
            new Dictionary<LibraryGroup, ImageSource>();

        /// <summary>The store's own icon, or null when there is none to be had.</summary>
        internal static ImageSource For(LibraryGroup group)
        {
            if (Cache.TryGetValue(group, out var cached)) return cached;

            ImageSource icon = null;
            try
            {
                // A Store app has no exe we may touch, so the SHELL is asked for its icon instead -
                // the same picture Windows itself draws in the Start menu. See ShellIconFor.
                if (group == LibraryGroup.Xbox)
                {
                    icon = ShellIconFor(XboxAumid, 32);
                    Cache[group] = icon;
                    return icon;
                }

                string file = LauncherExeFor(group);
                if (!string.IsNullOrEmpty(file) && File.Exists(file)) icon = Extract(file);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write($"StoreIcons: {group} could not be resolved: {ex.Message}");
            }

            Cache[group] = icon;
            return icon;
        }

        private static ImageSource Extract(string exePath)
        {
            using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
            {
                if (ico == null) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                // Frozen so the strip can be rebuilt from any thread later without re-extracting.
                // Safe here in a way it is NOT for a BitmapImage off an http URI - that one downloads
                // asynchronously and throws on Freeze; this one is already fully decoded in memory.
                src.Freeze();
                return src;
            }
        }

        private static string LauncherExeFor(LibraryGroup group)
        {
            switch (group)
            {
                // Steam's install root is already resolved for launching games; the exe next to it is
                // the same one whose icon Windows shows in the taskbar.
                case LibraryGroup.Steam:
                    string root = SteamSource.SteamPath();
                    return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "steam.exe");

                // Epic has no path in our own scanner - only a manifest directory - so it comes from
                // the protocol handler it registers. That is the same URI scheme Center already uses
                // to launch Epic games, so if this fails, launching was never going to work either.
                case LibraryGroup.Epic:
                    return ExeFromProtocol("com.epicgames.launcher");

                default:
                    return null;
            }
        }

        /// <summary>The exe behind a registered URI scheme, e.g. "com.epicgames.launcher".</summary>
        private static string ExeFromProtocol(string scheme)
        {
            using (var key = Registry.ClassesRoot.OpenSubKey(scheme + @"\shell\open\command"))
            {
                string command = key?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(command)) return null;

                // The value is a command line, not a path: typically "C:\...\Launcher.exe" "%1".
                // Quoted is the normal form, so take what is between the first pair of quotes; fall
                // back to the first whitespace-delimited token for the unquoted case.
                if (command[0] == '"')
                {
                    int end = command.IndexOf('"', 1);
                    return end > 1 ? command.Substring(1, end - 1) : null;
                }
                int space = command.IndexOf(' ');
                return space > 0 ? command.Substring(0, space) : command;
            }
        }

        // The Xbox app's AUMID. Stable across versions - the family name is derived from the
        // publisher id and the app id is declared in the manifest - which is exactly why this is
        // addressed by AUMID and not by a path under WindowsApps.
        private const string XboxAumid =
            "shell:AppsFolder\\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

        /// <summary>
        /// The icon the SHELL draws for an app, by AUMID. The one route to a Store app's artwork that
        /// a normal user process actually has.
        ///
        /// EVERY OTHER ROUTE WAS TRIED AND MEASURED SHUT, 2026-09-04:
        ///
        ///   Directory.GetDirectories(...WindowsApps, "Microsoft.GamingApp_*")   0 results
        ///   HKLM ...AppModel\Repository\Packages\&lt;full name&gt;                    denied
        ///   HKCU ...ActivatableClasses\Package                                  denied
        ///   Test-Path on a FULLY KNOWN path under WindowsApps                    True
        ///
        /// The last line is the trap: traverse is allowed, LISTING is not. A shell that lists the
        /// folder in Explorer, and a `ls` that reads one known subfolder, both work - so the folder
        /// looks readable right up until code asks it for its children and silently gets nothing.
        ///
        /// SIIGBF_ICONONLY (0x04) so the shell hands back the app's icon rather than a thumbnail of
        /// its content - for an app those are the same thing today, but the flag says which we mean.
        /// </summary>
        private static ImageSource ShellIconFor(string parsingName, int px)
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            object item;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out item);
            if (item == null) return null;

            IntPtr hbitmap = IntPtr.Zero;
            try
            {
                ((IShellItemImageFactory)item).GetImage(new SIZE { cx = px, cy = px }, 0x04, out hbitmap);
                if (hbitmap == IntPtr.Zero) return null;

                var info = new BITMAP();
                if (GetObject(hbitmap, Marshal.SizeOf(typeof(BITMAP)), ref info) == 0) return null;
                if (info.bmWidth <= 0 || info.bmHeight <= 0 || info.bmBits == IntPtr.Zero) return null;

                // Pbgra32: the shell's bitmap carries PREMULTIPLIED alpha. Reading it as Bgra32
                // would darken every edge pixel, and the more common CreateBitmapSourceFromHBitmap
                // drops alpha altogether - which is how this ends up as a logo on a black square.
                int stride = info.bmWidth * 4;
                var img = BitmapSource.Create(info.bmWidth, info.bmHeight, 96, 96,
                                              PixelFormats.Pbgra32, null,
                                              info.bmBits, stride * info.bmHeight, stride);

                // 🔴 AND THEN FLIPPED. The comment here used to claim the shell returns a TOP-DOWN
                // DIB; it does not, and the Xbox logo shipped upside down on 2026-09-04 because of
                // it. A DIB is bottom-up by default - row 0 of the buffer is the BOTTOM row of the
                // picture - and BitmapSource.Create reads a positive stride as top-down, so the two
                // disagree by exactly a vertical mirror.
                //
                // ⚠️ It cannot be fixed with a negative stride: BitmapSource.Create rejects one.
                // Mirroring afterwards is the supported way, and it costs nothing here - this runs
                // once per session behind the icon cache.
                var flipped = new TransformedBitmap(img, new ScaleTransform(1, -1));
                flipped.Freeze();
                return flipped;
            }
            finally
            {
                if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
                Marshal.ReleaseComObject(item);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public short bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

        // The ROMs tab: a drawn console, not a font glyph.
        //
        // Segoe has no console. Every candidate was the same game controller the Steam and Epic tabs
        // already fall back to, which on a strip where inactive tabs show ONLY their icon means three
        // tabs the user cannot tell apart.
        //
        // Supplied by the user as an SVG (game-consoles/consoles-07.svg) and transcribed here as
        // geometry rather than shipped as a file: it is a dozen numbers, it needs no build action and
        // no installer entry, and as a stroked vector it takes the theme's own colour and stays sharp
        // at any DPI - which a PNG at 16 px would not.
        private const string RomsOutlinePath =
            "M45.9,41H31.67l-1.1-1H17.43l-1.1,1H2.1A1.05,1.05,0,0,1,1,40V8A1.05,1.05,0,0,1,2.1,7H16.33" +
            "l1.1,1H30.57l1.1-1H45.9A1.05,1.05,0,0,1,47,8V40A1.05,1.05,0,0,1,45.9,41Z";

        /// <summary>The ROMs icon as a drawn vector, or null for every other tab.</summary>
        internal static UIElement VectorFor(LibraryGroup group, Brush stroke, double size)
        {
            if (group != LibraryGroup.Roms) return null;

            var shapes = new GeometryGroup();
            shapes.Children.Add(Geometry.Parse(RomsOutlinePath));                  // the console body
            shapes.Children.Add(new EllipseGeometry(new Point(24, 24), 13, 13));   // the screen
            shapes.Children.Add(new LineGeometry(new Point(32, 41), new Point(32, 34)));
            shapes.Children.Add(new LineGeometry(new Point(32, 14), new Point(32, 7)));
            shapes.Children.Add(new LineGeometry(new Point(16, 41), new Point(16, 34)));
            shapes.Children.Add(new LineGeometry(new Point(16, 14), new Point(16, 7)));
            shapes.Children.Add(new EllipseGeometry(new Point(7, 35), 2, 2));      // the two knobs
            shapes.Children.Add(new EllipseGeometry(new Point(41, 35), 2, 2));
            shapes.Children.Add(new EllipseGeometry(new Point(5.5, 24.5), 0.5, 0.5));
            shapes.Freeze();

            var path = new System.Windows.Shapes.Path
            {
                Data = shapes,
                Stroke = stroke,
                // 3, not the SVG's 2. A Viewbox scales the stroke along with the drawing, and 48
                // units shown at 20 px is well under half - so the source's 2 would land below a
                // pixel and read as a grey haze. 3 keeps it a solid line.
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };

            return new System.Windows.Controls.Viewbox
            {
                Child = path,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>
        /// The glyph a tab falls back to. Segoe Fluent Icons, with Segoe MDL2 Assets behind it - the
        /// codepoints used here exist in both.
        ///
        /// Deliberately a SMALL, well-known set with repeats rather than a distinct glyph per tab. A
        /// codepoint that does not exist renders as an empty box, and on a strip where the inactive
        /// tabs show nothing BUT their icon, a box is a tab the user cannot identify at all. Steam and
        /// Epic normally get their real logo above, so their shared controller glyph is the rare case,
        /// not the usual one.
        /// </summary>
        internal static string GlyphFor(LibraryGroup group)
        {
            switch (group)
            {
                case LibraryGroup.Recent: return "\uE823";        // Recent (clock)
                case LibraryGroup.Favorites: return "\uE735";     // FavoriteStarFill
                case LibraryGroup.All: return "\uE71D";           // AllApps
                case LibraryGroup.OtherStores: return "\uE719";   // Shop
                case LibraryGroup.Misc: return "\uE8B7";          // Folder
                case LibraryGroup.NotInstalled: return "\uE896";  // Download
                default: return "\uE7FC";                              // Game (Steam, Epic, Xbox, ROMs)
            }
        }
    }
}
