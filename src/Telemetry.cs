using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Trace17 {
    sealed class DriveTelemetry {
        public string Name;
        public double? TempC;
        public double? WarningTempC;
        public double? ActivePercent;
        public double? UsedSpacePercent;
        public double FreeGb;
        public double TotalGb;
    }

    sealed class TelemetrySnapshot {
        public string CpuName = "CPU";
        public double? CpuUsagePercent;
        public double? CpuTempC;
        public string CpuTempSource;
        public string GpuName = "GPU";
        public double? GpuUsagePercent;
        public double? GpuTempC;
        public double? RamUsagePercent;
        public double RamUsedGb;
        public double RamTotalGb;
        public readonly List<DriveTelemetry> Drives = new List<DriveTelemetry>();
        public string ActiveGameName;
        public uint ActiveGamePid;
        public double? Fps;
        public double? FrameTimeMs;
        public bool PresentMonConnected;
    }

    static class HardwareSensors {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr device, uint ioControlCode,
            byte[] inBuffer, int inBufferSize, byte[] outBuffer, int outBufferSize,
            out int bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        const uint FILE_SHARE_READ_WRITE = 3;
        const uint OPEN_EXISTING = 3;
        const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
        const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        const int StorageDeviceTemperatureProperty = 52;
        const uint HWiNFO_SM2_SIGNATURE = 0x53695748; // "HWiS"

        public static void ReadMemory(TelemetrySnapshot snap) {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (!GlobalMemoryStatusEx(ref mem) || mem.ullTotalPhys == 0) return;
            snap.RamTotalGb = mem.ullTotalPhys / 1073741824.0;
            snap.RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
            snap.RamUsagePercent = (snap.RamUsedGb / snap.RamTotalGb) * 100.0;
        }

        public static int? GetPhysicalDriveNumber(string driveRoot) {
            if (string.IsNullOrEmpty(driveRoot)) return null;
            string letter = driveRoot.Substring(0, 1).ToUpperInvariant();
            IntPtr handle = CreateFile(@"\\.\" + letter + ":", 0, FILE_SHARE_READ_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return null;
            try {
                byte[] output = new byte[16];
                int returned;
                if (!DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0,
                    output, output.Length, out returned, IntPtr.Zero) || returned < 8)
                    return null;
                return BitConverter.ToInt32(output, 4);
            } finally {
                CloseHandle(handle);
            }
        }

        public static bool TryReadPhysicalDriveTemperature(int driveNumber, out double tempC, out double warningC) {
            tempC = 0;
            warningC = 0;
            IntPtr handle = CreateFile(@"\\.\PhysicalDrive" + driveNumber, 0, FILE_SHARE_READ_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return false;
            try {
                byte[] input = new byte[12];
                BitConverter.GetBytes(StorageDeviceTemperatureProperty).CopyTo(input, 0);
                byte[] output = new byte[512];
                int returned;
                if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length,
                    output, output.Length, out returned, IntPtr.Zero))
                    return false;
                return ParseStorageTemperatureDescriptor(output, returned, out tempC, out warningC);
            } finally {
                CloseHandle(handle);
            }
        }

        internal static bool ParseStorageTemperatureDescriptor(byte[] data, int length, out double tempC, out double warningC) {
            tempC = 0;
            warningC = 0;
            if (data == null || length < 40) return false;
            ushort infoCount = BitConverter.ToUInt16(data, 12);
            if (infoCount == 0) return false;
            short warnRaw = BitConverter.ToInt16(data, 10);
            short tempRaw = BitConverter.ToInt16(data, 26);
            if (tempRaw <= -50 || tempRaw >= 200) return false;
            tempC = tempRaw;
            if (warnRaw > 0 && warnRaw < 200) warningC = warnRaw;
            return true;
        }

        public static bool TryReadCpuTempFromHWiNFO(out double tempC, out string source) {
            if (TryReadCpuTempFromSharedMemory(@"Global\HWiNFO_SENS_SM2", out tempC) ||
                TryReadCpuTempFromSharedMemory(@"Local\HWiNFO_SENS_SM2", out tempC)) {
                source = "HWiNFO64 SM2";
                return true;
            }
            if (TryReadCpuTempFromGadgetRegistry(out tempC)) {
                source = "HWiNFO64 Gadget";
                return true;
            }
            if (TryReadCpuTempFromHardwareMonitorWmi(@"root\LibreHardwareMonitor", out tempC)) {
                source = "LibreHardwareMonitor";
                return true;
            }
            if (TryReadCpuTempFromHardwareMonitorWmi(@"root\OpenHardwareMonitor", out tempC)) {
                source = "OpenHardwareMonitor";
                return true;
            }
            source = null;
            return false;
        }

        static bool TryReadCpuTempFromSharedMemory(string mappingName, out double tempC) {
            tempC = 0;
            try {
                using (var mmf = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.Read))
                using (var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read)) {
                    if (view.Capacity < 44) return false;
                    byte[] header = new byte[44];
                    view.ReadArray(0, header, 0, 44);
                    if (BitConverter.ToUInt32(header, 0) != HWiNFO_SM2_SIGNATURE) return false;
                    uint sensorOffset = BitConverter.ToUInt32(header, 16);
                    uint sensorSize = BitConverter.ToUInt32(header, 20);
                    uint sensorCount = BitConverter.ToUInt32(header, 24);
                    uint readingOffset = BitConverter.ToUInt32(header, 28);
                    uint readingSize = BitConverter.ToUInt32(header, 32);
                    uint readingCount = BitConverter.ToUInt32(header, 36);
                    if (readingSize < 292 || readingCount == 0 || readingCount > 8192) return false;

                    var sensorNames = new Dictionary<uint, string>();
                    if (sensorSize >= 136 && sensorCount <= 512) {
                        byte[] sBuf = new byte[sensorSize];
                        for (uint i = 0; i < sensorCount; i++) {
                            long pos = sensorOffset + (long)i * sensorSize;
                            if (pos + sensorSize > view.Capacity) break;
                            view.ReadArray(pos, sBuf, 0, (int)sensorSize);
                            string sName = ReadFixedAnsi(sBuf, 8, 128);
                            sensorNames[i] = sName;
                        }
                    }

                    byte[] rBuf = new byte[readingSize];
                    double bestValue = 0;
                    int bestScore = 0;
                    for (uint i = 0; i < readingCount; i++) {
                        long pos = readingOffset + (long)i * readingSize;
                        if (pos + readingSize > view.Capacity) break;
                        view.ReadArray(pos, rBuf, 0, (int)readingSize);
                        int type = BitConverter.ToInt32(rBuf, 0);
                        if (type != 1) continue; // SENSOR_TYPE_TEMP
                        uint sensorIdx = BitConverter.ToUInt32(rBuf, 4);
                        string labelOrig = ReadFixedAnsi(rBuf, 12, 128);
                        string labelUser = ReadFixedAnsi(rBuf, 140, 128);
                        string unit = ReadFixedAnsi(rBuf, 268, 16);
                        if (!unit.Contains("C")) continue;
                        double val = BitConverter.ToDouble(rBuf, 284);
                        if (double.IsNaN(val) || val <= 0 || val > 150) continue;

                        string sensorName;
                        sensorNames.TryGetValue(sensorIdx, out sensorName);
                        int score = ScoreCpuTempLabel(sensorName ?? "", labelOrig, labelUser);
                        if (score > bestScore) {
                            bestScore = score;
                            bestValue = val;
                        }
                    }
                    if (bestScore > 0) {
                        tempC = bestValue;
                        return true;
                    }
                }
            } catch {
            }
            return false;
        }

        static bool TryReadCpuTempFromGadgetRegistry(out double tempC) {
            tempC = 0;
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\HWiNFO64\VSB")) {
                    if (key == null) return false;
                    double bestValue = 0;
                    int bestScore = 0;
                    for (int i = 0; i < 256; i++) {
                        string sensor = key.GetValue("Sensor" + i) as string;
                        string label = key.GetValue("Label" + i) as string;
                        string raw = key.GetValue("ValueRaw" + i) as string;
                        string full = key.GetValue("Value" + i) as string;
                        if (string.IsNullOrEmpty(label)) continue;
                        if (full != null && !full.Contains("C")) continue;
                        double val;
                        if (!double.TryParse((raw ?? "").Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out val))
                            continue;
                        if (val <= 0 || val > 150) continue;
                        int score = ScoreCpuTempLabel(sensor ?? "", label, label);
                        if (score > bestScore) {
                            bestScore = score;
                            bestValue = val;
                        }
                    }
                    if (bestScore > 0) {
                        tempC = bestValue;
                        return true;
                    }
                }
            } catch {
            }
            return false;
        }

        static bool TryReadCpuTempFromHardwareMonitorWmi(string wmiNamespace, out double tempC) {
            tempC = 0;
            try {
                using (var searcher = new ManagementObjectSearcher(wmiNamespace,
                    "SELECT Name, Parent, Value FROM Sensor WHERE SensorType='Temperature'")) {
                    double bestValue = 0;
                    int bestScore = 0;
                    foreach (ManagementObject obj in searcher.Get()) {
                        string name = Convert.ToString(obj["Name"]) ?? "";
                        string parent = Convert.ToString(obj["Parent"]) ?? "";
                        if (parent.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        double val = Convert.ToDouble(obj["Value"]);
                        if (val <= 0 || val > 150) continue;
                        int score = ScoreCpuTempLabel("CPU", name, name);
                        if (score > bestScore) {
                            bestScore = score;
                            bestValue = val;
                        }
                    }
                    if (bestScore > 0) {
                        tempC = bestValue;
                        return true;
                    }
                }
            } catch {
            }
            return false;
        }

        internal static int ScoreCpuTempLabel(string sensor, string orig, string user) {
            string s = (sensor ?? "").ToLowerInvariant();
            string l = ((user ?? "") + " " + (orig ?? "")).ToLowerInvariant();
            if (s.Contains("gpu") || s.Contains("drive") || s.Contains("nvme") || s.Contains("ssd") ||
                l.Contains("gpu") || l.Contains("pch") || l.Contains("vrm") || l.Contains("mos") || l.Contains("dimm"))
                return 0;
            bool isCpuSensor = s.Contains("cpu") || s.Contains("intel") || s.Contains("amd") || s.Contains("dts") || s.Contains("ec");
            if (!isCpuSensor && !l.Contains("cpu") && !l.Contains("core") && !l.Contains("package"))
                return 0;
            if (l.Contains("cpu package")) return 100;
            if (l.Contains("tctl/tdie") || l.Contains("cpu die")) return 95;
            if (l.Contains("core max") || l.Contains("core temperatures")) return 90;
            if (l.Contains("package")) return 85;
            if (l.Contains("cpu") && !l.Contains("fan") && !l.Contains("power") && !l.Contains("limit")) return 70;
            if (isCpuSensor && l.Contains("core")) return 60;
            return 0;
        }

        static string ReadFixedAnsi(byte[] buffer, int offset, int maxLen) {
            int end = offset;
            int limit = Math.Min(buffer.Length, offset + maxLen);
            while (end < limit && buffer[end] != 0) end++;
            return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
        }
    }

    sealed class PresentMonClient : IDisposable {
        const string DllPath = @"C:\Program Files\Intel\PresentMonSharedService\PresentMonAPI2.dll";
        const int PM_METRIC_CPU_FRAME_TIME = 8;
        const int PM_METRIC_DISPLAYED_FPS = 11;
        const int PM_METRIC_PRESENTED_FPS = 12;
        const int PM_METRIC_GPU_TEMPERATURE = 30;
        const int PM_METRIC_GPU_UTILIZATION = 32;
        const int PM_METRIC_CPU_UTILIZATION = 56;
        const int PM_METRIC_CPU_TEMPERATURE = 59;
        const int PM_METRIC_DISPLAYED_FRAME_TIME = 85;
        const int PM_STAT_AVG = 1;
        const int PM_DEVICE_TYPE_GRAPHICS_ADAPTER = 1;
        const int PM_DEVICE_VENDOR_INTEL = 0;
        const int PM_METRIC_AVAILABILITY_AVAILABLE = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct PM_QUERY_ELEMENT {
            public int metric;
            public int stat;
            public uint deviceId;
            public uint arrayIndex;
            public ulong dataOffset;
            public ulong dataSize;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string path);

        [DllImport("PresentMonAPI2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int pmOpenSession(out IntPtr handle);
        [DllImport("PresentMonAPI2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int pmCloseSession(IntPtr handle);
        [DllImport("PresentMonAPI2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int pmGetIntrospectionRoot(IntPtr handle, out IntPtr root);
        [DllImport("PresentMonAPI2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int pmFreeIntrospectionRoot(IntPtr root);
        [DllImport("PresentMonAPI2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int pmStartTrackingProcess(IntPtr handle, uint pid);
        [DllImport("PresentMonAPI2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int pmStopTrackingProcess(IntPtr handle, uint pid);
        [DllImport("PresentMonAPI2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int pmRegisterDynamicQuery(IntPtr handle, out IntPtr queryHandle,
            [In, Out] PM_QUERY_ELEMENT[] elements, UIntPtr numElements, double windowSizeMs, double metricOffsetMs);
        [DllImport("PresentMonAPI2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int pmFreeDynamicQuery(IntPtr queryHandle);
        [DllImport("PresentMonAPI2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int pmPollDynamicQuery(IntPtr queryHandle, uint pid, byte[] blob, ref uint numSwapChains);

        static bool dllLoaded;
        static bool dllChecked;

        IntPtr session;
        IntPtr sysQuery;
        IntPtr fpsQuery;
        PM_QUERY_ELEMENT[] sysElems;
        PM_QUERY_ELEMENT[] fpsElems;
        uint trackedPid;
        uint gpuDeviceId = 2;
        bool hasGpuTemp;
        bool hasGpuUtil;
        bool hasCpuUtil;
        bool hasCpuTemp;
        string gpuDeviceName = "GPU";
        DateTime lastConnectAttempt = DateTime.MinValue;

        public bool IsConnected { get { return session != IntPtr.Zero; } }
        public string GpuName { get { return gpuDeviceName; } }

        static bool EnsureDllLoaded() {
            if (dllChecked) return dllLoaded;
            dllChecked = true;
            try {
                if (File.Exists(DllPath))
                    dllLoaded = LoadLibrary(DllPath) != IntPtr.Zero;
            } catch {
                dllLoaded = false;
            }
            return dllLoaded;
        }

        public bool EnsureConnected() {
            if (session != IntPtr.Zero) return true;
            if ((DateTime.UtcNow - lastConnectAttempt).TotalSeconds < 5) return false;
            lastConnectAttempt = DateTime.UtcNow;
            if (!EnsureDllLoaded()) return false;
            try {
                IntPtr h;
                if (pmOpenSession(out h) != 0 || h == IntPtr.Zero) return false;
                session = h;
                DiscoverDevicesAndMetrics();
                RegisterSystemQuery();
                RegisterFpsQuery();
                return true;
            } catch {
                CloseSession();
                return false;
            }
        }

        void DiscoverDevicesAndMetrics() {
            IntPtr root;
            if (pmGetIntrospectionRoot(session, out root) != 0 || root == IntPtr.Zero) return;
            try {
                // Find discrete GPU first, otherwise any graphics adapter.
                IntPtr devVec = Marshal.ReadIntPtr(root, 16);
                IntPtr dData = Marshal.ReadIntPtr(devVec, 0);
                long dSize = Marshal.ReadInt64(devVec, 8);
                uint bestGpu = 0;
                int bestScore = -1;
                string bestName = "GPU";
                for (int i = 0; i < (int)dSize; i++) {
                    IntPtr dp = Marshal.ReadIntPtr(dData, i * 8);
                    uint did = (uint)Marshal.ReadInt32(dp, 0);
                    int dtype = Marshal.ReadInt32(dp, 4);
                    int dvendor = Marshal.ReadInt32(dp, 8);
                    string name = ReadPmString(Marshal.ReadIntPtr(dp, 16));
                    if (dtype != PM_DEVICE_TYPE_GRAPHICS_ADAPTER) continue;
                    int score = dvendor != PM_DEVICE_VENDOR_INTEL ? 10 : 5;
                    if (score > bestScore) {
                        bestScore = score;
                        bestGpu = did;
                        bestName = string.IsNullOrEmpty(name) ? "GPU" : name;
                    }
                }
                if (bestScore >= 0) {
                    gpuDeviceId = bestGpu;
                    gpuDeviceName = ShortenGpuName(bestName);
                }

                // Check availability of GPU temp/util and CPU util/temp.
                IntPtr mVec = Marshal.ReadIntPtr(root, 0);
                IntPtr mData = Marshal.ReadIntPtr(mVec, 0);
                long mSize = Marshal.ReadInt64(mVec, 8);
                for (int m = 0; m < (int)mSize; m++) {
                    IntPtr mp = Marshal.ReadIntPtr(mData, m * 8);
                    int metricId = Marshal.ReadInt32(mp, 0);
                    if (metricId != PM_METRIC_GPU_TEMPERATURE && metricId != PM_METRIC_GPU_UTILIZATION &&
                        metricId != PM_METRIC_CPU_UTILIZATION && metricId != PM_METRIC_CPU_TEMPERATURE)
                        continue;
                    IntPtr dmiVec = Marshal.ReadIntPtr(mp, 32);
                    if (dmiVec == IntPtr.Zero) continue;
                    IntPtr vData = Marshal.ReadIntPtr(dmiVec, 0);
                    long vSize = Marshal.ReadInt64(dmiVec, 8);
                    for (int j = 0; j < (int)vSize; j++) {
                        IntPtr dmi = Marshal.ReadIntPtr(vData, j * 8);
                        uint devId = (uint)Marshal.ReadInt32(dmi, 0);
                        int avail = Marshal.ReadInt32(dmi, 4);
                        if (avail != PM_METRIC_AVAILABILITY_AVAILABLE) continue;
                        if (metricId == PM_METRIC_GPU_TEMPERATURE && devId == gpuDeviceId) hasGpuTemp = true;
                        if (metricId == PM_METRIC_GPU_UTILIZATION && devId == gpuDeviceId) hasGpuUtil = true;
                        if (metricId == PM_METRIC_CPU_UTILIZATION && devId == 65536) hasCpuUtil = true;
                        if (metricId == PM_METRIC_CPU_TEMPERATURE && (devId == 65536 || devId == 1)) hasCpuTemp = true;
                    }
                }
            } finally {
                pmFreeIntrospectionRoot(root);
            }
        }

        void RegisterSystemQuery() {
            var list = new List<PM_QUERY_ELEMENT>();
            if (hasGpuTemp) list.Add(new PM_QUERY_ELEMENT { metric = PM_METRIC_GPU_TEMPERATURE, stat = PM_STAT_AVG, deviceId = gpuDeviceId });
            if (hasGpuUtil) list.Add(new PM_QUERY_ELEMENT { metric = PM_METRIC_GPU_UTILIZATION, stat = PM_STAT_AVG, deviceId = gpuDeviceId });
            if (hasCpuUtil) list.Add(new PM_QUERY_ELEMENT { metric = PM_METRIC_CPU_UTILIZATION, stat = PM_STAT_AVG, deviceId = 65536 });
            if (hasCpuTemp) list.Add(new PM_QUERY_ELEMENT { metric = PM_METRIC_CPU_TEMPERATURE, stat = PM_STAT_AVG, deviceId = 65536 });
            if (list.Count == 0) return;
            sysElems = list.ToArray();
            IntPtr qh;
            if (pmRegisterDynamicQuery(session, out qh, sysElems, (UIntPtr)sysElems.Length, 1000.0, 0.0) == 0)
                sysQuery = qh;
        }

        void RegisterFpsQuery() {
            fpsElems = new[] {
                new PM_QUERY_ELEMENT { metric = PM_METRIC_DISPLAYED_FPS, stat = PM_STAT_AVG, deviceId = 0 },
                new PM_QUERY_ELEMENT { metric = PM_METRIC_PRESENTED_FPS, stat = PM_STAT_AVG, deviceId = 0 },
                new PM_QUERY_ELEMENT { metric = PM_METRIC_DISPLAYED_FRAME_TIME, stat = PM_STAT_AVG, deviceId = 0 },
                new PM_QUERY_ELEMENT { metric = PM_METRIC_CPU_FRAME_TIME, stat = PM_STAT_AVG, deviceId = 0 }
            };
            IntPtr qh;
            if (pmRegisterDynamicQuery(session, out qh, fpsElems, (UIntPtr)fpsElems.Length, 1000.0, 0.0) == 0)
                fpsQuery = qh;
        }

        public void PollSystem(TelemetrySnapshot snap) {
            if (!EnsureConnected() || sysQuery == IntPtr.Zero || sysElems == null) return;
            snap.PresentMonConnected = true;
            snap.GpuName = gpuDeviceName;
            uint swaps = 1;
            byte[] blob = new byte[256];
            int rc = pmPollDynamicQuery(sysQuery, 0, blob, ref swaps);
            if (rc != 0 || swaps == 0) {
                if (rc != 0) CloseSession();
                return;
            }
            for (int i = 0; i < sysElems.Length; i++) {
                int offset = (int)sysElems[i].dataOffset;
                if (offset + 8 > blob.Length) continue;
                double val = BitConverter.ToDouble(blob, offset);
                if (double.IsNaN(val) || double.IsInfinity(val)) continue;
                if (sysElems[i].metric == PM_METRIC_GPU_TEMPERATURE && val > 0 && val < 150)
                    snap.GpuTempC = val;
                else if (sysElems[i].metric == PM_METRIC_GPU_UTILIZATION && val >= 0 && val <= 100)
                    snap.GpuUsagePercent = val;
                else if (sysElems[i].metric == PM_METRIC_CPU_UTILIZATION && val >= 0 && val <= 100 && !snap.CpuUsagePercent.HasValue)
                    snap.CpuUsagePercent = val;
                else if (sysElems[i].metric == PM_METRIC_CPU_TEMPERATURE && val > 0 && val < 150) {
                    snap.CpuTempC = val;
                    snap.CpuTempSource = "PresentMon";
                }
            }
        }

        public void SetTrackedProcess(uint pid) {
            if (pid == trackedPid) return;
            if (session != IntPtr.Zero && trackedPid != 0) {
                try { pmStopTrackingProcess(session, trackedPid); } catch { }
            }
            trackedPid = 0;
            if (pid != 0 && EnsureConnected()) {
                try {
                    if (pmStartTrackingProcess(session, pid) == 0) trackedPid = pid;
                } catch { }
            }
        }

        public void PollFps(uint pid, TelemetrySnapshot snap) {
            if (pid == 0) {
                SetTrackedProcess(0);
                return;
            }
            if (!EnsureConnected() || fpsQuery == IntPtr.Zero) return;
            SetTrackedProcess(pid);
            if (trackedPid != pid) return;
            uint swaps = 4;
            byte[] blob = new byte[512];
            int rc = pmPollDynamicQuery(fpsQuery, pid, blob, ref swaps);
            if (rc != 0 || swaps == 0) return;

            int stride = 0;
            for (int i = 0; i < fpsElems.Length; i++) stride += (int)fpsElems[i].dataSize;
            if (stride <= 0) stride = 32;

            double bestDisplayed = 0;
            double bestPresented = 0;
            double bestFrameMs = 0;
            for (uint s = 0; s < swaps; s++) {
                int baseOff = (int)s * stride;
                if (baseOff + stride > blob.Length) break;
                double dFps = BitConverter.ToDouble(blob, baseOff + (int)fpsElems[0].dataOffset);
                double pFps = BitConverter.ToDouble(blob, baseOff + (int)fpsElems[1].dataOffset);
                double dMs = BitConverter.ToDouble(blob, baseOff + (int)fpsElems[2].dataOffset);
                double cMs = BitConverter.ToDouble(blob, baseOff + (int)fpsElems[3].dataOffset);
                if (!double.IsNaN(dFps) && dFps > bestDisplayed && dFps < 5000) bestDisplayed = dFps;
                if (!double.IsNaN(pFps) && pFps > bestPresented && pFps < 5000) bestPresented = pFps;
                double ms = (!double.IsNaN(dMs) && dMs > 0 && dMs < 2000) ? dMs
                    : ((!double.IsNaN(cMs) && cMs > 0 && cMs < 2000) ? cMs : 0);
                if (ms > 0 && (bestFrameMs == 0 || bestDisplayed > 0)) bestFrameMs = ms;
            }
            double chosen = bestDisplayed >= 1.0 ? bestDisplayed : bestPresented;
            if (chosen >= 1.0) {
                snap.Fps = chosen;
                snap.FrameTimeMs = bestFrameMs > 0 ? bestFrameMs : (1000.0 / chosen);
            }
        }

        static string ReadPmString(IntPtr strPtr) {
            if (strPtr == IntPtr.Zero) return "";
            IntPtr pData = Marshal.ReadIntPtr(strPtr, 0);
            if (pData == IntPtr.Zero) return "";
            string s = Marshal.PtrToStringAnsi(pData);
            return string.IsNullOrEmpty(s) ? "" : s.Trim();
        }

        static string ShortenGpuName(string name) {
            string s = name.Replace("(R)", "").Replace("(TM)", "").Trim();
            if (s.StartsWith("AMD Radeon ", StringComparison.OrdinalIgnoreCase))
                return s.Substring(4);
            return s;
        }

        void CloseSession() {
            if (trackedPid != 0 && session != IntPtr.Zero) {
                try { pmStopTrackingProcess(session, trackedPid); } catch { }
            }
            trackedPid = 0;
            if (sysQuery != IntPtr.Zero) {
                try { pmFreeDynamicQuery(sysQuery); } catch { }
                sysQuery = IntPtr.Zero;
            }
            if (fpsQuery != IntPtr.Zero) {
                try { pmFreeDynamicQuery(fpsQuery); } catch { }
                fpsQuery = IntPtr.Zero;
            }
            if (session != IntPtr.Zero) {
                try { pmCloseSession(session); } catch { }
                session = IntPtr.Zero;
            }
        }

        public void Dispose() { CloseSession(); }
    }

    sealed class TelemetryCollector : IDisposable {
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        static readonly HashSet<string> ShellProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
            "SearchApp", "LockApp", "TextInputHost", "ApplicationFrameHost", "dwm",
            "Taskmgr", "SystemSettings", "csrss", "Idle"
        };

        readonly PresentMonClient presentMon = new PresentMonClient();
        readonly Dictionary<string, PerformanceCounter> diskIdleCounters = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> driveNumberCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        PerformanceCounter cpuCounter;
        PerformanceCounter gpuFallbackCounter;
        bool countersInitialized;
        uint lastTargetPid;
        string lastTargetName;

        public bool FpsOverlayEnabled { get; set; }

        void EnsureCounters() {
            if (countersInitialized) return;
            countersInitialized = true;
            try {
                cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
                cpuCounter.NextValue();
            } catch {
                try {
                    cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    cpuCounter.NextValue();
                } catch { cpuCounter = null; }
            }
            try {
                var cat = new PerformanceCounterCategory("PhysicalDisk");
                foreach (string inst in cat.GetInstanceNames()) {
                    if (string.Equals(inst, "_Total", StringComparison.OrdinalIgnoreCase)) continue;
                    var pc = new PerformanceCounter("PhysicalDisk", "% Idle Time", inst, true);
                    pc.NextValue();
                    diskIdleCounters[inst] = pc;
                }
            } catch { }
            try {
                var gpuCat = new PerformanceCounterCategory("GPU Engine");
                string[] instances = gpuCat.GetInstanceNames();
                foreach (string inst in instances) {
                    if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0) {
                        gpuFallbackCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                        gpuFallbackCounter.NextValue();
                        break;
                    }
                }
            } catch { }
        }

        public TelemetrySnapshot Sample() {
            EnsureCounters();
            var snap = new TelemetrySnapshot();

            // 1. CPU Usage
            if (cpuCounter != null) {
                try {
                    float val = cpuCounter.NextValue();
                    snap.CpuUsagePercent = Math.Max(0.0, Math.Min(100.0, val));
                } catch { }
            }

            // 2. PresentMon (GPU Temp, GPU Usage, CPU fallback, optional CPU Temp)
            presentMon.PollSystem(snap);
            if (!snap.GpuUsagePercent.HasValue && gpuFallbackCounter != null) {
                try {
                    snap.GpuUsagePercent = Math.Max(0.0, Math.Min(100.0, gpuFallbackCounter.NextValue()));
                } catch { }
            }

            // 3. CPU Temperature (PresentMon if supported -> HWiNFO64 -> LibreHardwareMonitor)
            if (!snap.CpuTempC.HasValue) {
                double cpuTemp;
                string source;
                if (HardwareSensors.TryReadCpuTempFromHWiNFO(out cpuTemp, out source)) {
                    snap.CpuTempC = cpuTemp;
                    snap.CpuTempSource = source;
                }
            }

            // 4. RAM Usage
            HardwareSensors.ReadMemory(snap);

            // 5. SSD Temps & Usage
            CollectDrives(snap);

            // 6. Active Game FPS when overlay is enabled
            if (FpsOverlayEnabled) {
                uint pid;
                string name;
                DetectActiveGame(out pid, out name);
                snap.ActiveGamePid = pid;
                snap.ActiveGameName = name;
                presentMon.PollFps(pid, snap);
            } else {
                presentMon.SetTrackedProcess(0);
            }

            return snap;
        }

        void CollectDrives(TelemetrySnapshot snap) {
            try {
                var seenPhysicalDrives = new HashSet<int>();
                foreach (var drive in DriveInfo.GetDrives()) {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string label = "";
                    try { label = drive.VolumeLabel ?? ""; } catch { }
                    if (string.Equals(label, "Google Drive", StringComparison.OrdinalIgnoreCase)) continue;
                    string letter = drive.Name.Substring(0, 2).ToUpperInvariant(); // e.g. "C:"

                    int physNum;
                    if (!driveNumberCache.TryGetValue(letter, out physNum)) {
                        int? mapped = HardwareSensors.GetPhysicalDriveNumber(letter);
                        physNum = mapped.HasValue ? mapped.Value : -1;
                        driveNumberCache[letter] = physNum;
                    }
                    if (physNum >= 0 && !seenPhysicalDrives.Add(physNum)) continue;

                    var item = new DriveTelemetry { Name = letter };
                    if (drive.TotalSize > 0) {
                        item.TotalGb = drive.TotalSize / 1073741824.0;
                        item.FreeGb = drive.AvailableFreeSpace / 1073741824.0;
                        item.UsedSpacePercent = ((drive.TotalSize - drive.AvailableFreeSpace) / (double)drive.TotalSize) * 100.0;
                    }
                    if (physNum >= 0) {
                        double t, warn;
                        if (HardwareSensors.TryReadPhysicalDriveTemperature(physNum, out t, out warn)) {
                            item.TempC = t;
                            if (warn > 0) item.WarningTempC = warn;
                        }
                    }

                    foreach (var kvp in diskIdleCounters) {
                        // Instance names look like "0 C:" or "1 D:"
                        if (kvp.Key.IndexOf(letter, StringComparison.OrdinalIgnoreCase) >= 0) {
                            try {
                                float idle = kvp.Value.NextValue();
                                item.ActivePercent = Math.Max(0.0, Math.Min(100.0, 100.0 - idle));
                            } catch { }
                            break;
                        }
                    }
                    snap.Drives.Add(item);
                }
            } catch { }
        }

        void DetectActiveGame(out uint pid, out string name) {
            pid = 0;
            name = null;
            try {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero) {
                    uint fgPid;
                    GetWindowThreadProcessId(fg, out fgPid);
                    if (fgPid != 0 && fgPid != (uint)Process.GetCurrentProcess().Id) {
                        string procName = GetProcessNameSafe(fgPid);
                        if (!string.IsNullOrEmpty(procName) && !ShellProcesses.Contains(procName)) {
                            lastTargetPid = fgPid;
                            lastTargetName = procName;
                        }
                    }
                }
                if (lastTargetPid != 0) {
                    try {
                        using (var p = Process.GetProcessById((int)lastTargetPid)) {
                            if (p.HasExited) {
                                lastTargetPid = 0;
                                lastTargetName = null;
                            }
                        }
                    } catch {
                        lastTargetPid = 0;
                        lastTargetName = null;
                    }
                }
                pid = lastTargetPid;
                name = lastTargetName;
            } catch { }
        }

        public static IntPtr GetForegroundWindowHandle() {
            try { return GetForegroundWindow(); } catch { return IntPtr.Zero; }
        }

        static string GetProcessNameSafe(uint pid) {
            try {
                using (var p = Process.GetProcessById((int)pid)) {
                    return p.ProcessName;
                }
            } catch {
                return null;
            }
        }

        public void Dispose() {
            presentMon.Dispose();
            if (cpuCounter != null) cpuCounter.Dispose();
            if (gpuFallbackCounter != null) gpuFallbackCounter.Dispose();
            foreach (var pc in diskIdleCounters.Values) pc.Dispose();
            diskIdleCounters.Clear();
        }
    }
}
