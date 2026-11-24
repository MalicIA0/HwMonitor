using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace HwMonitor;

/// <summary>
/// Mesure des FPS en temps reel via PresentMon (Intel, ETW).
/// On capture tous les process, on compte les presentations (frames)
/// de la derniere seconde pour un PID donne.
/// Necessite les droits administrateur (session ETW temps reel).
/// FPS = toutes les frames affichees (rendues + generees par Frame Generation le cas echeant).
/// </summary>
public sealed class FpsService : IDisposable
{
    private const double WindowSeconds = 1.0;

    private Process? _proc;
    private readonly object _lock = new();
    private readonly Dictionary<int, Queue<double>> _presents = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _idxPid = -1;
    private volatile bool _disposed;
    private string _sessionName = "";

    /// <summary>Compte-rendu du menage des sessions ETW au demarrage (repris dans le diagnostic FPS).</summary>
    public static string CleanupLog { get; private set; } = "";

    public bool Started { get; private set; }
    public string LastError { get; private set; } = "";
    public int PidCount { get { lock (_lock) { return _presents.Count; } } }

    /// <summary>Code de sortie si PresentMon s'est arrete tout seul (null = toujours vivant ou jamais lance).</summary>
    public int? ExitCode { get; private set; }

    public void Start(string presentMonPath)
    {
        if (!File.Exists(presentMonPath))
        {
            LastError = "PresentMon.exe introuvable : " + presentMonPath;
            return;
        }

        // Nettoyage des restes d'anciennes instances (overlay tue brutalement) : un PresentMon
        // orphelin ou une session ETW "HwMonitor*" orpheline capterait les evenements a notre
        // place → le FPS resterait bloque a 0. On tourne en admin, donc on peut les arreter.
        CleanupOrphans();
        _sessionName = "HwMonitor_" + Environment.ProcessId;

        var psi = new ProcessStartInfo
        {
            FileName = presentMonPath,
            // Nom de session ETW UNIQUE par lancement (PID) : une session « HwMonitor » figée,
            // laissée orpheline par un précédent PresentMon mal fermé, faisait échouer le
            // démarrage (« Use --session_name with a different name ») → PidCount=0, aucun FPS.
            // Un nom unique élimine toute collision. --stop_existing_session couvre le PID réutilisé.
            Arguments = $"--output_stdout --v1_metrics --no_console_stats --session_name {_sessionName} --stop_existing_session",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            _proc = Process.Start(psi);
            if (_proc == null) { LastError = "echec du demarrage de PresentMon"; return; }
            Started = true;
            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) =>
            {
                try { ExitCode = _proc.ExitCode; } catch { /* deja dispose */ }
            };
            _ = Task.Run(() => ReadLoop(_proc.StandardOutput));
            _ = Task.Run(() => ReadErr(_proc.StandardError));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Tue les PresentMon orphelins et arrete les sessions ETW "HwMonitor*" restantes.</summary>
    private static void CleanupOrphans()
    {
        // 1) PresentMon d'anciennes instances (le notre n'est pas encore lance).
        foreach (var p in Process.GetProcessesByName("PresentMon"))
        {
            try { p.Kill(); p.WaitForExit(1500); } catch { /* deja mort / inaccessible */ }
        }
        // 2) Sessions ETW temps-reel "HwMonitor*" encore actives (sans consommateur = orphelines).
        //    Une seule survivante suffit a affamer notre nouvelle session : PresentMon demarre
        //    sans erreur mais ne remonte aucune frame (PidCount=0, FPS bloque sur "—").
        //    Le menage est pilote ici, en C# : la variante "cmd /c for /f ... logman stop"
        //    pouvait echouer sans que rien ne le signale.
        var log = new System.Text.StringBuilder();
        foreach (var name in ListEtwSessions("HwMonitor"))
        {
            string res = RunLogman("stop " + name + " -ets");
            log.Append(name).Append(" -> ").Append(res).Append('\n');
        }
        CleanupLog = log.Length > 0 ? log.ToString().TrimEnd() : "(aucune session orpheline)";
    }

    /// <summary>Noms des sessions ETW en cours dont le nom commence par <paramref name="prefix"/>.</summary>
    private static List<string> ListEtwSessions(string prefix)
    {
        var found = new List<string>();
        string output = RunLogman("query -ets");
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
            found.Add(name);
        }
        return found;
    }

