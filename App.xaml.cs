using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;

namespace HwMonitor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Mode diagnostic (sans fenetre) : HwMonitor.exe --selftest
        if (Array.Exists(e.Args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    private static void RunSelfTest()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== HwMonitor selftest ===");
        sb.AppendLine("Time: " + DateTime.Now);

        try
        {
            using var hw = new HardwareService();
            var r = hw.Read();
            sb.AppendLine($"CPU  temp={fmt(r.CpuTemp)}  load={fmt(r.CpuLoad)}");
            sb.AppendLine($"GPU  [{r.GpuName}]  temp={fmt(r.GpuTemp)}  load={fmt(r.GpuLoad)}");
            sb.AppendLine($"RAM  load={fmt(r.RamLoad)}  used={fmt(r.RamUsedGb)}Go  total={fmt(r.RamTotalGb)}Go  temp={fmt(r.RamTemp)}");
        }
        catch (Exception ex) { sb.AppendLine("HW ERROR: " + ex); }

        try
        {
            using var fps = new FpsService();
            fps.Start(Path.Combine(AppContext.BaseDirectory, "PresentMon.exe"));
            Thread.Sleep(3500);
            sb.AppendLine($"FPS  started={fps.Started}  pidsSeen={fps.PidCount}  lastErr=\"{fps.LastError}\"");
        }
        catch (Exception ex) { sb.AppendLine("FPS ERROR: " + ex); }

        var outPath = Path.Combine(Path.GetTempPath(), "hwmonitor_selftest.txt");
        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
    }

    private static string fmt(double? v) => v.HasValue ? v.Value.ToString("0.0") : "null";
}
