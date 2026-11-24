using System;
using System.Runtime.InteropServices;

namespace HwMonitor;

/// <summary>
/// Lecture et bascule du HDR (advanced color) de Windows via l'API CCD
/// (Connecting and Configuring Displays). Aucun droit admin requis pour changer
/// le HDR. On agit sur tous les écrans qui supportent le HDR.
/// </summary>
public static class HdrService
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const int GET_ADVANCED_COLOR_INFO = 9;
    private const int SET_ADVANCED_COLOR_STATE = 10;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct RATIONAL { public uint Num; public uint Den; }

    // ⚠ PATH_SOURCE contient un statusFlags final (20 octets, pas 16) : sans lui tout le
    // tableau est décalé de 4 octets et DisplayConfigGetDeviceInfo échoue (ERROR 87).
    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET
    {
        public LUID adapterId; public uint id; public uint modeIdx; public uint outTech;
        public uint rotation; public uint scaling; public RATIONAL refresh; public uint scanLine;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE src; public PATH_TARGET tgt; public uint flags; }

    // Union DISPLAYCONFIG_MODE_INFO : le plus grand membre (target mode) fait 48 octets.
    [StructLayout(LayoutKind.Sequential, Size = 48)] private struct MODE_UNION { }
    [StructLayout(LayoutKind.Sequential)]
    private struct MODE_INFO { public uint infoType; public uint id; public LUID adapterId; public MODE_UNION mode; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INFO_HEADER { public int type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GET_ADV { public INFO_HEADER header; public uint value; public int colorEncoding; public int bpc; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SET_ADV { public INFO_HEADER header; public uint value; }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint nPath, out uint nMode);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint nPath, [Out] PATH_INFO[] paths, ref uint nMode, [Out] MODE_INFO[] modes, IntPtr topo);
    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref GET_ADV packet);
    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref SET_ADV packet);

    /// <summary>État HDR : Supported = au moins un écran gère le HDR ; Enabled = au moins un l'a activé.</summary>
    public readonly record struct HdrState(bool Supported, bool Enabled);

    private static PATH_INFO[]? ActivePaths()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint nPath, out uint nMode) != 0) return null;
        var paths = new PATH_INFO[nPath];
        var modes = new MODE_INFO[nMode];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPath, paths, ref nMode, modes, IntPtr.Zero) != 0) return null;
        Array.Resize(ref paths, (int)nPath);
        return paths;
    }

    public static HdrState GetState()
    {
        try
        {
            var paths = ActivePaths();
            if (paths == null) return new HdrState(false, false);
            bool supported = false, enabled = false;
            foreach (var p in paths)
            {
                var g = new GET_ADV();
                g.header.type = GET_ADVANCED_COLOR_INFO;
                g.header.size = (uint)Marshal.SizeOf<GET_ADV>();
                g.header.adapterId = p.tgt.adapterId;
                g.header.id = p.tgt.id;
                if (DisplayConfigGetDeviceInfo(ref g) != 0) continue;
                if ((g.value & 0x1) != 0) supported = true;      // advancedColorSupported
                if ((g.value & 0x2) != 0) enabled = true;        // advancedColorEnabled
            }
            return new HdrState(supported, enabled);
        }
        catch { return new HdrState(false, false); }
    }

    /// <summary>Active/désactive le HDR sur tous les écrans qui le supportent. Retourne true si au moins un a changé.</summary>
    public static bool SetHdr(bool enable)
    {
        try
        {
            var paths = ActivePaths();
            if (paths == null) return false;
            bool any = false;
            foreach (var p in paths)
            {
                var g = new GET_ADV();
                g.header.type = GET_ADVANCED_COLOR_INFO;
                g.header.size = (uint)Marshal.SizeOf<GET_ADV>();
                g.header.adapterId = p.tgt.adapterId;
                g.header.id = p.tgt.id;
                if (DisplayConfigGetDeviceInfo(ref g) != 0) continue;
                if ((g.value & 0x1) == 0) continue; // écran sans support HDR : on l'ignore

                var s = new SET_ADV();
                s.header.type = SET_ADVANCED_COLOR_STATE;
                s.header.size = (uint)Marshal.SizeOf<SET_ADV>();
                s.header.adapterId = p.tgt.adapterId;
                s.header.id = p.tgt.id;
                s.value = enable ? 1u : 0u; // bit0 = enableAdvancedColor
                if (DisplayConfigSetDeviceInfo(ref s) == 0) any = true;
            }
            return any;
        }
        catch { return false; }
    }

    /// <summary>Bascule le HDR (on↔off) selon l'état courant. Retourne le nouvel état voulu.</summary>
    public static bool Toggle()
    {
        bool target = !GetState().Enabled;
        SetHdr(target);
        return target;
    }
}
