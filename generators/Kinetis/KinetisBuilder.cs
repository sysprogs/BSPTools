/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace kinetis_bsp_generator {

    class KinetisBuilder : BSPBuilder {

        private const string STARTUP_FILES_FOLDER = "StartupFiles";
        private const string TRIM_VALUE = "0xFFFFFFFF";
        private const string TRIM_VALUE_NAME = "TRIM_VALUE";

        private const string DEFAULT_ISR_NAME = "DefaultISR";
        private const string VECTOR_TABLE_RELATIVE_PATH = "\\startup\\gcc";
        private const string VECTOR_TABLE_FILE_PATTERN = "*.s";
        private const string SEGGER_FILE = "MCU.csv";
        private const string MANUFACTURER = "Freescale";
        private const string MANUFACTURER_COLUMN = "Manufacturer";
        private const string DEVICE_NAME_COLUMN = "Name";
        private const string DEVICES_FOLDER = @"platform\devices";
        private const string CMSIS_FOLDER = @"platform\cmsis";
        private const string FSL_DEVICE_REGISTERS_FILE = "fsl_device_registers.h";
        private const string SRAM_MEMORY = "SRAM";
        private const string FLASH_MEMORY = "FLASH";
        private const string KSDK_MANIFEST_FILE = "ksdk_manifest.xml";

        private static readonly Dictionary<string, string> LdMemoryNameToBSPName = new Dictionary<string, string> {
            { "text", FLASH_MEMORY },
            { "interrupts", "FLASH_Interrupts" },
            { "flash_config", "FLASH_Security" },
            { "data", SRAM_MEMORY },
            { "data_2", SRAM_MEMORY }
        };

        private readonly Dictionary<string, MemoryLayout> _mcuMemoryLayouts = new Dictionary<string, MemoryLayout>();

        private List<MCUBuilder> _mcuBuilders;
        private List<MCUFamilyBuilder> _mcuFamilyBuilders;
        private MCUBuilder[] _rejectedMCUs;
        private MCUFamilyBuilder[] _rejectedMCUFamilies;
        private List<EmbeddedFramework> _frameworks = new List<EmbeddedFramework>();
        private List<string> _exampleDirs = new List<string>();
        private List<MCU> _mcus = new List<MCU>();
        private List<MCUFamily> _mcuFamilies = new List<MCUFamily>();
        private ToolFlags _flags = new ToolFlags();
        private List<string> _projectFiles = new List<string>();
        private bool _parsePeripheralRegisters = true;
        private MCUFamilyBuilder _commonPseudofamily;
        private readonly Dictionary<string, List<string>> _familyToMCUs = new Dictionary<string, List<string>>();

        public KinetisBuilder(BSPDirectories dirs, bool parsePeripheralRegisters) : base(dirs) {
            ShortName = "Kinetis";
            LDSTemplate = XmlTools.LoadObject<LinkerScriptTemplate>(dirs.RulesDir + @"\Kinetis.ldsx");
            _parsePeripheralRegisters = parsePeripheralRegisters;
        }

        public void GeneratePackage() {

            Console.Write("Creating a list of MCUs... ");
            CreateMCUBuilders();
            Console.WriteLine("done");
            Console.WriteLine("Number of MCUs: {0}", _mcuBuilders.Count);

            Console.Write("Creating a list of MCU families... ");
            CreateMCUFamilyBuilders();
            Console.WriteLine("done");
            Console.WriteLine("Number of MCU families: {0}", _mcuFamilyBuilders.Count);

            Console.Write("Assigning MCUs to MCU families... ");
            AssignMCUsToFamilies();
            Console.WriteLine("done");
            Console.WriteLine("{0} MCU families have no MCUs and will be discarded: {1}", _rejectedMCUFamilies.Length, string.Join(",", _rejectedMCUFamilies.Select(mf => mf.Definition.Name)));
            Console.WriteLine("{0} MCUs were not assigned to any family and will be discarded: {1}", _rejectedMCUs.Length, string.Join(",", _rejectedMCUs.Select(m => m.Name)));

            Console.Write("Processing common files... ");
            ProcessCommonFiles();
            Console.WriteLine("done");

            Console.Write("Generating MCUs and their families... ");
            GenerateMCUsAndMCUFamilies();
            Console.WriteLine("done");

            Console.Write("Reading MCUs listed in Segger lists... ");
            var mcusFromSeggerFile = new HashSet<string>(ReadSeggerMCUs(Directories.RulesDir + "\\" + SEGGER_FILE, MANUFACTURER));
            Console.WriteLine("done");            

            Console.Write("Generating BSP... ");
            BoardSupportPackage bsp = new BoardSupportPackage {
                PackageID = "com.sysprogs.arm.freescale.kinetis",
                PackageDescription = "Freescale Kinetis Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "kinetis.mak",
                MCUFamilies = _mcuFamilies.ToArray(),
                SupportedMCUs = _mcus.ToArray(),
                Frameworks = _frameworks.ToArray(),
                Examples = _exampleDirs.ToArray(),
                FileConditions = MatchedFileConditions.ToArray(),
                PackageVersion = "1.4"
            };

            Save(bsp, true);
            Console.WriteLine("done");

            var mcusUnlistedInSeggerFile = new List<string>();
            foreach (var mcu in _mcus) {
                var generalizedMCUName = MCUNameToGeneralizedMCUName(mcu.ID);
                if (!mcusFromSeggerFile.Contains(generalizedMCUName)) {
                    mcusUnlistedInSeggerFile.Add(generalizedMCUName);
                }
            }

            Console.WriteLine("Generated MCU definitions: {0}\r\nGenerated families: {1}\r\nMCUs unlisted in Segger lists: {2}", _mcus.Count, _mcuFamilies.Count, mcusUnlistedInSeggerFile.Count);
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        private List<string> ReadSeggerMCUs(string file, string manufacturer) {
            int manufacturerColumn = -1;
            int deviceNameColumn = -1;
            var headerLine = true;
            var seggerMCUs = new List<string>();

            foreach (var line in File.ReadAllLines(file)) {
                string[] items = line.Split(';');
                if (headerLine) {
                    for (int i = 0; i < items.Length; i++) {
                        if (items[i] == MANUFACTURER_COLUMN) {
                            manufacturerColumn = i;
                        } else if (items[i] == DEVICE_NAME_COLUMN) {
                            deviceNameColumn = i;
                        } 
                    }
                    headerLine = false;
                } else {
                    if (items[manufacturerColumn] != manufacturer)
                        continue;
                    if (items[deviceNameColumn].IndexOf(' ') != -1)
                        continue;
                    seggerMCUs.Add(items[deviceNameColumn]);
                }
            }
            return seggerMCUs;
        }

        private void CreateMCUBuilders() {            
            Regex rgCpu = new Regex("defined\\(CPU_(MK[^\\)]+)\\)");
            var sdkDevices = rgCpu.Matches(File.ReadAllText(string.Format("{0}\\{1}\\{2}", Directories.InputDir, DEVICES_FOLDER, FSL_DEVICE_REGISTERS_FILE))).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
            var mcuBuilders = new List<MCUBuilder>();

            foreach (var sdkDevice in sdkDevices) {
                mcuBuilders.Add(new MCUBuilder { Name = sdkDevice });
            }

            _mcuBuilders = mcuBuilders;
        }

        private void CreateMCUFamilyBuilders() {
            var mcuFamilyBuilders = new List<MCUFamilyBuilder>();

            foreach (var deviceDirectory in Directory.GetDirectories(Directories.InputDir + "\\" + DEVICES_FOLDER)) {
                var deviceName = deviceDirectory.Substring(deviceDirectory.LastIndexOf(Path.DirectorySeparatorChar) + 1);

                if (deviceName[0] != 'M') {
                    throw new Exception("Unexpected device name");
                }

                var mcuFamilyName = deviceName.Substring(1);
                var familyDefinition = new FamilyDefinition();
                familyDefinition.Name = mcuFamilyName;
                familyDefinition.FamilySubdirectory = mcuFamilyName;
                familyDefinition.PrimaryHeaderDir = deviceDirectory;
                familyDefinition.Subfamilies = new MCUClassifier[0];
                mcuFamilyBuilders.Add(new MCUFamilyBuilder(this, familyDefinition));
            }

            _mcuFamilyBuilders = mcuFamilyBuilders;
        }

        private Dictionary<string, CortexCore> ReadMcuFamilyCores() {
            var dict = new Dictionary<string, CortexCore>();
            foreach (var device in XDocument.Load(Directories.InputDir + "\\" + KSDK_MANIFEST_FILE).Descendants("device")) {
                var deviceName = device.Attribute("name").Value.Substring(1);
                var deviceCore = (CortexCore)Enum.Parse(typeof(CortexCore), device.Element("core").Attribute("name").Value.Substring(1), true);
                if (dict.ContainsKey(deviceName) && dict[deviceName] != deviceCore) {
                    throw new Exception(string.Format("{0} has different core types in manifest", deviceName));
                }
                if (!dict.ContainsKey(deviceName))
                {
                    dict.Add(deviceName, deviceCore);
                }
            }
            return dict;
        }

        private void AssignMCUsToFamilies() {
            var assignedMCUs = new List<MCUBuilder>();
            var mcuNamePatterns = new HashSet<string>();
            var familyCores = ReadMcuFamilyCores();

            foreach (var mcuFamilyBuilder in _mcuFamilyBuilders) {

                var ldFiles = Directory.GetFiles(mcuFamilyBuilder.Definition.PrimaryHeaderDir, "*.ld", SearchOption.AllDirectories);
                _familyToMCUs.Add(mcuFamilyBuilder.Definition.Name, new List<string>());

                foreach (var ldFile in ldFiles) {

                    var ldFileName = ldFile.Substring(ldFile.LastIndexOf(Path.DirectorySeparatorChar) + 1);
                    var generalizedMCUName = ldFileName.Split('_')[0];
                    var mcuNamePattern = generalizedMCUName.Replace('x', '.');

                    if (!mcuNamePatterns.Contains(mcuNamePattern)) {

                        mcuNamePatterns.Add(mcuNamePattern);
                        var mcuFamilyMemoryLayout = ReadMemoryLayout(ldFile);

                        for (int i = 0; i < _mcuBuilders.Count; i++) {
                            var mcu = _mcuBuilders[i];

                            if (mcu == null)
                                continue;

                            if (Regex.Match(mcu.Name, mcuNamePattern).Success) {
                                var mcuMemoryLayout = mcuFamilyMemoryLayout.Clone();
                                mcuMemoryLayout.DeviceName = generalizedMCUName;
                                _mcuMemoryLayouts.Add(mcu.Name, mcuMemoryLayout);
                                mcu.RAMSize = (int)mcuMemoryLayout.Memories.First(mem => mem.Name == SRAM_MEMORY).Size;
                                mcu.FlashSize = (int)mcuMemoryLayout.Memories.First(mem => mem.Name == FLASH_MEMORY).Size;
                                mcu.Core = familyCores[mcuFamilyBuilder.Definition.Name];
                                mcuFamilyBuilder.MCUs.Add(mcu);
                                _familyToMCUs[mcuFamilyBuilder.Definition.Name].Add(mcu.Name);
                                assignedMCUs.Add(mcu);
                                _mcuBuilders[i] = null;
                            }
                        }
                    }
                }
            }

            _rejectedMCUFamilies = _mcuFamilyBuilders.Where(mfb => mfb.MCUs.Count == 0).ToArray();
            _mcuFamilyBuilders.RemoveAll(mfb => mfb.MCUs.Count == 0);
            _rejectedMCUs = _mcuBuilders.Where(mb => mb != null).ToArray();
            _mcuBuilders = assignedMCUs;
        }

        private MemoryLayout ReadMemoryLayout(string ldFile) {
            var memoryLayout = new MemoryLayout();
            memoryLayout.Memories = new List<Memory>();
            var rgMemory = new Regex(@"\s*m_([\w]+)\s*\([RWX]+\)\s*:\s*ORIGIN\s*=\s*0x([0-9a-fA-F]+)\s*,\s*LENGTH\s*=\s*0x([0-9a-fA-F]+)");

            foreach (var match in rgMemory.Matches(File.ReadAllText(ldFile)).Cast<Match>()) {
                string name;
                if (!LdMemoryNameToBSPName.TryGetValue(match.Groups[1].Value, out name)) {
                    continue;
                }
                Memory memory = null;
                if (name == "SRAM") {
                    memory = memoryLayout.Memories.FirstOrDefault(mem => mem.Name == SRAM_MEMORY);
                    if (memory != null) {
                        memory.Size += uint.Parse(match.Groups[3].Value, System.Globalization.NumberStyles.HexNumber);
                    }
                }

                if (memory == null) {
                    memory = new Memory {
                        Name = name,
                        Start = uint.Parse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber),
                        Size = uint.Parse(match.Groups[3].Value, System.Globalization.NumberStyles.HexNumber),
                        Access = MemoryAccess.Undefined,
                        Type = name == SRAM_MEMORY ? MemoryType.RAM : MemoryType.FLASH
                    };

                    if (memory.Name == FLASH_MEMORY) {
                        memoryLayout.Memories.Insert(0, memory);
                    } else {
                        memoryLayout.Memories.Add(memory);
                    }
                }
            }

            return memoryLayout;
        }

        private void ProcessCommonFiles() {
            _commonPseudofamily = new MCUFamilyBuilder(this, XmlTools.LoadObject<FamilyDefinition>(Directories.RulesDir + @"\CommonFiles.xml"));            
            _commonPseudofamily.CopyFamilyFiles(ref _flags, _projectFiles);

            var includeDirs = new Dictionary<string, List<string>>();
            var sourceFiles = new Dictionary<string, List<string>>();
            var headerFiles = new Dictionary<string, List<string>>();

            if (_commonPseudofamily.Definition.AdditionalFrameworks != null) {
                foreach (var framework in _commonPseudofamily.Definition.AdditionalFrameworks) {
                    GenerateFrameworkDefinition(framework, includeDirs, sourceFiles, headerFiles);
                }
            }

            foreach (var fw in _commonPseudofamily.GenerateFrameworkDefinitions()) {
                fw.AdditionalIncludeDirs = includeDirs[fw.ID].ToArray();
                fw.AdditionalHeaderFiles = headerFiles[fw.ID].ToArray();
                fw.AdditionalSourceFiles = sourceFiles[fw.ID].ToArray();
                _frameworks.Add(fw);
            }

            foreach (var sample in _commonPseudofamily.CopySamples()) {
                _exampleDirs.Add(sample);
            }
        }

        private void GenerateFrameworkDefinition(
            Framework framework, 
            Dictionary<string, List<string>> frameworkIncludeDirs, 
            Dictionary<string, List<string>> frameworkSourceFiles,
            Dictionary<string, List<string>> frameworkHeaderFiles) {

            if (framework.CopyJobs.Length < 1 || framework.CopyJobs.Length > 1) {
                throw new Exception("Expected one CopyJob object for a framework");
            }

            var fwPath = string.Format("{0}\\lib\\ksdk_{1}_lib\\armgcc", Directories.InputDir, framework.ID.Substring(framework.ID.LastIndexOf(".") + 1));
            var cmakeFiles = Directory.GetFiles(fwPath, "CMakeLists.txt", SearchOption.AllDirectories);            
            var filesToFamilies = new Dictionary<string, HashSet<string>>();
            var families = new List<string>();
            var copyPaths = new HashSet<string>();
            var allFiles = new HashSet<string>();

            foreach (var cmakeFile in cmakeFiles) {
                var splitPath = cmakeFile.Split(Path.DirectorySeparatorChar);
                var family = splitPath[splitPath.Length - 2];
                families.Add(family);
                var contents = File.ReadAllText(cmakeFile);
                var includeDirs = Regex.Matches(contents, @"INCLUDE_DIRECTORIES\(\${ProjDirPath}\/..\/..\/..\/..\/([\w\/]+)\)").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                var sourceFiles = Regex.Matches(contents, @"\${ProjDirPath}\/..\/..\/..\/..\/([\w\/.]+)").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                var allFamilyFiles = sourceFiles.Concat(includeDirs).Where(
                    f => 
                    {
                        var t = f.ToLower();
                        return !t.Contains(DEVICES_FOLDER) && !t.Contains(CMSIS_FOLDER) && !t.Contains("iar") && !t.Contains("realview") && !t.Contains("mdk");
                    }).ToArray();

                allFiles.UnionWith(allFamilyFiles);

                foreach (var file in allFamilyFiles) {
                    var filePath = file;

                    if (file.EndsWith(".h") || file.EndsWith(".c") || file.EndsWith(".txt")) {
                        filePath = file.Substring(0, file.LastIndexOf('/'));
                    }

                    filePath = filePath.Replace("/", "\\");
                    filePath += "\\";
                    copyPaths.Add(filePath + "*.c");
                    copyPaths.Add(filePath + "*.h");
                }                

                foreach (var file in allFamilyFiles.Select(f => f.Replace("/", "\\\\"))) {
                    if (!filesToFamilies.ContainsKey(file)) {
                        filesToFamilies.Add(file, new HashSet<string>());
                    }
                    filesToFamilies[file].Add(family);
                }
            }

            framework.CopyJobs[0].FilesToCopy = string.Join(";", copyPaths);
            var allSourceFiles = new HashSet<string>(allFiles.Where(f => f.EndsWith(".c")).Select(f => "$$SYS:BSP_ROOT$$/" + f)).ToList();
            var allHeaderFiles = new HashSet<string>(allFiles.Where(f => f.EndsWith(".h")).Select(f => "$$SYS:BSP_ROOT$$/" + f)).ToList();

            var dirs = allFiles.Where(f => !f.EndsWith(".h") && !f.EndsWith(".c")).Select(f => "$$SYS:BSP_ROOT$$/" + f).ToArray();
            var allIncludeDirs = new HashSet<string>();
            for (int i = 0; i < dirs.Length; i++) {
                var familyFound = false;
                foreach (var fam in families) {
                    if (dirs[i].Contains(fam)) {
                        familyFound = true;
                        allIncludeDirs.Add(dirs[i].Replace(fam, "$$SYS:FAMILY_ID$$"));
                        break;
                    }
                }
                if (!familyFound) {
                    allIncludeDirs.Add(dirs[i]);
                }
            }           
            
            var frameworkMCUs = new List<string>();          
            foreach (var familyName in families) {
                frameworkMCUs.AddRange(_familyToMCUs[familyName]);
            }

            framework.Filter = string.Join("|", frameworkMCUs);
            frameworkIncludeDirs.Add(framework.ID, allIncludeDirs.ToList());
            frameworkSourceFiles.Add(framework.ID, allSourceFiles);
            frameworkHeaderFiles.Add(framework.ID, allHeaderFiles);

            var fileConditions = new Dictionary<string, string>();
            foreach (var fileFamilies in filesToFamilies) {
                var fcKey = fileFamilies.Key;
                if (fileFamilies.Value.Count == 1) {
                    fileConditions.Add(fcKey, fileFamilies.Value.First());
                }
                else if (fileFamilies.Value.Count < families.Count) {
                    fileConditions.Add(fcKey, string.Join("|", fileFamilies.Value));
                }
            }
             
            framework.CopyJobs[0].SimpleFileConditions = fileConditions.Select(kv => string.Format("{0}: $$SYS:FAMILY_ID$$ =~ {1}", kv.Key, kv.Value)).ToArray();
        }

        private void GenerateMCUsAndMCUFamilies() {

            foreach (var mcuFamilyBuilder in _mcuFamilyBuilders) {
                var vectorTables = new List<StartupFileGenerator.InterruptVector[]>();
                var vectorTableFiles = Directory.GetFiles(
                    mcuFamilyBuilder.Definition.PrimaryHeaderDir + VECTOR_TABLE_RELATIVE_PATH,
                    VECTOR_TABLE_FILE_PATTERN);

                foreach (var vectorTableFile in vectorTableFiles) {
                    StartupFileGenerator.InterruptVector[] vectorTable = null;
                    vectorTable = StartupFileGenerator.ParseInterruptVectors(
                            vectorTableFile,
                            @"^[ \t]*__isr_vector[ \t]*:",
                            @"^[ \t]*.size[ \t]+__isr_vector",
                            @"^[ \t]*.long[ \t]+(\w+)[ \t]*(\/\*[\s]*[\w \t\(,';\-\/\)]*[\s]*\*\/)",
                            null,
                            @"^[ \t\r\n]+(\/\*[ \w]+\*\/)?$",
                            null,
                            1,
                            2);

                    if (vectorTable.Length > 0) {
                        var defaultISRNumber = 0;
                        foreach (var vector in vectorTable) {
                            if (vector == null) {
                                continue;
                            }
                            if (vector.Name == DEFAULT_ISR_NAME) {
                                vector.Name = DEFAULT_ISR_NAME + defaultISRNumber++;
                            } else if (vector.Name == TRIM_VALUE) {
                                vector.SpecialVectorValue = TRIM_VALUE;
                                vector.Name = TRIM_VALUE_NAME;
                            }
                        }
                        vectorTables.Add(vectorTable);
                    }
                }

                if (vectorTables.Count == 0) {
                    throw new Exception("Didn't find any vector table");
                }

                CheckVectorTables(vectorTables);
                Directory.CreateDirectory(Path.Combine(Directories.OutputDir, mcuFamilyBuilder.FamilyFilePrefix));
                GenerateStartupFiles(mcuFamilyBuilder, vectorTables[0]);

                if (_parsePeripheralRegisters) {
                    var headerFiles = Directory.GetFiles(mcuFamilyBuilder.Definition.PrimaryHeaderDir + "/include", "*.h");
                    var familyHeaderFiles = headerFiles
                        .Where(file => file.Substring(file.LastIndexOf(Path.DirectorySeparatorChar) + 1) == "M" + mcuFamilyBuilder.Definition.Name + ".h").ToArray();

                    if (familyHeaderFiles.Length == 0) {
                        throw new Exception("No header file found for MCU family");
                    } else if (familyHeaderFiles.Length > 1) {
                        throw new Exception("Only one header file expected for MCU family");
                    }

                    mcuFamilyBuilder.AttachPeripheralRegisters(new[]
                    {
                        new MCUDefinitionWithPredicate
                        {
                            MCUName = mcuFamilyBuilder.Definition.Name,
                            RegisterSets = PeripheralRegisterGenerator.GenerateFamilyPeripheralRegisters(familyHeaderFiles[0]),
                            MatchPredicate = m => true
                        }
                    });
                }

                var famObj = mcuFamilyBuilder.GenerateFamilyObject(true);
                famObj.ConfigurableProperties.PropertyGroups[0].Properties.Add(
                        new PropertyEntry.Boolean {
                            Name = "Disable Watchdog",
                            UniqueID = "com.sysprogs.bspoptions.wdog",
                            ValueForTrue = "DISABLE_WDOG",
                            DefaultValue = true
                        }
                    );


                famObj.AdditionalSourceFiles = new string[] {
                    "$$SYS:BSP_ROOT$$/" + mcuFamilyBuilder.FamilyFilePrefix + STARTUP_FILES_FOLDER + "/startup.c",
                    "$$SYS:BSP_ROOT$$/" + mcuFamilyBuilder.FamilyFilePrefix + STARTUP_FILES_FOLDER + "/vectors_" + mcuFamilyBuilder.Definition.Name + ".c" };

                var deviceSpecificFiles = _projectFiles.Where(file => file.Contains("devices") || file.Contains("CMSIS"));
                famObj.AdditionalSourceFiles = LoadedBSP.Combine(famObj.AdditionalSourceFiles, deviceSpecificFiles.Where(f => f.Contains(famObj.ID) && !MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                famObj.AdditionalHeaderFiles = LoadedBSP.Combine(famObj.AdditionalHeaderFiles, deviceSpecificFiles.
                    Where(f => (f.Contains(famObj.ID) || f.Contains("CMSIS") || f.Contains("fsl_device_registers.h")) && MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                famObj.AdditionalSystemVars = LoadedBSP.Combine(famObj.AdditionalSystemVars, _commonPseudofamily.Definition.AdditionalSystemVars);
                famObj.CompilationFlags = famObj.CompilationFlags.Merge(_flags);
                famObj.CompilationFlags.IncludeDirectories = new HashSet<string>(famObj.AdditionalSourceFiles.Concat(famObj.AdditionalHeaderFiles).Select(f => f.Substring(0, f.LastIndexOf("/")))).ToArray();
                _mcuFamilies.Add(famObj);
                GenerateLinkerScripts(mcuFamilyBuilder);

                foreach (var mcuBuilder in mcuFamilyBuilder.MCUs) {
                    mcuBuilder.StartupFile = "$$SYS:BSP_ROOT$$/" + mcuFamilyBuilder.FamilyFilePrefix + STARTUP_FILES_FOLDER + "/startup.c";
                    var mcu = mcuBuilder.GenerateDefinition(mcuFamilyBuilder, mcuFamilyBuilder.BSP, _parsePeripheralRegisters);
                    var preprocessorMacroses = mcu.CompilationFlags.PreprocessorMacros.ToList();
                    preprocessorMacroses.Add("CPU_" + mcu.ID);
                    mcu.CompilationFlags.PreprocessorMacros = preprocessorMacroses.ToArray();
                    _mcus.Add(mcu);
                }

                foreach (var fw in mcuFamilyBuilder.GenerateFrameworkDefinitions()) {
                    _frameworks.Add(fw);
                }

                foreach (var sample in mcuFamilyBuilder.CopySamples()) {
                    _exampleDirs.Add(sample);
                }
            }
        }

        private static void CheckVectorTables(List<StartupFileGenerator.InterruptVector[]> vectorTables) {
            var firstVectorTable = vectorTables[0];
            if (vectorTables.Count > 1) {
                for (int i = 1; i < vectorTables.Count; i++) {
                    if (vectorTables[i].Length != firstVectorTable.Length) {
                        throw new Exception("Different vector tables");
                    }

                    for (int j = 0; j < vectorTables[i].Length; j++) {
                        if (firstVectorTable[j].OptionalComment != vectorTables[i][j].OptionalComment) {
                            throw new Exception("Different comments for a vector table entry");
                        }
                    }
                }
            }
        }

        private void GenerateLinkerScripts(MCUFamilyBuilder mcuFamilyBuilder) {

            string ldsDirectory = Path.Combine(BSPRoot, mcuFamilyBuilder.Definition.FamilySubdirectory, "LinkerScripts");
            Directory.CreateDirectory(ldsDirectory);

            foreach (var mcu in mcuFamilyBuilder.MCUs) {
                var layout = GetMemoryLayout(mcu, mcuFamilyBuilder);
                GenerateLinkerScriptsAndUpdateMCU(ldsDirectory, mcuFamilyBuilder.FamilyFilePrefix, mcu, layout, layout.DeviceName);
            }
        }

        private void GenerateStartupFiles(MCUFamilyBuilder mcuFamilyBuilder, StartupFileGenerator.InterruptVector[] vectorTable) {

            var startupFilesPath = Path.Combine(BSPRoot, mcuFamilyBuilder.Definition.FamilySubdirectory, STARTUP_FILES_FOLDER);
            Directory.CreateDirectory(startupFilesPath);
            File.Copy(Directories.RulesDir + "/" + "startup.c", startupFilesPath + "/" + "startup.c", true);

            using (var fs = File.CreateText(string.Format("{0}\\vectors_{1}.c", startupFilesPath, mcuFamilyBuilder.Definition.Name))) {
                fs.WriteLine("/*");
                fs.WriteLine("\tThis file contains the definitions of the interrupt handlers for {0} MCU family.", mcuFamilyBuilder.Definition.Name);
                fs.WriteLine("\tThe file is provided by Sysprogs under the BSD license.", mcuFamilyBuilder.Definition.Name);
                fs.WriteLine("*/");
                fs.WriteLine("");
                fs.WriteLine("");
                fs.WriteLine("extern void *_estack;");
                fs.WriteLine("#define NULL ((void *)0)");
                fs.WriteLine("#define {0} ((void *){1})", TRIM_VALUE_NAME, TRIM_VALUE);
                fs.WriteLine("");
                fs.WriteLine("void Reset_Handler();");
                fs.WriteLine("void Default_Handler();");
                fs.WriteLine("");

                List<string> tableContents = new List<string>();

                for (int i = 2; i < vectorTable.Length; i++) {
                    var interrupt = vectorTable[i];
                    if (interrupt != null) {
                        if (interrupt.Name != TRIM_VALUE_NAME) {
                            fs.WriteLine("void {0}() __attribute__ ((weak, alias (\"Default_Handler\")));", interrupt.Name);
                            tableContents.Add(string.Format("&{0}", interrupt.Name));
                        } else {
                            tableContents.Add(string.Format("{0}", interrupt.Name));
                        }

                    } else {
                        tableContents.Add("NULL");
                    }
                }

                fs.WriteLine("");
                fs.WriteLine("void * __vect_table[0x{0:x}] __attribute__ ((section (\".vectortable\"))) = ", vectorTable.Length);
                fs.WriteLine("{");
                fs.WriteLine("\t&_estack,");
                fs.WriteLine("\t&Reset_Handler,");
                for (int i = 0; i < tableContents.Count; i++) {
                    string comma = (i == tableContents.Count - 1) ? "" : ",";
                    fs.WriteLine("\t{0}{1}", tableContents[i], comma);
                }
                fs.WriteLine("};");
                fs.WriteLine("");
                fs.WriteLine("void Default_Handler()");
                fs.WriteLine("{");
                fs.WriteLine("\tasm(\"BKPT 255\");");
                fs.WriteLine("}");
            }            
        }

        const uint FLASHBase = 0x410;
        const uint SRAMBase = 0x20000000;

        public override void GetMemoryBases(out uint flashBase, out uint ramBase) {
            flashBase = FLASHBase;
            ramBase = SRAMBase;
        }

        public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family) {
            return _mcuMemoryLayouts[mcu.Name];            
        }
                
        private uint GetSramStart(MCUBuilder mcu) {
            return _mcuMemoryLayouts[mcu.Name].Memories.Where(mem => mem.Name == "SRAM").First().Start;            
        }

        private static string MCUNameToGeneralizedMCUName(string mcu) {
            if (mcu.EndsWith("R"))
                mcu = mcu.Substring(0, mcu.Length - 1);
            char[] chars = mcu.ToCharArray();
            int i;

            if (chars[0] == 'P')
                chars[0] = 'M';

            for (i = chars.Length - 1; i >= 0; i--)
                if (chars[i] < '0' || chars[i] > '9')
                    break;
            if (i < 3)
                throw new Exception("Wrong MCU name: " + mcu);

            if (chars[i - 2] == 'Z')
                chars[i - 1] = chars[i] = 'x';
            else
                chars[i - 2] = chars[i - 1] = chars[i] = 'x';

            string result = new string(chars);
            if (result.Contains("xx") && !result.Contains("xxx"))
                result = result.Replace("xx", "xxx");
            return result;
        }
    }
}
