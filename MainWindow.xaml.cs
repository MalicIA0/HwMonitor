using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace HwMonitor;

public partial class MainWindow : Window
{
    private HardwareService? _hw;
    private FpsService? _fps;
    private DispatcherTimer? _timer;
    private bool _busy;
    private StreamWriter? _fpsLog;

    // Valeurs affichees (creees en code : la disposition est reconstruite dynamiquement)
    private TextBlock CpuTempText = null!, GpuTempText = null!, RamLoadText = null!, ResText = null!, FpsText = null!, HdrText = null!, PadText = null!;

    // Disposition courante : "ligne" | "colonne" | "mixte"
    private string _layout = "mixte";

    private static readonly FontFamily UiFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily MonoFont = new("Consolas");

    /// <summary>
    /// Cran de taille de police applique a TOUTE l'appli. Les tailles ecrites plus bas
    /// sont les tailles de REFERENCE ; ce decalage s'y ajoute au moment de l'affichage.
    /// Un cran plus petit = -1, un cran plus grand = +1 : une seule valeur a changer,
    /// les proportions entre les elements restent celles d'origine.
    /// </summary>
    private const double FontStep = -2;

    /// <summary>Taille de reference -> taille reellement affichee.</summary>
    private static double Pt(double size) => size + FontStep;

    // Exposees au XAML via x:Static : le chrome suit le meme cran que les valeurs.
    public static double PtGhost => Pt(11);      // croix + engrenage
    public static double PtMenu => Pt(12);       // options du menu de disposition
    public static double PtMenuTitle => Pt(9);   // titre « DISPOSITION »

    /// <summary>
    /// Opacite de l'overlay AU REPOS : plus bas = plus discret par-dessus un jeu.
    /// Le survol la ramene a 1, donc rien n'est perdu quand on le regarde vraiment.
    /// 1 = comportement d'avant. Seconde (et derniere) molette du "plus discret".
    /// </summary>
    public static double OpacityRest => 0.7;

    // Couleurs d'accent des libelles
    private const string CpuColor = "#38BDF8", GpuColor = "#4ADE80", RamColor = "#A78BFA", ResColor = "#F472B6", FpsColor = "#9AA0AE", HdrColor = "#FFD24A", PadColor = "#00D4FF";

    // Palette FPS + temperatures
    private static readonly Brush Green = Frozen("#4ADE80");
    private static readonly Brush Orange = Frozen("#FBBF24");
    private static readonly Brush Red = Frozen("#F87171");
    private static readonly Brush Violet = Frozen("#A78BFA");
    private static readonly Brush Gold = Frozen("#FFD24A");
    private static readonly Brush Grey = Frozen("#7A7A88");
    private static readonly Brush White = Frozen("#F4F4F8");

    // Halos assortis (glow) pour les valeurs importantes
    private static readonly Effect GlowGreen = MakeGlow("#4ADE80");
    private static readonly Effect GlowOrange = MakeGlow("#FBBF24");
    private static readonly Effect GlowRed = MakeGlow("#F87171");
    private static readonly Effect GlowViolet = MakeGlow("#A78BFA");
    private static readonly Effect GlowGold = MakeGlow("#FFD24A");

    // Ombre noire de lisibilite (fond transparent, par-dessus un jeu)
    private static readonly Effect ShadowBlack = MakeShadow();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    public MainWindow()
    {
        InitializeComponent();
        InitMetrics();
        LoadLayout();
        BuildBody();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /* ---------- Construction / disposition ---------- */

    private void InitMetrics()
    {
        CpuTempText = MakeValue("--°");
        GpuTempText = MakeValue("--°");
        RamLoadText = MakeValue("--%");
        ResText = MakeValue("—");
        FpsText = MakeValue("—");
        HdrText = MakeValue("—");
        PadText = MakeName("—");
        // La valeur HDR est cliquable : bascule le HDR directement depuis l'overlay.
        // e.Handled empeche le clic de declencher le DragMove de la fenetre.
        HdrText.Cursor = System.Windows.Input.Cursors.Hand;
        HdrText.ToolTip = "Cliquer pour activer / couper le HDR";
        HdrText.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            HdrService.Toggle();
            ApplyHdr();
        };
    }

