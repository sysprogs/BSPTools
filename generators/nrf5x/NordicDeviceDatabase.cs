using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace nrf5x
{
    class NordicDeviceDatabase
    {
        public readonly NordicMCUBuilder[] Devices;
        public readonly SoftdeviceDefinition[] Softdevices;

        struct QualifiedLinkerScript
        {
            public string FullPath;
            public string BoardName;
            public string Softdevice;
            public int DeviceNumber;

            public override string ToString() => $"nRF{DeviceNumber} - {Softdevice}";
        }

        static DeviceSummary ExtractDeviceSummary(string linkerScript)
        {
            var rgDevice = new Regex("nrf(52[0-9]+)_xx..\\.ld");
            var m = rgDevice.Match(linkerScript);
            if (!m.Success)
                return default;

            var info = new LDFileMemoryInfo(linkerScript);
            if (info.FLASH.Length == 0 || info.RAM.Length == 0)
                throw new Exception("Invalid FLASH/RAM size in " + linkerScript);

            var svdFileName = $"nrf{m.Groups[1].Value}.svd";
            if (svdFileName == "nrf52832.svd")
                svdFileName = "nrf52.svd";  //Backward compatibility
            var svdFilePath = Path.Combine(Path.GetDirectoryName(linkerScript), svdFileName);

            if (!File.Exists(svdFilePath))
                throw new Exception("Missing " + svdFilePath);

            var doc = new XmlDocument();
            doc.Load(svdFilePath);
            var cpuNode = doc.DocumentElement.SelectSingleNode("cpu") as XmlElement ?? throw new Exception("Missing 'cpu' element.");

            bool hasFpu = cpuNode.SelectSingleNode("fpuPresent")?.InnerText switch
            {
                "1" => true,
                "0" => false,
                _ => throw new Exception("Missing fpuPresent node")
            };

            if (cpuNode.SelectSingleNode("name")?.InnerText != "CM4")
                throw new Exception("Unexpected core name for " + svdFileName);

            return new DeviceSummary
            {
                DeviceNumber = int.Parse(m.Groups[1].Value),
                FLASH = info.FLASH,
                RAM = info.RAM,
                ID = Path.GetFileNameWithoutExtension(linkerScript),
                HasFPU = hasFpu,
                SVDFileName = svdFileName,
            };
        }

        public NordicDeviceDatabase(string rulesDir, string inputDir)
        {
            //Detect supported devices by parsing the linker scripts in the MDK directory
            var deviceSummaries = Directory.GetFiles(Path.Combine(inputDir, @"modules\nrfx\mdk"), "*.ld")
                .Select(ExtractDeviceSummary).Where(s => s.ID != null).ToArray();

            Devices = deviceSummaries.Select(s => new NordicMCUBuilder(s)).ToArray();

            var deviceByNumber = Devices.Where(d=>d.Name.EndsWith("xxaa", StringComparison.InvariantCultureIgnoreCase)).ToDictionary(d => d.Summary.DeviceNumber);

            //Softdevice are manually compiled from Nordic website/documentation
            Softdevices = File.ReadAllLines(Path.Combine(rulesDir, "softdevices.txt"))
                .Where(l => !l.Trim().StartsWith("#"))
                .Select(l => SoftdeviceDefinition.Parse(l, deviceByNumber))
                .ToArray();

            //Scan all linker scripts from example projects. Try to cover as many device/softdevice combinations as possible.
            var allLinkerScripts = Directory.GetFiles(inputDir, "*.ld", SearchOption.AllDirectories)
                .Select(QualifyLinkerScript)
                .Where(l => l.FullPath != null)
                .ToArray();

            Console.WriteLine("Analyzing softdevice scripts...");

            //We now need to find out the maximum RAM/FLASH amounts reserved by each softdevice.
            //Because not all supported MCU/softdevice combinations are covered by the SDK examples, we need to extract this reliably and rebuild the linker scripts for all supported variants.
            HashSet<string> inconsistentLinkerScripts = new HashSet<string>();

            foreach (var sd in Softdevices)
            {
                HashSet<ulong> reservedRAMVariants = new HashSet<ulong>();
                HashSet<ulong> reservedFLASHVariants = new HashSet<ulong>();

                foreach(var lds in allLinkerScripts)
                {
                    if (StringComparer.InvariantCultureIgnoreCase.Compare(lds.Softdevice, sd.Name) != 0)
                        continue;

                    var memInfo = new LDFileMemoryInfo(lds.FullPath);
                    var dev = deviceByNumber[lds.DeviceNumber];

                    if (memInfo.RAM.End != dev.Summary.RAM.End)
                        throw new Exception("Inconsistent end of RAM for " + lds.FullPath);

                    if (memInfo.FLASH.End != dev.Summary.FLASH.End)
                    {
                        inconsistentLinkerScripts.Add(lds.FullPath);
                        continue;
                    }

                    reservedFLASHVariants.Add(memInfo.FLASH.Origin - dev.Summary.FLASH.Origin);
                    reservedRAMVariants.Add(memInfo.RAM.Origin - dev.Summary.RAM.Origin);
                }

                sd.ReservedFLASH = reservedFLASHVariants.Cast<ulong?>().SingleOrDefault() ?? throw new Exception("Inconsistent FLASH offsets found for " + sd.Name);
                sd.ReservedRAM = reservedRAMVariants.Max();

                foreach (var mcu in sd.MCUs)
                    mcu.Softdevices.Add(sd);
            }

            if (inconsistentLinkerScripts.Count(s => !s.Contains("ble_app_buttonless_dfu")) > 0)
                throw new Exception("Found inconsistent linker scripts. Please investigate.");

            var commonLinkerScriptFile = Path.Combine(inputDir, @"modules\nrfx\mdk\" + CommonLinkerScriptFileName);
            if (!File.Exists(commonLinkerScriptFile))
            {
                /* The nrf_full.ld file should be created manually by copying the SECTIONS parts from one of the example linker scripts.
                 * In the SDK v17 we used examples\ble_peripheral\ble_app_beacon\pca10040\s132\armgcc\ble_app_beacon_gcc_nrf52.ld.
                 * This makes sure all the advanced sections, such as .sdh_ble_observers, are included.
                 * Save the file under <SDK>\modules\nrfx\mdk\nrf_full.ld and the auto-generated scripts will successfully use it.
                 */

                throw new Exception($"Missing {commonLinkerScriptFile}. Please create it manually.");
            }
        }

        public const string CommonLinkerScriptFileName = "nrf_full.ld";

        Dictionary<string, string> _UnrecognizedTargets = new Dictionary<string, string>();

        private QualifiedLinkerScript QualifyLinkerScript(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (Path.GetFileName(dir).ToLower() != "armgcc")
                return default;

            var makefile = Path.Combine(dir, "Makefile");
            if (!File.Exists(makefile))
                return default;

            var softdevice = Path.GetFileName(Path.GetDirectoryName(dir));
            var rgValidSoftdevice = new Regex("^(s[0-9]+|blank)$");

            if (!rgValidSoftdevice.IsMatch(softdevice))
            {
                _UnrecognizedTargets[softdevice] = path;
                return default;
            }

            var board = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(dir)));
            if (!board.StartsWith("pca"))
            {
                _UnrecognizedTargets[board] = path;
                return default;
            }

            var rgTargets = new Regex("TARGETS[ \t]+:=(.*)");
            var rgSingleChip = new Regex("^nrf([0-9]+)_xxaa(|_[0-9a-z]+)$");
            var targets = File.ReadAllLines(makefile).Select(l => rgTargets.Match(l)).Where(m => m.Success).SingleOrDefault()?.Groups[1].Value ?? throw new Exception("Multiple TARGETS lines in " + makefile);

            var chipMatch = rgSingleChip.Match(targets.Trim());
            if (!chipMatch.Success)
                return default;

            return new QualifiedLinkerScript
            {
                BoardName = board,
                DeviceNumber = int.Parse(chipMatch.Groups[1].Value),
                FullPath = path,
                Softdevice = softdevice
            };
        }
    }

    public struct DeviceSummary
    {
        public string ID;
        public int DeviceNumber;
        public SingleMemoryInfo FLASH, RAM;
        public bool HasFPU;
        public string SVDFileName;

        public override string ToString() => ID;
    }

    public class NordicMCUBuilder : MCUBuilder
    {
        public DeviceSummary Summary;
        public List<SoftdeviceDefinition> Softdevices = new List<SoftdeviceDefinition>();

        public NordicMCUBuilder(DeviceSummary summary)
        {
            Summary = summary;

            var rgDevice = new Regex("nrf([0-9]+)_(xx..)");
            var m = rgDevice.Match(summary.ID);
            if (!m.Success)
                throw new Exception("Unknown device ID: " + summary.ID);

            Name = $"nRF{m.Groups[1].Value}_{m.Groups[2].Value.ToUpper()}";
            FlashSize = (int)summary.FLASH.Length / 1024;
            RAMSize = (int)summary.RAM.Length / 1024;
            Core = CortexCore.M4;
            FPU = summary.HasFPU ? FPUType.SP : FPUType.None;
            StartupFile = "$$SYS:BSP_ROOT$$/nRF5x/modules/nrfx/mdk/gcc_startup_nrf$$com.sysprogs.bspoptions.nrf5x.mcu.basename$$.S";
        }
    }

    public struct SingleMemoryInfo
    {
        public ulong Length;
        public ulong Origin;

        public ulong End => Origin + Length;
    }

    public class SoftdeviceDefinition
    {
        public string Name;

        public string LowercaseName => Name.ToLower();

        public NordicMCUBuilder[] MCUs;

        public ulong ReservedRAM, ReservedFLASH;

        public bool HardwareFP => MCUs.Count(m => m.FPU == FPUType.None) == 0;

        public override string ToString() => $"{Name} ({MCUs?.Length} devices)";

        internal static SoftdeviceDefinition Parse(string line, Dictionary<int, NordicMCUBuilder> deviceByNumber)
        {
            int idx = line.IndexOf(":");
            if (idx == -1)
                throw new Exception("Invalid softdevice definition:" + line);

            return new SoftdeviceDefinition
            {
                Name = line.Substring(0, idx).Trim(),
                MCUs = line.Substring(idx + 1).Trim().Split(',')
                .Select(l => l.Trim())
                .Select(l => l.StartsWith("nRF", StringComparison.InvariantCultureIgnoreCase) ? l.Substring(3) : l)
                .Select(l => deviceByNumber[int.Parse(l)])
                .ToArray()
            };

        }
    }

    class LDFileMemoryInfo
    {
        public readonly SingleMemoryInfo FLASH, RAM;
        public readonly string FullPath;
        public readonly string TargetDevice;

        bool _HasBLEObservers, _HasPowerMgt;

        public bool HasAllNecessarySymbols => _HasBLEObservers;// && _HasPowerMgt;

        public override string ToString() => FullPath;

        public LDFileMemoryInfo(string fn)
        {
            FullPath = fn;
            foreach (var line in File.ReadAllLines(fn))
            {
                if (line.Contains("__stop_sdh_ble_observers"))
                    _HasBLEObservers = true;
                if (line.Contains("__start_pwr_mgmt_data"))
                    _HasPowerMgt = true;

                var m = Regex.Match(line, $"^[ \t]*(FLASH|RAM).*ORIGIN[ =]+0x([a-fA-F0-9]+).*LENGTH[ =]+0x([a-fA-F0-9]+)");
                if (m.Success)
                {
                    var info = new SingleMemoryInfo
                    {
                        Origin = ulong.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber),
                        Length = ulong.Parse(m.Groups[3].Value, System.Globalization.NumberStyles.HexNumber)
                    };

                    switch (m.Groups[1].Value)
                    {
                        case "FLASH":
                            FLASH = info;
                            break;
                        case "RAM":
                            RAM = info;
                            break;
                        default:
                            throw new Exception("Unexpected memory: " + m.Groups[1].Value);
                    }
                }
            }

            if (FLASH.Length == 0)
                throw new Exception("Missing FLASH in " + fn);
            if (RAM.Length == 0 || RAM.Origin == 0)
                throw new Exception("Missing RAM in " + fn);

            var makefile = Path.Combine(Path.GetDirectoryName(fn), "Makefile");
            Regex rgTargets = new Regex("TARGETS[ \t]*:=[ \t]*(.*)");
            Regex rgSingleTarget = new Regex("^(nrf[0-9]+)_[a-z0-9_]+$");
            if (File.Exists(makefile))
            {
                foreach (var line in File.ReadAllLines(makefile))
                {
                    var m = rgTargets.Match(line);
                    if (m.Success)
                    {
                        var targets = m.Groups[1].Value.Trim();
                        m = rgSingleTarget.Match(targets);
                        if (!m.Success)
                            throw new Exception("Multiple targets found in " + makefile);

                        TargetDevice = m.Groups[1].Value;
                    }
                }

                if (TargetDevice == null)
                    throw new Exception("No targets defined in " + makefile);
            }
        }
    }

}
