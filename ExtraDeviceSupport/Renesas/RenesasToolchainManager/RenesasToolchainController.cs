using BSPEngine;
using Microsoft.Win32;
using RenesasToolchainManager.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RenesasToolchainManager
{
    class RenesasToolchainController
    {
        public class RenesasToolchain : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;

            #region Model
            bool _IsIntegrated;
            public bool IsIntegrated
            {
                get => _IsIntegrated;
                set
                {
                    _IsIntegrated = value;
                    OnPropertyChanged(nameof(IsIntegrated));
                }
            }

            bool _CanEdit = true;
            public bool CanEdit
            {
                get => _CanEdit;
                set
                {
                    _CanEdit = value;
                    OnPropertyChanged(nameof(CanEdit));
                }
            }

            double _Progress;
            public double Progress
            {
                get => _Progress;
                set
                {
                    _Progress = value;
                    OnPropertyChanged(nameof(Progress));
                }
            }

            string _GCCPath;
            public string GCCPath
            {
                get => _GCCPath;
                set
                {
                    _GCCPath = value;
                    OnPropertyChanged(nameof(GCCPath));
                }
            }

            string _Status;
            public string Status
            {
                get => _Status;
                set
                {
                    _Status = value;
                    OnPropertyChanged(nameof(Status));
                }
            }


            string _E2StudioPath;

            public string E2StudioPath
            {
                get => _E2StudioPath;
                set
                {
                    _E2StudioPath = value;
                    OnPropertyChanged(nameof(E2StudioPath));
                }
            }
            #endregion

            void OnPropertyChanged(string name)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }

            string _LinkDir;

            public RenesasToolchain(string linkDir, string toolchainDir, Toolchain tc)
            {
                GCCPath = toolchainDir;
                _LinkDir = linkDir;
                IsIntegrated = true;
            }

            public RenesasToolchain(string gccFromRegistry, string e2StudioFromRegistry)
            {
                GCCPath = gccFromRegistry;
                E2StudioPath = e2StudioFromRegistry;
                IsIntegrated = false;
            }

            const string BSPDirectoryName = "SysprogsBSP";

            void BuildBSP(string target)
            {
                List<MCU> mcus = new List<MCU>();
                Status = "Copying device-specific files...";

                string linkerScriptDir = Path.Combine(E2StudioPath, @"internal\projectgen\rl78\rl78_GccLinkerScripts");
                if (!Directory.Exists(linkerScriptDir))
                    throw new Exception("Missing " + linkerScriptDir);

                string bspDir = Path.Combine(GCCPath, BSPDirectoryName);
                Directory.CreateDirectory(Path.Combine(bspDir, "LinkerScripts"));

                var linkerScripts = Directory.GetFiles(linkerScriptDir, "*.ld");
                int i = 0;

                string generatorResourceDir = Path.Combine(E2StudioPath, @"internal\projectgen\rl78\Generate");
                File.Copy(Path.Combine(generatorResourceDir, @"resetprg\reset_program.asm"), Path.Combine(bspDir, "start.S"), true);
                File.WriteAllText(Path.Combine(bspDir, "stubs.c"), "void __attribute__((weak)) HardwareSetup(void)\r\n{\r\n}\r\n");

                string debugComponentDir = Path.Combine(E2StudioPath, "DebugComp", "RL78");
                Directory.CreateDirectory(Path.Combine(bspDir, "DeviceDefinitions"));

                foreach (var fn in linkerScripts)
                {
                    Progress = (double)i++ / linkerScripts.Length;
                    var mcu = RenesasMCUGenerator.GenerateMCUDefinition(bspDir, fn, generatorResourceDir, target, debugComponentDir);
                    if (mcu != null)
                        mcus.Add(mcu);
                }

                string debugPackageDir = Path.Combine(bspDir, "DebugPackage");
                Directory.CreateDirectory(debugPackageDir);
                File.WriteAllText(Path.Combine(debugPackageDir, "DebugCompLink.txt"), debugComponentDir);

                EmbeddedDebugPackage edp = new EmbeddedDebugPackage
                {
                    PackageID = $"com.sysprogs.renesas.{target}.gdbstub",
                    UserFriendlyName = $"{target} Debug Method package",
                    ExtensionDLL = "RenesasDebugPackage.dll",
                    ExtensionSupportsShadowLoad = true,
                    SupportedDebugMethods = new[]
                    {
                        new DebugMethod
                        {
                            UserFriendlyName = "E2 GDB Stub",
                            ID = "e2_gdbstub",
                            RequireExplicitDisconnect = true,
                            ControllerClass = "RenesasDebugPackage.RenesasDebugController",
                            UseContinueToStart = true,
                            SendCtrlCToGDBServer = false,
                            AutoSelectScore = 100
                        }
                    }
                };

                XmlTools.SaveObject(edp, Path.Combine(debugPackageDir, "edp.xml"));
                var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("RenesasToolchainManager.RenesasDebugPackage.dll") ?? throw new Exception("Could not find the debug package resource");
                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);
                File.WriteAllBytes(Path.Combine(debugPackageDir, "RenesasDebugPackage.dll"), data);

                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = $"com.sysprogs.{target}.built-in",
                    PackageDescription = $"{target} devices",
                    PackageVersion = "1.0",
                    GNUTargetID = target,
                    MCUFamilies = new[]
                    {
                        RenesasMCUGenerator.GenerateMCUFamilyDefinition(target)
                    },
                    SupportedMCUs = mcus.ToArray(),
                    DebugMethodPackages = new[] { "DebugPackage" },
                    Frameworks = new[]
                    {
                        RenesasMCUGenerator.GenerateStartupFilesFramework(target)
                    }
                };

                XmlTools.SaveObject(bsp, Path.Combine(bspDir, "BSP.xml"));
            }

            public void Integrate()
            {
                if (string.IsNullOrEmpty(E2StudioPath) || !Directory.Exists(E2StudioPath))
                    throw new Exception("Missing or invalid E2 Studio directory");
                if (string.IsNullOrEmpty(GCCPath) || !Directory.Exists(GCCPath))
                    throw new Exception("Missing or invalid E2 Studio directory");

                if (Directory.Exists(Path.Combine(GCCPath, BSPDirectoryName)))
                {
                    Status = "Removing previous BSP...";
                    Directory.Delete(Path.Combine(GCCPath, BSPDirectoryName), true);
                }

                Status = "Detecting toolchain version...";

                string gcc = Directory.GetFiles(Path.Combine(GCCPath, "bin"), "*-gcc.exe").FirstOrDefault() ?? throw new Exception("Could not find GCC in " + GCCPath);
                string gdb = Directory.GetFiles(Path.Combine(GCCPath, "bin"), "*-gdb.exe").FirstOrDefault() ?? throw new Exception("Could not find GCC in " + GCCPath);

                var gccVersion = GCCInfoCollector.QueryGCCVersion(gcc);
                var gdbVersion = GCCInfoCollector.QueryGDBVersion(gdb);

                int idx = gcc.IndexOf("-gcc.exe", StringComparison.InvariantCultureIgnoreCase);
                if (idx == -1)
                    throw new Exception("Failed to determine the prefix from the gcc path");

                Status = "Locating Python...";
                string pythonExe = Directory.GetFiles(E2StudioPath, "python27.dll", SearchOption.AllDirectories).FirstOrDefault() ?? throw new Exception("Could not find python27.dll in " + E2StudioPath);
                string pythonDir = Path.GetDirectoryName(pythonExe);

                BuildBSP(gccVersion.Target);

                Toolchain tc = new Toolchain
                {
                    ToolchainName = "Renesas RL78",
                    ToolchainType = ToolchainType.Embedded,
                    GNUTargetID = gccVersion.Target,
                    GCCVersion = gccVersion.Version,
                    GDBVersion = gdbVersion.Version,
                    GDBExecutable = Path.Combine(E2StudioPath, "DebugComp", "RL78", "rl78-elf-gdb.exe"),
                    Make = "$(VISUALGDB_DIR)/make.exe",
                    BinaryDirectory = "bin",
                    ToolchainID = "com.sysprogs.renesas." + gccVersion.Target,
                    Prefix = Path.GetFileName(gcc.Substring(0, idx + 1)),
                    BuiltInBSPs = new[]
                    {
                        BSPDirectoryName
                    },
                    ExtraEnvironment = new[]
                    {
                        $"PATH=%PATH%;{pythonDir}",
                        $"PYTHONHOME={pythonDir}"
                    }
                };

                Status = "Writing configuration files...";
                XmlTools.SaveObject(tc, Path.Combine(GCCPath, "Toolchain.xml"));

                _LinkDir = Path.Combine(_ToolchainProfileDirectory, Guid.NewGuid().ToString());
                Directory.CreateDirectory(_LinkDir);
                File.WriteAllText(Path.Combine(_LinkDir, "ToolchainLink.txt"), GCCPath);

                IsIntegrated = true;
            }

            public void RemoveIntegration()
            {
                Directory.Delete(_LinkDir, true);
                if (Directory.Exists(Path.Combine(GCCPath, BSPDirectoryName)))
                    Directory.Delete(Path.Combine(GCCPath, BSPDirectoryName), true);
                if (File.Exists(Path.Combine(GCCPath, "Toolchain.xml")))
                    File.Delete(Path.Combine(GCCPath, "Toolchain.xml"));
                IsIntegrated = false;
            }
        }

        public ObservableCollection<RenesasToolchain> Toolchains { get; } = new ObservableCollection<RenesasToolchain>();

        static readonly string _ToolchainProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\VisualGDB\ToolchainProfiles\localhost";

        public RenesasToolchainController()
        {
            HashSet<string> discoveredLocations = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var e2StudioFromRegistry = (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Renesas\Renesase2studio")?.GetValue("Path") ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Renesas\Renesase2studio")?.GetValue("Path")) as string;

            if (Directory.Exists(_ToolchainProfileDirectory))
            {
                foreach (var dir in Directory.GetDirectories(_ToolchainProfileDirectory))
                {
                    string linkFile = Path.Combine(dir, "ToolchainLink.txt");
                    if (File.Exists(linkFile))
                    {
                        try
                        {
                            string toolchainDir = File.ReadAllText(linkFile);
                            string toolchainXml = Path.Combine(toolchainDir, "Toolchain.xml");
                            var tc = XmlTools.LoadObject<BSPEngine.Toolchain>(toolchainXml);
                            if (tc.GNUTargetID?.StartsWith("rl78-") == true)
                            {
                                Toolchains.Add(new RenesasToolchain(dir, toolchainDir, tc) { E2StudioPath = e2StudioFromRegistry });
                                discoveredLocations.Add(Path.GetFullPath(toolchainDir));
                            }
                        }
                        catch { }
                    }
                }
            }

            var gccFromRegistry = Registry.CurrentUser.OpenSubKey(@"Software\CyberTHOR Studios GNU Tools")?.GetValue("Path") as string;

            if (!string.IsNullOrEmpty(gccFromRegistry))
                gccFromRegistry = Path.GetDirectoryName(gccFromRegistry);

            if (string.IsNullOrEmpty(gccFromRegistry) || !discoveredLocations.Contains(Path.GetFullPath(gccFromRegistry)))
                Toolchains.Add(new RenesasToolchain(gccFromRegistry, e2StudioFromRegistry));
        }
    }
}