    private static TextBlock MakeValue(string initial) => new()
    {
        Text = initial,
        FontFamily = MonoFont,
        FontSize = Pt(14),
        Foreground = Frozen("#F4F4F8"),
        VerticalAlignment = VerticalAlignment.Center,
        Effect = ShadowBlack
    };

    /// <summary>Valeur textuelle (nom de profil) : police UI, plus lisible que le mono pour des mots.</summary>
    private static TextBlock MakeName(string initial) => new()
    {
        Text = initial,
        FontFamily = UiFont,
        FontSize = Pt(13),
        FontWeight = FontWeights.SemiBold,
        Foreground = Frozen("#F4F4F8"),
        VerticalAlignment = VerticalAlignment.Center,
        Effect = ShadowBlack
    };

    private static TextBlock MakeLabel(string text, string colorHex, double size = 11.5, double minWidth = 0) => new()
    {
        Text = text,
        FontFamily = UiFont,
        FontSize = Pt(size),
        FontWeight = FontWeights.Bold,
        Foreground = Frozen(colorHex),
        MinWidth = minWidth,
        VerticalAlignment = VerticalAlignment.Center,
        Effect = ShadowBlack
    };

    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal };

    /// <summary>Detache une valeur de son parent avant de la re-parenter (WPF interdit 2 parents).</summary>
    private static void Detach(TextBlock t)
    {
        if (t.Parent is Panel p) p.Children.Remove(t);
    }

    private void BuildBody()
    {
        Detach(CpuTempText); Detach(GpuTempText); Detach(RamLoadText); Detach(ResText); Detach(FpsText); Detach(HdrText); Detach(PadText);
        Body.Children.Clear();

        switch (_layout)
        {
            case "ligne": BuildLigne(); break;
            case "colonne": BuildColonne(); break;
            default: BuildMixte(); break;
        }

        UpdateMenuChecks();
    }

    // Tout sur une seule ligne horizontale.
    private void BuildLigne()
    {
        FpsText.FontSize = Pt(14);
        var row = Row();
        void Add(string lbl, string col, TextBlock val, bool last = false)
        {
            row.Children.Add(MakeLabel(lbl, col));
            val.MinWidth = 0; // colonne aligne les valeurs, pas cette disposition-ci
            val.Margin = new Thickness(5, 0, last ? 0 : 14, 0);
            row.Children.Add(val);
        }
        Add("CPU", CpuColor, CpuTempText);
        Add("GPU", GpuColor, GpuTempText);
        Add("RAM", RamColor, RamLoadText);
        ResText.FontSize = Pt(13); // « 2560×1440 » : un cran plus petit pour ne pas étirer la ligne
        Add("RES", ResColor, ResText);
        Add("FPS", FpsColor, FpsText);
        Add("HDR", HdrColor, HdrText, last: true);
        Body.Children.Add(row);
        // Le nom du profil ne tient pas sur la meme ligne : il va juste en dessous.
        Body.Children.Add(PadRow(11.5, 3));
    }

    // Tout empile : une metrique par ligne, valeurs alignees.
    private void BuildColonne()
    {
        FpsText.FontSize = Pt(14);
        void AddRow(string lbl, string col, TextBlock val, bool first = false)
        {
            var row = Row();
            row.Margin = new Thickness(0, first ? 0 : 3, 0, 0);
            row.Children.Add(MakeLabel(lbl, col, minWidth: 48)); // 48 : « PROFIL » est plus large que « CPU »
            val.MinWidth = 0;
            val.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(val);
            Body.Children.Add(row);
        }
        AddRow("CPU", CpuColor, CpuTempText, first: true);
        AddRow("GPU", GpuColor, GpuTempText);
        AddRow("RAM", RamColor, RamLoadText);
        ResText.FontSize = Pt(14);
        AddRow("RES", ResColor, ResText);
        AddRow("FPS", FpsColor, FpsText);
        AddRow("PROFIL", PadColor, PadText);
        AddRow("HDR", HdrColor, HdrText);
    }

    // Mixte : temperatures + RAM + resolution sur une ligne, separateur, puis FPS en dessous.
    private void BuildMixte()
    {
        var temps = Row();
        void AddTemp(string lbl, string col, TextBlock val, bool last = false)
        {
            temps.Children.Add(MakeLabel(lbl, col));
            val.MinWidth = 0;
            val.Margin = new Thickness(5, 0, last ? 0 : 16, 0);
            temps.Children.Add(val);
        }
        AddTemp("CPU", CpuColor, CpuTempText);
        AddTemp("GPU", GpuColor, GpuTempText);
        AddTemp("RAM", RamColor, RamLoadText);
        ResText.FontSize = Pt(13); // la ligne du haut porte deja 3 valeurs : la resolution reste discrete
        AddTemp("RES", ResColor, ResText, last: true);
        Body.Children.Add(temps);

        Body.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 1,
            Fill = Frozen("#40FFFFFF"),
            Margin = new Thickness(0, 7, 0, 0),
            Effect = ShadowBlack
        });

        var fpsRow = Row();
        fpsRow.Margin = new Thickness(0, 4, 0, 0);
        fpsRow.Children.Add(MakeLabel("FPS", "#7A7A88", size: 9));
        FpsText.FontSize = Pt(16);
        FpsText.MinWidth = 0;
        FpsText.Margin = new Thickness(8, 0, 0, 0);
        fpsRow.Children.Add(FpsText);
        // HDR à droite du FPS
        var hdrLbl = MakeLabel("HDR", HdrColor, size: 9);
        hdrLbl.Margin = new Thickness(18, 0, 0, 0);
        hdrLbl.VerticalAlignment = VerticalAlignment.Center;
        fpsRow.Children.Add(hdrLbl);
        HdrText.FontSize = Pt(14);
        HdrText.Margin = new Thickness(6, 0, 0, 0);
        fpsRow.Children.Add(HdrText);
        Body.Children.Add(fpsRow);
        Body.Children.Add(PadRow(9, 3));
    }

    /// <summary>Ligne « PROFIL » + nom : le profil DualForge actuellement sur la manette.</summary>
    private StackPanel PadRow(double labelSize, double topMargin)
    {
        var row = Row();
        row.Margin = new Thickness(0, topMargin, 0, 0);
        row.Children.Add(MakeLabel("PROFIL", PadColor, size: labelSize));
        PadText.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(PadText);
        return row;
    }

    private void UpdateMenuChecks()
    {
        OptLigne.Content = (_layout == "ligne" ? "✓  " : "     ") + "En ligne";
        OptColonne.Content = (_layout == "colonne" ? "✓  " : "     ") + "En colonne";
        OptMixte.Content = (_layout == "mixte" ? "✓  " : "     ") + "Mixte";
    }

    private void GearBtn_Click(object sender, RoutedEventArgs e) => LayoutPopup.IsOpen = true;

    private void LayoutOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            _layout = tag;
            SaveLayout();
            BuildBody();
        }
        LayoutPopup.IsOpen = false;
    }

    private static string LayoutFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HwMonitor", "overlay-layout.txt");

    private void LoadLayout()
    {
        try { _layout = File.ReadAllText(LayoutFile).Trim(); } catch { _layout = "mixte"; }
        if (_layout != "ligne" && _layout != "colonne" && _layout != "mixte") _layout = "mixte";
    }

    private void SaveLayout()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)!);
            File.WriteAllText(LayoutFile, _layout);
        }
        catch { /* pas grave : on garde juste le defaut au prochain lancement */ }
    }

    /* ---------- Cycle de vie ---------- */

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Coin haut-gauche de l'ecran
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + 12;
        Top = wa.Top + 12;

        try
        {
            _hw = new HardwareService();
            // Dump de diagnostic (tous les capteurs + valeurs) au demarrage
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "hwmonitor_sensors.txt"), _hw.DumpAll()); } catch { }
        }
        catch (Exception ex)
        {
            // Plus de pastille d'etat : on signale l'echec des capteurs sur la valeur CPU
            CpuTempText.ToolTip = "Capteurs KO (lancer en admin) : " + ex.Message;
        }

        _fps = new FpsService();
        try { _fps.Start(Path.Combine(AppContext.BaseDirectory, "PresentMon.exe")); }
        catch { /* la FPS reste a "—" */ }

        // Journal live (1 ligne/s) : premier plan detecte + PresentMon a-t-il des frames pour ce PID.
        // Sert a diagnostiquer un FPS bloque sur "—" pendant une vraie session de jeu.
        try
        {
            _fpsLog = new StreamWriter(Path.Combine(Path.GetTempPath(), "hwmonitor_fps_live.txt"), append: false) { AutoFlush = true };
            _fpsLog.WriteLine("heure               | pid     | process              | vu_par_presentmon | fps");
        }
        catch { /* pas grave, diagnostic seulement */ }

        // Diagnostic FPS ecrit 3s apres le lancement (laisse le temps a PresentMon
        // de demarrer/planter) : lisible dans %TEMP%\hwmonitor_fps_diag.txt
        var diagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        diagTimer.Tick += (_, _) =>
        {
            diagTimer.Stop();
            WriteFpsDiag();
            UpdateFpsTooltip();
        };
        diagTimer.Start();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += Tick;
        _timer.Start();
        Tick(this, EventArgs.Empty); // premier rafraichissement immediat
    }

    private void WriteFpsDiag()
    {
        if (_fps == null) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== HwMonitor : diagnostic FPS ===");
            sb.AppendLine(DateTime.Now.ToString());
            sb.AppendLine($"Started    : {_fps.Started}");
            sb.AppendLine($"ExitCode   : {(_fps.ExitCode.HasValue ? _fps.ExitCode.Value.ToString() : "(toujours actif ou jamais lance)")}");
            sb.AppendLine($"PidCount   : {_fps.PidCount}  (nb de process dont une frame a ete vue)");
            sb.AppendLine($"LastError  : {_fps.LastError}");
            sb.AppendLine($"Menage ETW : {FpsService.CleanupLog}");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "hwmonitor_fps_diag.txt"), sb.ToString());
        }
        catch { /* rien */ }
    }

    private void UpdateFpsTooltip()
    {
        if (_fps == null) return;
        if (!_fps.Started)
            FpsText.ToolTip = "PresentMon n'a pas demarre : " + _fps.LastError;
        else if (_fps.ExitCode.HasValue)
            FpsText.ToolTip = $"PresentMon s'est arrete (code {_fps.ExitCode}) : {_fps.LastError}";
        else
            FpsText.ToolTip = null;
    }

    private async void Tick(object? sender, EventArgs e)
    {
        // Reaffirme le premier-plan a chaque tick : certains jeux (ou Windows)
        // repassent leur fenetre au-dessus de la nôtre malgre Topmost="True".
        // Sans effet en plein ecran EXCLUSIF (DWM ne compose plus rien) :
        // la seule solution est alors le mode Bordureless/Fenetre sans bordure du jeu.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // Independant des capteurs (simple lecture de fichier) : le profil reste
        // affiche meme si les capteurs materiels sont KO.
        ApplyPad();
        ApplyRes();

        if (_busy || _hw == null) return;
        _busy = true;
        try
        {
            var r = await Task.Run(() => _hw!.Read());
            ApplyCpu(r);
            ApplyGpu(r);
            ApplyRam(r);
            ApplyFps();
            ApplyHdr();
        }
        catch { /* on ignore un tick rate */ }
        finally { _busy = false; }
    }

    private void ApplyCpu(HwReading r)
    {
        // Ryzen : chaud = normal. Au-delà de 95°C (throttle proche) le texte s'enflamme comme le FPS.
        SetTemp(CpuTempText, r.CpuTemp, warn: 70, hot: 85, fire: 95);
    }

    private void ApplyGpu(HwReading r)
    {
        SetTemp(GpuTempText, r.GpuTemp, warn: 65, hot: 80, fire: 90);
        if (!string.IsNullOrEmpty(r.GpuName)) GpuTempText.ToolTip = r.GpuName;
    }

    private void ApplyRam(HwReading r)
    {
        double? load = r.RamLoad;
        RamLoadText.Text = load.HasValue ? $"{load:0}%" : "--%";
        if (r.RamTotalGb.HasValue)
            RamLoadText.ToolTip = r.RamTemp.HasValue
                ? $"{r.RamUsedGb:0.0} / {r.RamTotalGb:0.0} Go — {r.RamTemp:0}°C"
                : $"{r.RamUsedGb:0.0} / {r.RamTotalGb:0.0} Go";

        if (!load.HasValue)
        {
            StopFireIfActive(RamLoadText);
            RamLoadText.Foreground = Grey;
            RamLoadText.Effect = ShadowBlack;
            return;
        }
        // Meme echelle que temperatures/FPS : vert = ok, orange = charge, rouge = sature, feu = extreme.
        SetFireOrNormal(RamLoadText, load.Value >= 92, () =>
        {
            if (load < 70) { RamLoadText.Foreground = Green; RamLoadText.Effect = GlowGreen; }
            else if (load < 85) { RamLoadText.Foreground = Orange; RamLoadText.Effect = GlowOrange; }
            else { RamLoadText.Foreground = Red; RamLoadText.Effect = GlowRed; }
        });
    }

    private void ApplyFps()
    {
        if (_fps == null) { SetFps(null); return; }
        var h = GetForegroundWindow();
        GetWindowThreadProcessId(h, out uint pid);
        int usedPid = (int)pid;
        double? fps = _fps.GetFps(usedPid);

        // Repli : la fenetre au premier plan ne presente rien (jeu dont le rendu
        // est fait par un autre process, launcher, focus vole une frame...).
        // On prend alors le process qui presente le plus de frames = le jeu.
        // minFps=1 (au lieu de 5) : affiche aussi l'activite HORS JEU (bureau composé par le
        // DWM, navigateur, animations...) au lieu de rester bloque sur "—".
        if (!fps.HasValue || fps.Value < 1)
        {
            var busiest = _fps.GetBusiest(1);
            if (busiest.HasValue) { fps = busiest.Value.fps; usedPid = busiest.Value.pid; }
        }

        LogFpsSample(usedPid, fps);
        SetFps(fps);
    }

    /// <summary>
    /// Resolution de rendu de l'appli au premier plan (repli : celle de l'ecran).
    /// Code couleur par palier : 4K or, 1440p violet, 1080p vert, en dessous orange.
    /// </summary>
    private void ApplyRes()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var info = ResolutionService.Get(hwnd);
        if (!info.Ok)
        {
            ResText.Text = "—";
            ResText.Foreground = Grey;
            ResText.Effect = ShadowBlack;
            ResText.ToolTip = "Resolution indisponible";
            return;
        }

        // Definition standard : on affiche son nom commercial (« Full HD », « 4K »…),
        // plus lisible que les pixels. Les tailles batardes gardent les pixels exacts.
        string pixels = $"{info.Width}×{info.Height}";
        ResText.Text = ResolutionService.Label(info.Width, info.Height) ?? pixels;

        string tier = ResolutionService.Tier(info.Width, info.Height);
        // Meme logique que les autres valeurs : la couleur dit le palier d'un coup d'oeil.
        if (tier is "8K" or "4K") { ResText.Foreground = Gold; ResText.Effect = GlowGold; }
        else if (tier == "1440p") { ResText.Foreground = Violet; ResText.Effect = GlowViolet; }
        else if (tier == "1080p") { ResText.Foreground = Green; ResText.Effect = GlowGreen; }
        else { ResText.Foreground = Orange; ResText.Effect = GlowOrange; }

        string ecran = info.ScreenWidth > 0
            ? $"{info.ScreenWidth}×{info.ScreenHeight}" + (info.Hz > 0 ? $" @ {info.Hz} Hz" : "")
            : "inconnu";
        ResText.ToolTip = info.FromWindow
            ? $"{pixels} — rendu de l'appli au premier plan (ecran : {ecran})"
            : $"{pixels} — resolution de l'ecran ({ecran})";
    }

    private void ApplyHdr()
    {
        var s = HdrService.GetState();
        if (!s.Supported)
        {
            HdrText.Text = "N/A";
            HdrText.Foreground = Grey;
            HdrText.Effect = ShadowBlack;
        }
        else if (s.Enabled)
        {
            HdrText.Text = "ON"; // HDR actif = vert
            HdrText.Foreground = Green;
            HdrText.Effect = GlowGreen;
        }
        else
        {
            HdrText.Text = "OFF"; // HDR coupe = rouge
            HdrText.Foreground = Red;
            HdrText.Effect = GlowRed;
        }
    }

    /// <summary>Nom du profil DualForge applique a la manette (« — » si aucun).</summary>
    private void ApplyPad()
    {
        string? name = DualForgeService.ActiveProfile();
        if (string.IsNullOrEmpty(name))
        {
            PadText.Text = "—";
            PadText.Foreground = Grey;
            PadText.ToolTip = "DualForge : aucun profil applique";
            return;
        }
        // Un nom peut atteindre 60 caracteres cote DualForge : on tronque pour que
        // la largeur de l overlay ne saute pas, le nom complet va dans l infobulle.
        PadText.Text = name.Length > 16 ? name[..15] + "…" : name;
        PadText.Foreground = White;
        PadText.ToolTip = "Profil DualForge sur la manette : " + name;
    }

    private void LogFpsSample(int pid, double? fps)
    {
        if (_fpsLog == null) return;
        try
        {
            string name = SafeProcName(pid);
            bool seen = _fps!.HasPid(pid);
            _fpsLog.WriteLine($"{DateTime.Now:HH:mm:ss} | {pid,7} | {name,-20} | {(seen ? "oui" : "non"),-17} | {(fps.HasValue ? fps.Value.ToString("0.0") : "—")}");
        }
        catch { /* diagnostic seulement */ }
    }

    // Effet « en feu » (FPS >= 180, temperature/RAM extreme) : degrade or/orange/rouge anime + halo
    // vacillant. Suivi par controle (et non plus un seul champ) pour que CPU/GPU/RAM/FPS puissent
    // s'enflammer independamment les uns des autres.
    private readonly HashSet<TextBlock> _fireActive = new();

    /// <summary>Bascule un TextBlock entre son effet « feu » et ses couleurs normales selon `onFire`.</summary>
    private void SetFireOrNormal(TextBlock tb, bool onFire, Action applyNormalColors)
    {
        if (onFire)
        {
            if (!_fireActive.Contains(tb)) { ApplyFire(tb); _fireActive.Add(tb); }
            return; // on laisse l'animation de feu tourner
        }
        if (_fireActive.Contains(tb)) { StopFire(tb); _fireActive.Remove(tb); }
        applyNormalColors();
    }

    private void StopFireIfActive(TextBlock tb)
    {
        if (_fireActive.Contains(tb)) { StopFire(tb); _fireActive.Remove(tb); }
    }

    private void SetFps(double? fps)
    {
        if (fps.HasValue && fps.Value >= 1)
        {
            int v = (int)Math.Round(fps.Value);
            FpsText.Text = v.ToString();

            // >= 180 FPS : texte doré ENFLAMMÉ (flammes animées + halo qui vacille).
            SetFireOrNormal(FpsText, v >= 180, () =>
            {
                // Echelle : <=50 rouge, 51-75 orange, 76-120 vert, 121-179 violet.
                Brush brush; Effect glow;
                if (v <= 50)       { brush = Red;    glow = GlowRed; }
                else if (v <= 75)  { brush = Orange; glow = GlowOrange; }
                else if (v <= 120) { brush = Green;  glow = GlowGreen; }
                else               { brush = Violet; glow = GlowViolet; }
                FpsText.Foreground = brush;
                FpsText.Effect = glow;
            });
        }
        else
        {
            StopFireIfActive(FpsText);
            FpsText.Text = "—";
            FpsText.Foreground = Grey;
            FpsText.Effect = ShadowBlack;
        }
    }

    /// <summary>Applique un dégradé « flammes » animé + un halo vacillant sur le texte FPS.</summary>
    private void ApplyFire(TextBlock tb)
    {
        // Dégradé vertical : braise rouge (bas) → orange feu → or → or clair (pointe des flammes).
        var brush = new LinearGradientBrush { StartPoint = new Point(0.5, 1), EndPoint = new Point(0.5, 0) };
        var gRed = new GradientStop(Color.FromRgb(0xB4, 0x1E, 0x00), 0.0);
        var gOrange = new GradientStop(Color.FromRgb(0xFF, 0x5A, 0x00), 0.35);
        var gGold = new GradientStop(Color.FromRgb(0xFF, 0xC4, 0x2E), 0.70);
        var gLight = new GradientStop(Color.FromRgb(0xFF, 0xF3, 0xC0), 1.0);
        brush.GradientStops.Add(gRed);
        brush.GradientStops.Add(gOrange);
        brush.GradientStops.Add(gGold);
        brush.GradientStops.Add(gLight);
        tb.Foreground = brush;

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        void Dance(GradientStop s, double from, double to, int ms)
        {
            s.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = ease
            });
        }
        // Les couleurs chaudes montent/descendent → les flammes « lèchent » le texte.
        Dance(gOrange, 0.28, 0.44, 360);
        Dance(gGold, 0.60, 0.82, 500);

        // Halo qui vacille (rayon + couleur orange↔or), comme la lueur d'un feu.
        var glow = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x6A, 0x00), ShadowDepth = 0, BlurRadius = 12, Opacity = 0.9 };
        tb.Effect = glow;
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(9, 21, TimeSpan.FromMilliseconds(170))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        });
        glow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(Color.FromRgb(0xFF, 0x6A, 0x00), Color.FromRgb(0xFF, 0xC8, 0x2E), TimeSpan.FromMilliseconds(280))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    /// <summary>Arrête proprement les animations de feu (libère les storyboards).</summary>
    private static void StopFire(TextBlock tb)
    {
        if (tb.Foreground is LinearGradientBrush fireBrush)
        {
            foreach (var s in fireBrush.GradientStops) s.BeginAnimation(GradientStop.OffsetProperty, null);
        }
        if (tb.Effect is DropShadowEffect fireGlow)
        {
            fireGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            fireGlow.BeginAnimation(DropShadowEffect.ColorProperty, null);
        }
        tb.Effect = null;
    }

    private static string SafeProcName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return ""; }
    }

    // Feu tricolore + halo : vert = bon, orange = limite, rouge = trop chaud, "feu" = extreme (fire).
    private void SetTemp(TextBlock tb, double? t, double warn, double hot, double fire)
    {
        tb.Text = t.HasValue ? $"{t:0}°" : "--°";
        if (!t.HasValue) { StopFireIfActive(tb); tb.Foreground = Grey; tb.Effect = ShadowBlack; return; }
        SetFireOrNormal(tb, t.Value >= fire, () =>
        {
            if (t < warn) { tb.Foreground = Green; tb.Effect = GlowGreen; }
            else if (t < hot) { tb.Foreground = Orange; tb.Effect = GlowOrange; }
            else { tb.Foreground = Red; tb.Effect = GlowRed; }
        });
    }

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static Effect MakeGlow(string hex)
    {
        var e = new DropShadowEffect
        {
            Color = (Color)ColorConverter.ConvertFromString(hex),
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.7,
        };
        e.Freeze();
        return e;
    }

    private static Effect MakeShadow()
    {
        var e = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 5,
            ShadowDepth = 1,
            Opacity = 0.9,
        };
        e.Freeze();
        return e;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* si on clique un bouton */ }
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _fps?.Dispose();
        _hw?.Dispose();
        _fpsLog?.Dispose();
    }
}
