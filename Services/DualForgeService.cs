using System;
using System.IO;
using System.Text.Json;

namespace HwMonitor;

/// <summary>
/// Profil DualSense actuellement appliqué par DualForge. L'appli écrit son
/// magasin de profils dans %APPDATA%\dualforge\dualforge-profiles.json (écriture
/// atomique : fichier temporaire + renommage) ; on se contente de le relire.
/// Aucun couplage : si DualForge n'est pas installé, le fichier n'existe pas et
/// l'overlay affiche « — ».
/// </summary>
public static class DualForgeService
{
    private static readonly string StoreFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "dualforge", "dualforge-profiles.json");

    // Cache : le fichier ne bouge qu'au changement de profil, inutile de le
    // reparser chaque seconde. Invalidé sur la date d'écriture + la taille.
    private static DateTime _stamp;
    private static long _size = -1;
    private static string? _cached;

    /// <summary>
    /// Nom du profil appliqué à la manette, ou null (DualForge absent, jamais
    /// lancé, ou aucun profil appliqué).
    /// </summary>
    public static string? ActiveProfile()
    {
        try
        {
            var info = new FileInfo(StoreFile);
            if (!info.Exists)
            {
                _stamp = default; _size = -1; _cached = null;
                return null;
            }
            if (info.LastWriteTimeUtc == _stamp && info.Length == _size) return _cached;
            _stamp = info.LastWriteTimeUtc;
            _size = info.Length;
            _cached = Read();
            return _cached;
        }
        catch
        {
            // Fichier verrouillé une fraction de seconde pendant l'écriture :
            // on garde la derniere valeur connue plutot que de clignoter.
            return _cached;
        }
    }

    private static string? Read()
    {
        try
        {
            // ReadAllText retire le BOM UTF-8 s'il y en a un (JsonDocument, lui,
            // le refuserait) — DualForge tolère aussi un fichier édité à la main.
            using var doc = JsonDocument.Parse(File.ReadAllText(StoreFile));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("activeId", out var active) || active.ValueKind != JsonValueKind.String)
                return null;
            string id = active.GetString() ?? "";
            if (id.Length == 0) return null;

            if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var p in profiles.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                if (!p.TryGetProperty("id", out var pid) || pid.ValueKind != JsonValueKind.String) continue;
                if (pid.GetString() != id) continue;
                var name = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()?.Trim()
                    : null;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            return null; // id qui ne correspond plus à aucun profil
        }
        catch { return null; }
    }
}