    /// <summary>Lance logman.exe et rend sa sortie (vide si l'appel echoue).</summary>
    private static string RunLogman(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "logman.exe",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            string outp = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            return outp.Trim();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private void ReadErr(StreamReader r)
    {
        try
        {
            string? line;
            while ((line = r.ReadLine()) != null)
                if (line.Length > 0) LastError = line.Trim();
        }
        catch { /* flux ferme */ }
    }

    private void ReadLoop(StreamReader r)
    {
        try
        {
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (_disposed) break;
                if (_idxPid < 0)
                {
                    if (line.Contains("ProcessID", StringComparison.OrdinalIgnoreCase))
                        ParseHeader(line);
                    continue;
                }
                ParseRow(line);
            }
        }
        catch { /* flux ferme */ }
    }

    private void ParseHeader(string header)
    {
        var cols = header.Split(',');
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i].Trim().Equals("ProcessID", StringComparison.OrdinalIgnoreCase)) { _idxPid = i; break; }
        }
    }

    private void ParseRow(string line)
    {
        var cols = line.Split(',');
        if (_idxPid < 0 || _idxPid >= cols.Length) return;
        if (!int.TryParse(cols[_idxPid], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            return;

        double t = _clock.Elapsed.TotalSeconds;

        lock (_lock)
        {
            if (!_presents.TryGetValue(pid, out var q))
            {
                q = new Queue<double>();
                _presents[pid] = q;
            }
            q.Enqueue(t);
            double cutoff = t - WindowSeconds;
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
        }
    }

    /// <summary>Vrai si PresentMon a deja vu au moins une frame de ce PID (meme hors fenetre de calcul).</summary>
    public bool HasPid(int pid)
    {
        lock (_lock) { return _presents.ContainsKey(pid); }
    }

    /// <summary>FPS (toutes frames affichees) du process indique, ou null s'il ne presente rien.</summary>
    public double? GetFps(int pid)
    {
        lock (_lock)
        {
            if (!_presents.TryGetValue(pid, out var q)) return null;
            double cutoff = _clock.Elapsed.TotalSeconds - WindowSeconds;
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            if (q.Count == 0) return null;
            return q.Count / WindowSeconds;
        }
    }

    /// <summary>
    /// Process qui presente le plus de frames en ce moment (= le jeu, en general),
    /// ou null si rien ne depasse <paramref name="minFps"/>. Sert de repli quand la
    /// fenetre au premier plan n'est pas le process qui fait le rendu.
    /// </summary>
    public (int pid, double fps)? GetBusiest(double minFps = 5)
    {
        lock (_lock)
        {
            int bestPid = -1;
            double best = 0;
            double cutoff = _clock.Elapsed.TotalSeconds - WindowSeconds;
            foreach (var kv in _presents)
            {
                var q = kv.Value;
                while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
                double f = q.Count / WindowSeconds;
                if (f > best) { best = f; bestPid = kv.Key; }
            }
            if (bestPid < 0 || best < minFps) return null;
            return (bestPid, best);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { /* deja mort */ }
        try { _proc?.Dispose(); } catch { /* rien */ }
        // Tuer PresentMon ne ferme pas sa session ETW : sans ca, chaque fermeture de
        // l'overlay laissait une session orpheline qui cassait le FPS au lancement suivant.
        if (_sessionName.Length > 0) RunLogman("stop " + _sessionName + " -ets");
    }
}
