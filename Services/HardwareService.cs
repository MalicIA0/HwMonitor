using System;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace HwMonitor;

/// <summary>Un instantane des capteurs a un instant t.</summary>
public sealed class HwReading
{
    public double? CpuTemp;
    public double? CpuLoad;
    public double? GpuTemp;
    public double? GpuLoad;
    public double? RamLoad;
    public double? RamUsedGb;
    public double? RamTotalGb;
    public double? RamTemp;
    public string GpuName = "";
}

/// <summary>
/// Lecture des capteurs via LibreHardwareMonitorLib.
/// Necessite les droits administrateur (chargement d'un driver kernel).
/// </summary>
public sealed class HardwareService : IDisposable
{
    private readonly Computer _computer;
    private readonly object _lock = new();

    public HardwareService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
        };
        _computer.Open();
    }

    public HwReading Read()
    {
        var r = new HwReading();
        lock (_lock)
        {
            IHardware? nvidia = null;
            IHardware? otherGpu = null;
            IHardware? memTotal = null;
            IHardware? memVirtual = null;
            double? dimmTempMax = null;

            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();

                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        r.CpuTemp = CpuTemp(hw);
                        r.CpuLoad = Exact(hw, SensorType.Load, "CPU Total");
                        break;

                    case HardwareType.GpuNvidia:
                        nvidia = hw;
                        break;

                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        otherGpu ??= hw;
                        break;

                    case HardwareType.Memory:
                        // LibreHardwareMonitor expose plusieurs "Memory" distincts :
                        // "Virtual Memory" / "Total Memory" (usage systeme), et depuis
                        // que PawnIO lit le SMBus, une entree par barrette physique
                        // (ex: "Corsair - CMH32GX5M2B6000Z38") qui n'a que sa temperature.
                        // Il ne faut pas les fusionner naivement, sinon les barrettes
                        // (sans % d'usage) ecrasent les valeurs d'usage systeme.
                        if (hw.Name.Equals("Total Memory", StringComparison.OrdinalIgnoreCase))
                            memTotal = hw;
                        else if (hw.Name.Equals("Virtual Memory", StringComparison.OrdinalIgnoreCase))
                            memVirtual = hw;
                        else
                        {
                            double? t = DimmTemp(hw);
                            if (t.HasValue && (!dimmTempMax.HasValue || t > dimmTempMax.Value))
                                dimmTempMax = t;
                        }
                        break;
                }
            }

            // On privilegie le GPU dedie NVIDIA ; sinon l'iGPU (AMD/Intel).
            var gpu = nvidia ?? otherGpu;
            if (gpu != null)
            {
                r.GpuName = gpu.Name;
                r.GpuTemp = PosExact(gpu, "GPU Core") ?? PosMax(gpu, SensorType.Temperature);
                r.GpuLoad = Exact(gpu, SensorType.Load, "GPU Core") ?? Max(gpu, SensorType.Load);
            }

            // "Total Memory" = RAM physique reellement utilisee (correspond au Gestionnaire
            // des taches) ; "Virtual Memory" inclut le fichier d'echange et gonfle le total.
            var mem = memTotal ?? memVirtual;
            if (mem != null)
            {
                r.RamLoad = Exact(mem, SensorType.Load, "Memory");
                r.RamUsedGb = Exact(mem, SensorType.Data, "Memory Used");
                double? avail = Exact(mem, SensorType.Data, "Memory Available");
                if (r.RamUsedGb.HasValue && avail.HasValue)
                    r.RamTotalGb = r.RamUsedGb + avail;
            }
            r.RamTemp = dimmTempMax; // temperature max entre les barrettes (la plus chaude)
        }
        return r;
    }

    private static double? CpuTemp(IHardware hw)
    {
        // AMD Ryzen : "Core (Tctl/Tdie)". Intel : "CPU Package".
        // On ignore les valeurs a 0 : capteur present mais non alimente
        // (driver kernel non charge / bloque par Windows).
        return PosExact(hw, "Core (Tctl/Tdie)")
               ?? PosExact(hw, "Core (Tdie)")
               ?? PosExact(hw, "CPU Package")
               ?? PosExact(hw, "Core Average")
               ?? PosExact(hw, "Core Max")
               ?? PosMax(hw, SensorType.Temperature);
    }

    private static double? Exact(IHardware hw, SensorType type, string name)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == type
                && s.Value.HasValue
                && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                return s.Value.Value;
        }
        return null;
    }

    /// <summary>Comme Exact mais renvoie null si la valeur est &lt;= 0 (fausse lecture).</summary>
    private static double? PosExact(IHardware hw, string name)
    {
        var v = Exact(hw, SensorType.Temperature, name);
        return v is > 0 ? v : null;
    }

    private static double? Max(IHardware hw, SensorType type)
    {
        double? max = null;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != type || !s.Value.HasValue) continue;
            if (!max.HasValue || s.Value.Value > max.Value) max = s.Value.Value;
        }
        return max;
    }

    /// <summary>Max en ignorant les valeurs &lt;= 0.</summary>
    private static double? PosMax(IHardware hw, SensorType type)
    {
        double? max = null;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != type || !s.Value.HasValue || s.Value.Value <= 0) continue;
            if (!max.HasValue || s.Value.Value > max.Value) max = s.Value.Value;
        }
        return max;
    }

    /// <summary>
    /// Temperature d'une barrette RAM (capteur "DIMM #N" uniquement).
    /// On exclut expres les autres capteurs Temperature de ce materiel
    /// ("Thermal Sensor High/Critical Limit"...) qui sont des seuils
    /// constants, pas des lectures, et fausseraient un simple Max().
    /// </summary>
    private static double? DimmTemp(IHardware hw)
    {
        double? max = null;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || !s.Value.HasValue || s.Value.Value <= 0) continue;
            if (!s.Name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase)) continue;
            if (!max.HasValue || s.Value.Value > max.Value) max = s.Value.Value;
        }
        return max;
    }

    /// <summary>Dump complet (materiel + capteurs + valeurs) pour diagnostic.</summary>
    public string DumpAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== HwMonitor : dump capteurs ===");
        sb.AppendLine(DateTime.Now.ToString());
        lock (_lock)
        {
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
                sb.AppendLine();
                sb.AppendLine($"[{hw.HardwareType}] {hw.Name}");
                DumpSensors(sb, hw, "  ");
                foreach (var sub in hw.SubHardware)
                {
                    sb.AppendLine($"  <sous-materiel> [{sub.HardwareType}] {sub.Name}");
                    DumpSensors(sb, sub, "    ");
                }
            }
        }
        return sb.ToString();
    }

    private static void DumpSensors(StringBuilder sb, IHardware hw, string indent)
    {
        foreach (var s in hw.Sensors)
        {
            string val = s.Value.HasValue ? s.Value.Value.ToString("0.###") : "null";
            sb.AppendLine($"{indent}{s.SensorType,-12} | {s.Name,-30} | {val}");
        }
    }

    public void Dispose()
    {
        try { _computer.Close(); } catch { /* rien */ }
    }
}
