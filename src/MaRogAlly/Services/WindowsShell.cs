using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaRogAlly.Core;
using MaRogAlly.Models;
using Microsoft.Win32;

namespace MaRogAlly.Services;

/// <summary>Fenêtre d'application ouverte sur la machine (mêmes règles que la barre des tâches de Windows).</summary>
public sealed record OpenWindow(IntPtr Handle, string Title, bool Minimized, string? IconSource);

/// <summary>Application lancée mais sans fenêtre (Steam ou Epic Games une fois « fermés »…) ; OpenTarget la fait réapparaître.</summary>
public sealed record BackgroundApp(string Name, string Path, string OpenTarget);

/// <summary>Application installée, telle que la liste le menu Démarrer de Windows.</summary>
public sealed record InstalledApp(string Name, string Id)
{
    public string IconSource => $@"shell:AppsFolder\{Id}";
}

/// <summary>Accès à Windows pour la barre de gauche : fenêtres ouvertes, icônes, applications, volets du système.</summary>
public static class WindowsShell
{
    // ---------------- Fenêtres ouvertes ----------------

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "Xaml_WindowedPopupClass",
    };

    public static List<OpenWindow> Enumerate(IntPtr self)
    {
        var list = new List<OpenWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == self || !IsWindowVisible(hwnd)) return true;
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            var appWindow = (style & WS_EX_APPWINDOW) != 0;
            if (!appWindow && ((style & WS_EX_TOOLWINDOW) != 0 || (style & WS_EX_NOACTIVATE) != 0 || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)) return true;
            // Applications du Store en arrière-plan : présentes mais « masquées » par Windows.
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            var title = new StringBuilder(256);
            if (GetWindowText(hwnd, title, title.Capacity) == 0) return true;
            var className = ClassOf(hwnd);
            if (ShellClasses.Contains(className)) return true;
            var source = className == "ApplicationFrameWindow" && AppIdOf(hwnd) is { } appId ? $@"shell:AppsFolder\{appId}" : ProcessPath(hwnd);
            list.Add(new OpenWindow(hwnd, title.ToString(), IsIconic(hwnd), source));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(128);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    public static void Minimize(IntPtr hwnd) => ShowWindow(hwnd, SW_MINIMIZE);
    /// <summary>Demande polie : l'application peut proposer d'enregistrer avant de se fermer.</summary>
    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    private static string? ProcessPath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return ProcessPath(pid);
    }

    private static string? ProcessPath(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    // ---------------- Applications en arrière-plan ----------------

    private static HashSet<string> _trayPrograms = [];
    private static DateTime _trayRead;
    private static readonly ConcurrentDictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Processus lancés sans fenêtre : ceux qui ont une icône dans la zone de notification (liste tenue par Windows)
    /// et les lanceurs de jeux, hors composants de Windows et applications qui ont déjà une fenêtre ouverte.
    /// </summary>
    public static List<BackgroundApp> ListBackgroundApps(IReadOnlyList<OpenWindow> windows)
    {
        var programs = TrayPrograms();
        var platforms = new Dictionary<string, PlatformInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in PlatformInfo.All)
            foreach (var name in info.LauncherProcessNames)
            {
                platforms[name] = info;
                programs.Add(name);
            }

        var withWindow = windows.Select(w => w.IconSource).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var apps = new Dictionary<string, BackgroundApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || !programs.Contains(process.ProcessName)) continue;
                if (ProcessPath((uint)process.Id) is not { } path || withWindow.Contains(path) || apps.ContainsKey(path)) continue;
                if (path.StartsWith(systemDir, StringComparison.OrdinalIgnoreCase)) continue;
                var platform = platforms.GetValueOrDefault(process.ProcessName);
                apps[path] = new BackgroundApp(platform?.Name ?? Describe(path, process.ProcessName), path, platform?.LauncherUri ?? path);
            }
        }
        // Une plateforme a parfois plusieurs processus sans fenêtre : une seule ligne par nom.
        return apps.Values.DistinctBy(a => a.Name).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Describe(string path, string fallback) => Descriptions.GetOrAdd(path, p =>
    {
        try { return FileVersionInfo.GetVersionInfo(p).FileDescription is { Length: > 0 } description ? description : fallback; }
        catch { return fallback; }
    });

    /// <summary>Noms des programmes qui ont eu une icône dans la zone de notification (relus au plus une fois par minute).</summary>
    private static HashSet<string> TrayPrograms()
    {
        if (DateTime.Now - _trayRead > TimeSpan.FromMinutes(1))
        {
            _trayRead = DateTime.Now;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var settings = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
                foreach (var id in settings?.GetSubKeyNames() ?? [])
                {
                    using var icon = settings!.OpenSubKey(id);
                    if (icon?.GetValue("ExecutablePath") is string program) names.Add(Path.GetFileNameWithoutExtension(program));
                }
            }
            catch (Exception ex) { Log.Error("Zone de notification", ex); }
            _trayPrograms = names;
        }
        return new HashSet<string>(_trayPrograms, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Identifiant d'une application du Store : sa fenêtre appartient à ApplicationFrameHost, sans icône utile.</summary>
    private static string? AppIdOf(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return null;
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            store.GetValue(ref key, out var value);
            var id = value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null;
            PropVariantClear(ref value);
            Marshal.ReleaseComObject(store);
            return id;
        }
        catch { return null; }
    }

    // ---------------- Icônes et applications (fil dédié : les objets du Shell veulent un fil STA) ----------------

    private static readonly BlockingCollection<Action> ShellQueue = StartShellThread();

    private static BlockingCollection<Action> StartShellThread()
    {
        var queue = new BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable())
                work();
        }) { IsBackground = true, Name = "WindowsShell" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return queue;
    }

    private static Task<T> OnShellThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        ShellQueue.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Icône d'un programme (chemin) ou d'une application (shell:AppsFolder\…), une seule fois par source.</summary>
    public static Task<ImageSource?> IconAsync(string source)
        => Icons.GetOrAdd(source, s => OnShellThread(() =>
        {
            try { return LoadIcon(s); }
            catch { return null; }
        }));

    private static ImageSource? LoadIcon(string source)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(source, IntPtr.Zero, ref iid, out var factory) != 0) return null;
        try
        {
            if (factory.GetImage(new NativeSize { Width = 64, Height = 64 }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out var bitmap) != 0) return null;
            try { return ToImage(bitmap); }
            finally { DeleteObject(bitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    /// <summary>Copie les pixels du bitmap Windows en gardant sa transparence (l'interop WPF standard la perd).</summary>
    private static ImageSource? ToImage(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<DibSection>(), out var dib) == 0 || dib.Bits == IntPtr.Zero || dib.BitsPixel != 32)
        {
            var fallback = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }
        var pixels = new byte[dib.WidthBytes * dib.Height];
        Marshal.Copy(dib.Bits, pixels, 0, pixels.Length);
        // Hauteur positive : les lignes sont rangées du bas vers le haut.
        if (dib.HeaderHeight > 0)
        {
            var flipped = new byte[pixels.Length];
            for (var y = 0; y < dib.Height; y++)
                Buffer.BlockCopy(pixels, y * dib.WidthBytes, flipped, (dib.Height - 1 - y) * dib.WidthBytes, dib.WidthBytes);
            pixels = flipped;
        }
        var image = BitmapSource.Create(dib.Width, dib.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, dib.WidthBytes);
        image.Freeze();
        return image;
    }

    /// <summary>Applications installées (programmes et applications du Store), hors désinstalleurs.</summary>
    public static Task<List<InstalledApp>> ListAppsAsync() => OnShellThread(() =>
    {
        var apps = new List<InstalledApp>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name, id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("désinstall", StringComparison.OrdinalIgnoreCase)) continue;
                apps.Add(new InstalledApp(name, id));
            }
        }
        catch (Exception ex) { Log.Error("Liste des applications", ex); }
        return apps.DistinctBy(a => a.Id).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>Exécutable du navigateur web par défaut, ou null.</summary>
    public static string? DefaultBrowser()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId) return null;
            using var open = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (open?.GetValue("") is not string command) return null;
            var exe = command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];
            return File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }

    /// <summary>Navigateur par défaut : sa fenêtre s'il est déjà ouvert, sinon on le lance.</summary>
    public static bool ShowBrowser(string? exe, IntPtr self)
    {
        if (exe is null) return GameLauncher.Open("https://www.google.com");
        if (Enumerate(self).FirstOrDefault(w => string.Equals(w.IconSource, exe, StringComparison.OrdinalIgnoreCase)) is { } window)
        {
            Activate(window.Handle);
            return true;
        }
        return GameLauncher.Open(exe);
    }

    /// <summary>
    /// Historique de lancement tenu par Windows (UserAssist : noms en ROT13, date de dernier lancement à l'octet 60).
    /// Les noms sont les mêmes identifiants que ceux de la liste des applications.
    /// </summary>
    public static Dictionary<string, DateTime> WindowsLaunchHistory()
    {
        var history = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in new[] { "{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}", "{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}" })
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{group}\Count");
                foreach (var name in key?.GetValueNames() ?? [])
                {
                    if (key!.GetValue(name) is not byte[] { Length: >= 68 } data) continue;
                    var ticks = BitConverter.ToInt64(data, 60);
                    if (ticks > 0) history[Rot13(name)] = DateTime.FromFileTime(ticks);
                }
            }
            catch (Exception ex) { Log.Error("Historique des applications", ex); }
        }
        return history;
    }

    private static string Rot13(string text) => string.Concat(text.Select(c => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c,
    }));

    /// <summary>Lanceur de jeux d'après son nom (à écarter des dernières applications).</summary>
    public static bool IsGameLauncher(string name)
    {
        var n = Text.Normalize(name);
        return n.Contains("launcher") || n.Contains("steam") || n.Contains("epic games") || n.Contains("gog galaxy") || n is "ea" or "ea app" or "origin"
            || n.Contains("ubisoft") || n.Contains("battle net") || n.Contains("battlenet") || n.Contains("xbox") || n.Contains("amazon games")
            || n.Contains("riot client") || n.Contains("itch") || n.Contains("rockstar games") || n.Contains("ankama");
    }

    public static bool LaunchApp(InstalledApp app) => GameLauncher.Open($@"shell:AppsFolder\{app.Id}");

    // ---------------- Volets du système ----------------

    private const byte VK_LWIN = 0x5B;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static void WinShortcut(byte? key)
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        if (key is byte k)
        {
            keybd_event(k, 0, 0, UIntPtr.Zero);
            keybd_event(k, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Volet Wi-Fi, Bluetooth, volume et luminosité (Win + A).</summary>
    public static void ShowQuickSettings() => WinShortcut((byte)'A');
    /// <summary>Volet des notifications (Win + N).</summary>
    public static void ShowNotifications() => WinShortcut((byte)'N');
    /// <summary>Bureau de Windows (Win + D).</summary>
    public static void ShowDesktop() => WinShortcut((byte)'D');
    public static void ShowStartMenu() => WinShortcut(null);

    /// <summary>Clavier tactile de Windows : le service TabTip, puis sa commande d'affichage.</summary>
    public static async Task ToggleTouchKeyboardAsync()
    {
        try
        {
            if (Process.GetProcessesByName("TabTip").Length == 0)
            {
                var tip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ink\TabTip.exe");
                Process.Start(new ProcessStartInfo(tip) { UseShellExecute = true });
                await Task.Delay(500);
            }
            await OnShellThread(() =>
            {
                var invocation = (ITipInvocation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4CE576FA-83DC-4F88-951C-9D0782B4E376"))!)!;
                invocation.Toggle(GetDesktopWindow());
                Marshal.ReleaseComObject(invocation);
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Error("Clavier tactile de Windows", ex);
            GameLauncher.Open("osk.exe"); // clavier visuel classique, en secours
        }
    }

    // ---------------- Windows ----------------

    private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14, SW_RESTORE = 9, SW_MINIMIZE = 6;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x8000000;
    private const uint GW_OWNER = 4, WM_CLOSE = 0x0010, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct PropVariant { public ushort Type; public ushort R1, R2, R3; public IntPtr Pointer; public IntPtr Extra; }

    /// <summary>DIBSECTION : BITMAP, puis l'en-tête dont la hauteur donne le sens des lignes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
        public uint HeaderSize;
        public int HeaderWidth, HeaderHeight;
        public ushort HeaderPlanes, HeaderBitCount;
        public uint Compression, SizeImage;
        public int XPels, YPels;
        public uint ClrUsed, ClrImportant, Mask1, Mask2, Mask3;
        public IntPtr Section;
        public uint Offset;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [ComImport, Guid("37C994E7-432B-4834-A2F7-DCE1F13B834B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr desktop);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItemImageFactory item);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr handle, int size, out DibSection value);
}
