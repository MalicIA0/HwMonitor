using System;
using System.Runtime.InteropServices;

namespace HwMonitor;

/// <summary>
/// Résolution de rendu : taille en pixels de la fenêtre au premier plan (donc la
/// résolution à laquelle le jeu affiche réellement), avec repli sur le mode
/// d'affichage de l'écran qui la porte quand rien d'utile n'est au premier plan
/// (bureau, overlay lui-même, petite fenêtre).
///
/// L'appli est déclarée PerMonitorV2 (app.manifest) : GetClientRect renvoie déjà
/// des pixels PHYSIQUES, aucune conversion DPI n'est nécessaire. En plein écran
/// EXCLUSIF, Windows change le mode de l'écran : les deux sources concordent.
/// </summary>
public static class ResolutionService
{
    /// <summary>Résolution affichée. Width == 0 ⇒ rien de fiable à afficher (« — »).</summary>
    public readonly record struct Info(int Width, int Height, int ScreenWidth, int ScreenHeight, int Hz, bool FromWindow)
    {
        public bool Ok => Width > 0 && Height > 0;
    }

    // En dessous, ce n'est pas une surface de rendu (barre de recherche, popup,
    // bulle de notification qui vole le premier plan une fraction de seconde).
    private const int MinWidth = 640, MinHeight = 360;

    public static Info Get(IntPtr overlayHwnd)
    {
        IntPtr fg = GetForegroundWindow();

        // Mode de l'écran : celui du premier plan si possible, sinon celui de l'overlay.
        var (sw, sh, hz) = ScreenMode(fg != IntPtr.Zero ? fg : overlayHwnd);

        if (fg != IntPtr.Zero && fg != overlayHwnd && fg != GetShellWindow() && fg != GetDesktopWindow()
            && GetClientRect(fg, out var rc))
        {
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w >= MinWidth && h >= MinHeight)
                return new Info(w, h, sw, sh, hz, FromWindow: true);
        }

        if (sw > 0 && sh > 0) return new Info(sw, sh, sw, sh, hz, FromWindow: false);
        return default; // « — »
    }

    /// <summary>Mode d'affichage courant de l'écran qui porte <paramref name="hwnd"/>.</summary>
    private static (int w, int h, int hz) ScreenMode(IntPtr hwnd)
    {
        try
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = new string('\0', 32) };
            bool got = GetMonitorInfoW(mon, ref mi);

            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>(), dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32) };
            if (EnumDisplaySettingsW(got ? mi.szDevice : null, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmPelsWidth > 0)
                return ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);

            // Repli : le rectangle du moniteur (pixels physiques, appli PerMonitorV2).
            if (got && mi.rcMonitor.Right > mi.rcMonitor.Left)
                return (mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top, 0);
        }
        catch { /* écran débranché en plein rafraîchissement : on affichera « — » */ }
        return (0, 0, 0);
    }

    /// <summary>Palier commercial (« 4K », « 1440p »…) — sert au code couleur et à l'infobulle.</summary>
    public static string Tier(int width, int height)
    {
        if (height >= 4320) return "8K";
        if (height >= 2160 || width >= 3840) return "4K";
        if (height >= 1440 || width >= 2560) return "1440p";
        if (height >= 1080 || width >= 1920) return "1080p";
        if (height >= 720) return "720p";
        return $"{height}p";
    }

    /// <summary>
    /// Nom commercial d'une définition standard (« Full HD », « 4K »…), affiché à la
    /// place des pixels quand la taille tombe pile dessus. <c>null</c> sinon : une
    /// fenêtre de 1600×900 n'est aucun standard, on montre alors les pixels exacts.
    /// </summary>
    public static string? Label(int width, int height) => (width, height) switch
    {
        (7680, 4320) => "8K",
        (3840, 2160) => "4K",
        (2560, 1440) => "QHD",
        (1920, 1080) => "Full HD",
        (1280, 720)  => "HD",
        _ => null
    };

    /* ---------- Interop ---------- */

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);
}
