using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Trace17 {
    sealed class WheaEvent {
        const string Provider = "Microsoft-Windows-WHEA-Logger";
        public const string Filter = "Provider[@Name='" + Provider + "'] and EventID=17";
        static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

        public long Id;
        public DateTimeOffset Time;
        public int? Bus;
        public int? Device;
        public int? Function;
        public int? VendorId;
        public int? DeviceId;
        public string PrimaryDeviceName;
        public string CorrectableStatus;
        public string UncorrectableStatus;
        public string DeviceKey;
        public string FriendlyDevice;

        public static WheaEvent Parse(string xml) {
            var doc = XElement.Parse(xml);
            var system = doc.Element(Ns + "System");
            var data = doc.Element(Ns + "EventData");
            if (doc.Name != Ns + "Event" || system == null || data == null ||
                (string)system.Element(Ns + "Channel") != "System" ||
                (string)system.Element(Ns + "EventID") != "17" ||
                (string)system.Element(Ns + "Provider").Attribute("Name") != Provider)
                return null;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.Elements(Ns + "Data")) {
                var name = (string)item.Attribute("Name");
                if (name != null) fields[name] = (item.Value ?? "").Trim();
            }

            int bus, dev, fn, vid, did;
            int? b = TryParseInt(fields, "Bus", out bus) ? (int?)bus : null;
            int? d = TryParseInt(fields, "Device", out dev) ? (int?)dev : null;
            int? f = TryParseInt(fields, "Function", out fn) ? (int?)fn : null;
            int? v = TryParseInt(fields, "VendorID", out vid) ? (int?)vid : null;
            int? p = TryParseInt(fields, "DeviceID", out did) ? (int?)did : null;

            string primary;
            fields.TryGetValue("PrimaryDeviceName", out primary);
            string corr, uncorr;
            fields.TryGetValue("CorrectableErrorStatus", out corr);
            fields.TryGetValue("UncorrectableErrorStatus", out uncorr);

            string bdf = (b.HasValue && d.HasValue && f.HasValue)
                ? b.Value + ":" + d.Value + ":" + f.Value
                : "PCIe";
            string ids = (v.HasValue && p.HasValue)
                ? v.Value.ToString("X4", CultureInfo.InvariantCulture) + ":" + p.Value.ToString("X4", CultureInfo.InvariantCulture)
                : "";
            string key = ids.Length > 0 ? bdf + "/" + ids : (!string.IsNullOrEmpty(primary) ? primary : bdf);

            return new WheaEvent {
                Id = (long)system.Element(Ns + "EventRecordID"),
                Time = DateTimeOffset.Parse((string)system.Element(Ns + "TimeCreated").Attribute("SystemTime"),
                    CultureInfo.InvariantCulture),
                Bus = b,
                Device = d,
                Function = f,
                VendorId = v,
                DeviceId = p,
                PrimaryDeviceName = primary ?? "",
                CorrectableStatus = corr ?? "",
                UncorrectableStatus = uncorr ?? "",
                DeviceKey = key,
                FriendlyDevice = ids.Length > 0 ? bdf + " (" + ids + ")" : key
            };
        }

        static bool TryParseInt(Dictionary<string, string> fields, string field, out int value) {
            value = 0;
            string input;
            if (!fields.TryGetValue(field, out input) || string.IsNullOrEmpty(input)) return false;
            input = input.Trim();
            return input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(input.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                : int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    sealed class PciDeviceResolver {
        readonly Dictionary<string, string> byBdf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> byVidDid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();
        bool loaded;

        public void EnsureLoaded() {
            lock (gate) {
                if (loaded) return;
                loaded = true;
                try {
                    BuildMap();
                } catch { }
            }
        }

        public string Resolve(WheaEvent ev) {
            if (ev == null) return "Unknown PCIe";
            EnsureLoaded();
            lock (gate) {
                string bdf = (ev.Bus.HasValue && ev.Device.HasValue && ev.Function.HasValue)
                    ? ev.Bus.Value + ":" + ev.Device.Value + ":" + ev.Function.Value
                    : "";
                string vd = (ev.VendorId.HasValue && ev.DeviceId.HasValue)
                    ? ev.VendorId.Value.ToString("X4", CultureInfo.InvariantCulture) + ":" +
                      ev.DeviceId.Value.ToString("X4", CultureInfo.InvariantCulture)
                    : "";
                string label;
                if (bdf.Length > 0 && byBdf.TryGetValue(bdf, out label))
                    return label + " (" + bdf + (vd.Length > 0 ? " · " + vd : "") + ")";
                if (vd.Length > 0 && byVidDid.TryGetValue(vd, out label))
                    return label + " (" + (bdf.Length > 0 ? bdf + " · " : "") + vd + ")";
                if (!string.IsNullOrEmpty(ev.PrimaryDeviceName))
                    return ev.PrimaryDeviceName + (bdf.Length > 0 ? " (" + bdf + ")" : "");
                return ev.FriendlyDevice;
            }
        }

        public string ResolveBdf(int bus, int device, int function, int vendorId, int deviceId) {
            return Resolve(new WheaEvent {
                Bus = bus, Device = device, Function = function,
                VendorId = vendorId, DeviceId = deviceId,
                FriendlyDevice = bus + ":" + device + ":" + function
            });
        }

        void BuildMap() {
            // 1. Map drive letters (e.g. C:, D:) -> DiskDrive -> model & PNPDeviceID.
            var diskToLetters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            using (var partSearcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition")) {
                var partToLetters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (ManagementObject rel in partSearcher.Get()) {
                    string ant = ExtractQuoted(Convert.ToString(rel["Antecedent"]));
                    string dep = ExtractQuoted(Convert.ToString(rel["Dependent"]));
                    if (string.IsNullOrEmpty(ant) || string.IsNullOrEmpty(dep)) continue;
                    List<string> list;
                    if (!partToLetters.TryGetValue(ant, out list)) {
                        list = new List<string>();
                        partToLetters[ant] = list;
                    }
                    if (!list.Contains(dep)) list.Add(dep);
                }

                using (var diskPartSearcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition")) {
                    foreach (ManagementObject rel in diskPartSearcher.Get()) {
                        string disk = ExtractQuoted(Convert.ToString(rel["Antecedent"]));
                        string part = ExtractQuoted(Convert.ToString(rel["Dependent"]));
                        List<string> letters;
                        if (string.IsNullOrEmpty(disk) || string.IsNullOrEmpty(part) || !partToLetters.TryGetValue(part, out letters))
                            continue;
                        List<string> dLetters;
                        if (!diskToLetters.TryGetValue(disk, out dLetters)) {
                            dLetters = new List<string>();
                            diskToLetters[disk] = dLetters;
                        }
                        foreach (string l in letters)
                            if (!dLetters.Contains(l)) dLetters.Add(l);
                    }
                }
            }

            // 2. Collect friendly Device Manager names from Win32_PnPEntity for all PCI devices.
            var pnpNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var pnp = new ManagementObjectSearcher("SELECT PNPDeviceID, Name FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI\\\\VEN_%'")) {
                foreach (ManagementObject obj in pnp.Get()) {
                    string id = Convert.ToString(obj["PNPDeviceID"]) ?? "";
                    string name = Convert.ToString(obj["Name"]) ?? "";
                    if (id.Length > 0 && name.Length > 0) pnpNameMap[id] = name;
                }
            }

            // 3. Join with Registry Enum\PCI LocationInformation to get exact Bus:Device:Function -> Name.
            var pnpBdfMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var pciKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI")) {
                if (pciKey != null) {
                    foreach (string venDev in pciKey.GetSubKeyNames()) {
                        using (var vdKey = pciKey.OpenSubKey(venDev)) {
                            if (vdKey == null) continue;
                            string vid = ExtractHexPart(venDev, "VEN_");
                            string did = ExtractHexPart(venDev, "DEV_");
                            string vdPair = (vid.Length == 4 && did.Length == 4) ? vid + ":" + did : "";
                            foreach (string inst in vdKey.GetSubKeyNames()) {
                                using (var instKey = vdKey.OpenSubKey(inst)) {
                                    if (instKey == null) continue;
                                    string loc = Convert.ToString(instKey.GetValue("LocationInformation")) ?? "";
                                    string fullId = @"PCI\" + venDev + @"\" + inst;
                                    string desc;
                                    if (!pnpNameMap.TryGetValue(fullId, out desc) || string.IsNullOrEmpty(desc)) {
                                        desc = Convert.ToString(instKey.GetValue("FriendlyName")) ??
                                               Convert.ToString(instKey.GetValue("DeviceDesc")) ?? "";
                                        int semi = desc.LastIndexOf(';');
                                        if (semi >= 0 && semi + 1 < desc.Length) desc = desc.Substring(semi + 1);
                                    }
                                    desc = ShortenDeviceName(desc);

                                    string bdf = ParseBdfFromLocation(loc);
                                    if (!string.IsNullOrEmpty(bdf)) {
                                        pnpBdfMap[fullId] = bdf;
                                        if (desc.Length > 0 && !byBdf.ContainsKey(bdf))
                                            byBdf[bdf] = desc;
                                    }
                                    if (!string.IsNullOrEmpty(vdPair) && desc.Length > 0 && !byVidDid.ContainsKey(vdPair))
                                        byVidDid[vdPair] = desc;
                                }
                            }
                        }
                    }
                }
            }

            // 4. Correlate physical disk drives (C:, D:, etc.) to their NVMe PCIe controller & Bus-0 root port.
            using (var diskSearcher = new ManagementObjectSearcher("SELECT DeviceID, Model, PNPDeviceID FROM Win32_DiskDrive")) {
                foreach (ManagementObject disk in diskSearcher.Get()) {
                    string devId = Convert.ToString(disk["DeviceID"]) ?? "";
                    string model = ShortenDeviceName(Convert.ToString(disk["Model"]) ?? "NVMe SSD");
                    string pnpId = Convert.ToString(disk["PNPDeviceID"]) ?? "";
                    List<string> letters;
                    if (!diskToLetters.TryGetValue(devId, out letters) || letters.Count == 0) continue;
                    string driveTag = string.Join("/", letters.ToArray());

                    string pciController = FindPciAncestorFromDisk(pnpId);
                    if (!string.IsNullOrEmpty(pciController)) {
                        string bdf;
                        if (pnpBdfMap.TryGetValue(pciController, out bdf) && !string.IsNullOrEmpty(bdf))
                            byBdf[bdf] = driveTag + " NVMe · " + model;
                        string rootBdf = GuessBus0RootPortFromChild(pciController);
                        if (!string.IsNullOrEmpty(rootBdf))
                            byBdf[rootBdf] = driveTag + " PCIe Root · " + model;
                    }
                }
            }
        }

        static string FindPciAncestorFromDisk(string diskPnpId) {
            if (string.IsNullOrEmpty(diskPnpId)) return null;
            try {
                // On Windows, disk PNPDeviceID is e.g. SCSI\DISK&VEN_NVME&PROD_...\5&2D34A&0&000000
                // Check Enum\PCI entries whose ParentIdPrefix matches or read Enum\SCSI\<subkey>\<inst>
                int idx = diskPnpId.LastIndexOf('\\');
                if (idx <= 0) return null;
                string diskInst = diskPnpId.Substring(idx + 1);
                string[] parts = diskInst.Split('&');
                string prefix = parts.Length >= 2 ? parts[0] + "&" + parts[1] : "";
                if (prefix.Length == 0) return null;

                using (var pciKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI")) {
                    if (pciKey == null) return null;
                    foreach (string venDev in pciKey.GetSubKeyNames()) {
                        using (var vdKey = pciKey.OpenSubKey(venDev)) {
                            if (vdKey == null) continue;
                            foreach (string inst in vdKey.GetSubKeyNames()) {
                                using (var instKey = vdKey.OpenSubKey(inst)) {
                                    if (instKey == null) continue;
                                    string parentPrefix = Convert.ToString(instKey.GetValue("ParentIdPrefix")) ?? "";
                                    if (parentPrefix.Length > 0 &&
                                        diskInst.IndexOf(parentPrefix, StringComparison.OrdinalIgnoreCase) >= 0) {
                                        return @"PCI\" + venDev + @"\" + inst;
                                    }
                                }
                            }
                        }
                    }
                }
            } catch { }
            return null;
        }

        static string GuessBus0RootPortFromChild(string pciId) {
            int lastAmp = pciId.LastIndexOf('&');
            if (lastAmp < 0 || lastAmp + 1 >= pciId.Length) return null;
            string tail = pciId.Substring(lastAmp + 1);
            int val;
            if (!int.TryParse(tail, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out val)) return null;
            int dev = (val >> 3) & 0x1F;
            int fn = val & 0x07;
            return "0:" + dev + ":" + fn;
        }

        internal static string ParseBdfFromLocation(string loc) {
            if (string.IsNullOrEmpty(loc)) return null;
            // Matches registry resource format "@System32\drivers\pci.sys,#65536;PCI bus %1, device %2, function %3;(5,0,0)"
            var res = Regex.Match(loc, @"\((\d+),\s*(\d+),\s*(\d+)\)\s*$");
            if (res.Success)
                return res.Groups[1].Value + ":" + res.Groups[2].Value + ":" + res.Groups[3].Value;
            var m = Regex.Match(loc, @"bus\s+(\d+),\s*device\s+(\d+),\s*function\s+(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Value + ":" + m.Groups[2].Value + ":" + m.Groups[3].Value;
        }

        static string ExtractHexPart(string text, string prefix) {
            int idx = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || idx + prefix.Length + 4 > text.Length) return "";
            return text.Substring(idx + prefix.Length, 4).ToUpperInvariant();
        }

        static string ExtractQuoted(string rel) {
            if (string.IsNullOrEmpty(rel)) return "";
            int q1 = rel.IndexOf('"');
            int q2 = rel.LastIndexOf('"');
            if (q1 < 0 || q2 <= q1) return "";
            return rel.Substring(q1 + 1, q2 - q1 - 1).Replace(@"\\", @"\");
        }

        static string ShortenDeviceName(string name) {
            if (string.IsNullOrEmpty(name)) return "PCIe Device";
            string s = name.Replace("(R)", "").Replace("(TM)", "").Trim();
            if (s.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
            if (s.Length > 28) s = s.Substring(0, 27) + "…";
            return s;
        }
    }

    sealed class Reading {
        public readonly List<WheaEvent> Events = new List<WheaEvent>();
        public string Gap;
        public string Failure;
        public bool Initialized;
    }

    sealed class SystemLog {
        readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        bool initialized;
        long cursor;
        DateTime? cursorTime;

        public Reading Poll() {
            var result = new Reading();
            try {
                long latest = 0;
                DateTime? latestTime = null;
                using (var query = Reader("*", true))
                using (var entry = query.ReadEvent()) {
                    if (entry != null) {
                        latest = entry.RecordId.Value;
                        latestTime = entry.TimeCreated;
                    }
                }
                long oldest = EventLogSession.GlobalSession.GetLogInformation("System", PathType.LogName)
                    .OldestRecordNumber.GetValueOrDefault();
                // If rollover ran past our snapshot while it was being read, retry without moving the cursor.
                if (latest > 0 && oldest > latest)
                    throw new IOException("System log changed during collection.");

                bool reset = initialized && (latest < cursor ||
                    (latest == cursor && latestTime != cursorTime));
                if (!reset && initialized && cursor > 0 && cursor >= oldest && latest > cursor) {
                    using (var query = Reader("*[System[EventRecordID=" + cursor + "]]", false))
                    using (var anchor = query.ReadEvent()) {
                        if (anchor == null)
                            throw new IOException("System log moved before the event cursor could be verified.");
                        reset = anchor.TimeCreated != cursorTime;
                    }
                }
                bool overwritten = initialized && cursor < oldest - 1;
                if (reset || overwritten)
                    result.Gap = reset ? "System log was cleared or replaced. Coverage gap possible."
                        : "System log rolled past the last record read. Some events may be missing.";

                if (latest > 0 && (!initialized || reset || latest > cursor)) {
                    string boundary = initialized && !reset ? " and EventRecordID>" + cursor :
                        " and TimeCreated[@SystemTime >= '" +
                        started.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture) + "']";
                    string filter = "*[System[" + WheaEvent.Filter + boundary + " and EventRecordID<=" + latest + "]]";
                    using (var query = Reader(filter, false)) {
                        EventRecord entry;
                        while ((entry = query.ReadEvent()) != null) {
                            using (entry) {
                                var match = WheaEvent.Parse(entry.ToXml());
                                if (match != null) result.Events.Add(match);
                            }
                        }
                    }
                }
                cursor = latest;
                cursorTime = latestTime;
                result.Initialized = !initialized;
                initialized = true;
                return result;
            } catch (Exception error) {
                return new Reading { Failure = error.GetType().Name + " — " + error.Message };
            }
        }

        internal static EventLogReader Reader(string xpath, bool newestFirst) {
            return new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) {
                ReverseDirection = newestFirst
            });
        }
    }

    // Alert on the first recent event and once more when a burst reaches ten in a rolling minute.
    sealed class Burst {
        readonly List<DateTimeOffset> recent = new List<DateTimeOffset>();
        DateTimeOffset latest;
        bool escalated;

        public int Add(DateTimeOffset time, DateTimeOffset now) {
            if (time < now.AddSeconds(-60)) return 0;
            if (now - latest > TimeSpan.FromSeconds(60)) escalated = false;
            latest = time > latest ? time : latest;
            recent.RemoveAll(t => t < now.AddSeconds(-60));
            recent.Add(time);
            if (recent.Count >= 10 && !escalated) {
                escalated = true;
                return 2;
            }
            return recent.Count == 1 ? 1 : 0;
        }

        public int Count { get { return recent.Count; } }
    }
}
