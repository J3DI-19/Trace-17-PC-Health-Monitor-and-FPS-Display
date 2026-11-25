using System;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;

namespace Trace17 {
    static class Verification {
        static string Xml(DateTimeOffset time, int id, string bus = "0x0", string dev = "0x6", string fn = "0x1",
            string vid = "0x8086", string did = "0x7eca") {
            return "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>" +
                "<Provider Name='Microsoft-Windows-WHEA-Logger'/><EventID>17</EventID><Channel>System</Channel>" +
                "<EventRecordID>" + id + "</EventRecordID><TimeCreated SystemTime='" +
                time.ToString("o", CultureInfo.InvariantCulture) + "'/></System><EventData>" +
                "<Data Name='Bus'>" + bus + "</Data><Data Name='Device'>" + dev + "</Data><Data Name='Function'>" + fn + "</Data>" +
                "<Data Name='VendorID'>" + vid + "</Data><Data Name='DeviceID'>" + did + "</Data></EventData></Event>";
        }

        public static WheaEvent Sample(DateTimeOffset time, int id) { return WheaEvent.Parse(Xml(time, id)); }

        static void Check(bool passed, string test, TextWriter output) {
            if (!passed) throw new InvalidOperationException(test);
            output.WriteLine("PASS " + test);
        }

        public static int Run(string mode) {
            string file = Path.Combine(Program.Home, "logs", mode == "--verify-live" ? "verification-live.txt" : "verification-synthetic.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (var output = File.CreateText(file)) {
                output.WriteLine(DateTimeOffset.Now.ToString("o"));
                try {
                    if (mode == "--verify-live") {
                        var initial = new SystemLog().Poll();
                        Check(initial.Failure == null, "System log poll " + initial.Failure, output);
                        Check(initial.Initialized, "Initial event cursor established", output);
                        var resolver = new PciDeviceResolver();
                        using (var reader = SystemLog.Reader("*[System[" + WheaEvent.Filter + "]]", true)) {
                            EventRecord item;
                            bool found = false;
                            for (int i = 0; i < 100 && (item = reader.ReadEvent()) != null; i++) {
                                using (item) {
                                    var match = WheaEvent.Parse(item.ToXml());
                                    if (match == null) continue;
                                    output.WriteLine("Historical match only. Record " + match.Id + " [" + resolver.Resolve(match) + "] at " + match.Time.ToLocalTime().ToString("o"));
                                    found = true;
                                    break;
                                }
                            }
                            Check(found, "Read and matched a historical WHEA 17 event without notifying", output);
                        }

                        using (var collector = new TelemetryCollector()) {
                            collector.Sample();
                            System.Threading.Thread.Sleep(550);
                            var snap = collector.Sample();
                            Check(snap.RamUsagePercent.HasValue && snap.RamTotalGb > 0, "Live RAM usage sampled (" +
                                string.Format(CultureInfo.InvariantCulture, "{0:F0}% of {1:F1} GB", snap.RamUsagePercent ?? 0, snap.RamTotalGb) + ")", output);
                            Check(snap.Drives.Count > 0, "Live fixed drives enumerated (" + snap.Drives.Count + ")", output);
                            foreach (var dr in snap.Drives) {
                                output.WriteLine("Drive " + dr.Name + " temp=" + (dr.TempC.HasValue ? dr.TempC.Value.ToString("F0", CultureInfo.InvariantCulture) + "C" : "N/A") +
                                    " active=" + (dr.ActivePercent.HasValue ? dr.ActivePercent.Value.ToString("F1", CultureInfo.InvariantCulture) + "%" : "N/A"));
                            }
                            Check(snap.CpuUsagePercent.HasValue, "Live CPU usage sampled (" +
                                string.Format(CultureInfo.InvariantCulture, "{0:F1}%", snap.CpuUsagePercent ?? 0) + ")", output);
                            output.WriteLine("GPU (" + snap.GpuName + ") presentMon=" + snap.PresentMonConnected +
                                " temp=" + (snap.GpuTempC.HasValue ? snap.GpuTempC.Value.ToString("F1", CultureInfo.InvariantCulture) + "C" : "N/A") +
                                " util=" + (snap.GpuUsagePercent.HasValue ? snap.GpuUsagePercent.Value.ToString("F1", CultureInfo.InvariantCulture) + "%" : "N/A"));
                            output.WriteLine("CPU temp=" + (snap.CpuTempC.HasValue ? snap.CpuTempC.Value.ToString("F1", CultureInfo.InvariantCulture) + "C (" + snap.CpuTempSource + ")" : "N/A"));
                        }

                        // Verify PCIe device attribution across C:, D:, root ports, and GPU
                        string cNvme = resolver.ResolveBdf(1, 0, 0, 0x15B7, 0x5045);
                        string dNvme = resolver.ResolveBdf(5, 0, 0, 0x15B7, 0x5045);
                        string cRoot = resolver.ResolveBdf(0, 1, 0, 0x8086, 0x7ECC);
                        string dRoot = resolver.ResolveBdf(0, 6, 1, 0x8086, 0x7ECA);
                        string gpuDev = resolver.ResolveBdf(4, 0, 0, 0x1002, 0x7550);
                        output.WriteLine("PCIe Map C: NVMe -> " + cNvme);
                        output.WriteLine("PCIe Map D: NVMe -> " + dNvme);
                        output.WriteLine("PCIe Map C: Root -> " + cRoot);
                        output.WriteLine("PCIe Map D: Root -> " + dRoot);
                        output.WriteLine("PCIe Map GPU     -> " + gpuDev);
                        Check(cNvme.Contains("C:") && dNvme.Contains("D:") && cRoot.Contains("C:") && dRoot.Contains("D:") && gpuDev.Contains("9070"),
                            "Resolved C:, D:, root ports, and GPU PCIe paths", output);
                    } else {
                        var now = DateTimeOffset.UtcNow;
                        string xml = Xml(now, 123);
                        Check(Sample(now, 123).Id == 123 && Sample(now, 123).Time == now, "Exact match and original timestamp", output);

                        // All-device WHEA 17 support (C: drive, GPU, other PCIe devices accepted; non-WHEA-17 rejected)
                        var otherDrive = WheaEvent.Parse(Xml(now, 124, "0x1", "0x0", "0x0", "0x15b7", "0x5045"));
                        Check(otherDrive != null && otherDrive.DeviceKey == "1:0:0/15B7:5045", "Accepted C: NVMe device WHEA 17", output);
                        var gpuEv = WheaEvent.Parse(Xml(now, 125, "0x4", "0x0", "0x0", "0x1002", "0x7550"));
                        Check(gpuEv != null && gpuEv.DeviceKey == "4:0:0/1002:7550", "Accepted GPU PCIe device WHEA 17", output);

                        foreach (var change in new[] {
                            new[] { "EventID>17", "EventID>18" },
                            new[] { "WHEA-Logger", "Other-Logger" },
                            new[] { "System</Channel", "Application</Channel" }
                        }) Check(WheaEvent.Parse(xml.Replace(change[0], change[1])) == null, "Rejected " + change[1], output);

                        var burst = new Burst();
                        Check(burst.Add(now.AddSeconds(-61), now) == 0, "Historical catch-up does not create a live burst", output);
                        Check(burst.Add(now, now) == 1, "First WHEA 17 warning", output);
                        for (int i = 2; i <= 12; i++)
                            Check(burst.Add(now.AddSeconds(i), now.AddSeconds(i)) == (i == 10 ? 2 : 0), "Burst notification at count " + i, output);
                        Check(burst.Add(now.AddSeconds(73), now.AddSeconds(73)) == 1, "New warning after quiet period", output);

                        // Storage temperature descriptor parsing
                        byte[] nvmeDesc = new byte[48];
                        BitConverter.GetBytes((short)90).CopyTo(nvmeDesc, 10);
                        BitConverter.GetBytes((ushort)3).CopyTo(nvmeDesc, 12);
                        BitConverter.GetBytes((short)38).CopyTo(nvmeDesc, 26);
                        double t, w;
                        Check(HardwareSensors.ParseStorageTemperatureDescriptor(nvmeDesc, 48, out t, out w) && t == 38 && w == 90,
                            "Parsed NVMe storage temperature descriptor", output);
                        nvmeDesc[12] = 0;
                        Check(!HardwareSensors.ParseStorageTemperatureDescriptor(nvmeDesc, 48, out t, out w),
                            "Rejected empty NVMe storage temperature descriptor", output);

                        // CPU sensor label scoring
                        Check(HardwareSensors.ScoreCpuTempLabel("CPU [#0]: Intel Core Ultra 7 270K", "CPU Package", "CPU Package") >= 100,
                            "Matched CPU Package temperature label", output);
                        Check(HardwareSensors.ScoreCpuTempLabel("GPU [#0]: AMD Radeon RX 9070 XT", "GPU Temperature", "GPU Temperature") == 0,
                            "Rejected GPU label from CPU temperature selection", output);

                        // Registry LocationInformation parsing
                        Check(PciDeviceResolver.ParseBdfFromLocation(@"@System32\drivers\pci.sys,#65536;PCI bus %1, device %2, function %3;(5,0,0)") == "5:0:0",
                            "Parsed PCI registry resource BDF string", output);
                    }
                    return 0;
                } catch (Exception error) {
                    output.WriteLine("FAIL " + error);
                    return 1;
                }
            }
        }
    }
}
