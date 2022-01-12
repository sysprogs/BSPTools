using BSPEngine;
using OpenOCDPackage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP8266DebugPackage
{
    public abstract class ESPxxDebugController : OpenOCDDebugController
    {
        private readonly bool _IsESP32;

        public override Type[] SettingsObjectTypes => new[] { typeof(ESP32OpenOCDSettings), typeof(ESP8266OpenOCDSettings) };

        public override ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            return new GUI.ESPxxOpenOCDSettingsControl(method, host, this, _IsESP32);
        }

        protected override abstract IGDBStubInstance CreateStub(DebugStartContext context, OpenOCDSettings settings, OpenOCDCommandLine cmdLine, int gdbPort, int telnetPort, string temporaryScript, IExternalToolInstance tool, string sharedSessionID);

        public override string AdapterSpeedCommand => "adapter_khz";

        public override object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration)
        {
            ESPxxOpenOCDSettingsEditor editor = new ESPxxOpenOCDSettingsEditor(host, methodDirectory, null, default(KnownInterfaceInstance), _IsESP32, this);
            string value;
            if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.openocd.iface_script", out value))
                editor.ReplaceScript(OpenOCDCommandLine.ScriptType.Interface, value);
            else
                return null;    //Not an OpenOCD configuration

            editor.RebuildStartupCommands();

            if (editor.FLASHSettings is ESP8266BinaryImage.ESP8266ImageHeader hdr8266)
            {
                if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_size", out value))
                    hdr8266.Size = ESP8266BinaryImage.ParseEnumValue<ESP8266BinaryImage.FLASHSize>(value);
                if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_mode", out value))
                    hdr8266.Mode = ESP8266BinaryImage.ParseEnumValue<ESP8266BinaryImage.FLASHMode>(value);
                if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_freq", out value))
                    hdr8266.Frequency = ESP8266BinaryImage.ParseEnumValue<ESP8266BinaryImage.FLASHFrequency>(value);
            }
            else if (editor.FLASHSettings is ESP8266BinaryImage.ESP32ImageHeader hdr32)
            {
                if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_size", out value))
                    hdr32.Size = ESP8266BinaryImage.ParseEnumValue<ESP8266BinaryImage.ESP32FLASHSize>(value);
                if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_mode", out value))
                    hdr32.Mode = ESP8266BinaryImage.ParseEnumValue<ESP8266BinaryImage.FLASHMode>(value);
                if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_freq", out value))
                    hdr32.Frequency = ESP8266BinaryImage.ParseEnumValue<ESP8266BinaryImage.FLASHFrequency>(value);
            }

            if (!legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.program_flash", out value) || string.IsNullOrEmpty(value))
                editor.ProgramMode = ProgramMode.Disabled;

            return editor.Settings;
        }

        protected ESPxxDebugController(bool isESP32)
        {
            _IsESP32 = isESP32;
        }

        protected override bool SupportsScriptEditing => false;

        
        protected override string GetOpenOCDDirectory(string methodDir)
        {
            if (File.Exists(methodDir + @"\bin\openocd.exe"))
                return methodDir;
            return Path.GetFullPath(Path.Combine(methodDir, @"..\..\..\OpenOCD"));
        }
    }

    public class ESP32DebugController : ESPxxDebugController
    {
        public ESP32DebugController()
            : base(true)
        {
        }

        public class ESP32GDBStub : OpenOCDGDBStub
        {
            private ESP32DebugController _Controller;
            private DebugStartContext _Context;

            public ESP32GDBStub(ESP32DebugController controller, DebugStartContext context, OpenOCDCommandLine cmdLine, IExternalToolInstance tool, ESP32OpenOCDSettings settings, int gdbPort, int telnetPort, string temporaryScript)
                : base(cmdLine, tool, settings, gdbPort, telnetPort, temporaryScript)
            {
                _Controller = controller;
                _Context = context;
            }

            public override ILiveMemoryEvaluator CreateLiveMemoryEvaluator(IDebugStartService service)
            {
                if (service.SystemDictionary.TryGetValue("com.sysprogs.visualgdb.gdb_override", out var value))
                {
                    try
                    {
                        var fn = Path.GetFileName(value);
                        if (fn.StartsWith("riscv", StringComparison.InvariantCultureIgnoreCase))
                        {
                            //ESP32C3 devices are based on RISC-V and do support the live memory engine with the latest ESP-IDF versions.
                            return base.CreateLiveMemoryEvaluator(service);
                        }
                    }
                    catch { }
                }

                return null;
            }

            protected override bool SkipCommandOnAttach(string cmd)
            {
                return base.SkipCommandOnAttach(cmd);
            }

            protected override bool RunLoadCommand(IDebugStartService service, ISimpleGDBSession session, string cmd) => _Controller.LoadFLASH(_Context, service, session, (ESP32OpenOCDSettings)_Settings, this);

            protected override bool ShouldIgnoreErrorLine(string line)
            {
                if (base.ShouldIgnoreErrorLine(line))
                    return true;
                if (line.Contains("virt2phys"))
                    return true;    //Looks to be by design in recent OpenOCD update
                if (line.Contains("No symbols for FreeRTOS"))
                    return true;    //Not relevant

                return false;
            }
        }


        protected override IGDBStubInstance CreateStub(DebugStartContext context, OpenOCDSettings settings, OpenOCDCommandLine cmdLine, int gdbPort, int telnetPort, string temporaryScript, IExternalToolInstance tool, string sharedSessionID)
        {
            return new ESP32GDBStub(this, context, cmdLine, tool, (ESP32OpenOCDSettings)settings, gdbPort, telnetPort, temporaryScript);
        }

        bool LoadFLASH(DebugStartContext context, IDebugStartService service, ISimpleGDBSession session, ESP32OpenOCDSettings settings, ESP32GDBStub stub)
        {
            string val;
            if (!service.SystemDictionary.TryGetValue("com.sysprogs.esp32.load_flash", out val) || val != "1")
            {
                //This is a RAM-only configuration
                return session.RunGDBCommand("load").IsDone;
            }
            else
            {
                if (settings.ProgramFLASHUsingExternalTool)
                {
                    var svc = service.AdvancedDebugService as IExternallyProgrammedProjectDebugService ?? throw new Exception("This project type does not support external FLASH memory programming");
                    svc.ProgramFLASHMemoryUsingExternalTool(settings.ProgramMode);
                }
                else
                {
                    List<ProgrammableRegion> blocks = BuildProgrammableBlocksFromSettings(service, settings);

                    Regex rgFLASHSize = new Regex("Auto-detected flash size ([0-9]+) KB");
                    if (settings.CheckFLASHSize)
                    {
                        var match = stub.Tool.AllText.Split('\n').Select(s => rgFLASHSize.Match(s)).FirstOrDefault(m => m.Success);
                        if (match != null)
                        {
                            int detectedSizeKB = int.Parse(match.Groups[1].Value);
                            int specifiedSizeMB = 0;
                            switch (settings.FLASHSettings.Size)
                            {
                                case ESP8266BinaryImage.ESP32FLASHSize.size1MB:
                                    specifiedSizeMB = 1;
                                    break;
                                case ESP8266BinaryImage.ESP32FLASHSize.size2MB:
                                    specifiedSizeMB = 2;
                                    break;
                                case ESP8266BinaryImage.ESP32FLASHSize.size4MB:
                                    specifiedSizeMB = 4;
                                    break;
                                case ESP8266BinaryImage.ESP32FLASHSize.size8MB:
                                    specifiedSizeMB = 8;
                                    break;
                                case ESP8266BinaryImage.ESP32FLASHSize.size16MB:
                                    specifiedSizeMB = 16;
                                    break;
                            }

                            if (detectedSizeKB < (specifiedSizeMB * 1024) && detectedSizeKB >= 1024)
                            {
                                if (service.GUIService.Prompt($"The FLASH size specified via Project Properties is greater than the actual SPI FLASH size on your device. Please switch FLASH size to {detectedSizeKB / 1024}MB or less.\nDo you want to cancel FLASH programming?", System.Windows.Forms.MessageBoxIcon.Warning))
                                    throw new OperationCanceledException();
                            }
                        }
                    }

                    using (var ctx = session.CreateScopedProgressReporter("Programming FLASH...", new[] { "Programming FLASH memory" }))
                    {
                        int blkNum = 0;
                        foreach (var blk in blocks)
                        {
                            ctx.ReportTaskProgress(blkNum++, blocks.Count);
                            string path = blk.FileName.Replace('\\', '/');
                            if (path.Contains(" "))
                                throw new Exception($"ESP32 OpenOCD does not support spaces in paths. Please relocate {path} to a location without spaces");
                            var result = session.RunGDBCommand($"mon program_esp \"{path}\" 0x{blk.Offset:x}");
                            bool succeeded = result.StubOutput?.FirstOrDefault(l => l.Contains("** Programming Finished **")) != null;
                            if (!succeeded)
                                throw new Exception("FLASH programming failed. Please try unplugging the board and plugging it back. If nothing helps, please review the gdb/OpenOCD logs for details.");
                        }
                    }
                }
            }

            if (!session.RunGDBCommand("mon reset halt").IsDone)
                throw new Exception("Failed to reset target after programming");

            return true;
        }

        public static bool BuildProgrammableBlocksFromSynthesizedESPIDFVariables(IDebugStartService service, out List<ProgrammableRegion> blocks)
        {
            if (service.MCU.Configuration.TryGetValue("com.sysprogs.esp32.esptool.binaries.count", out var tmp) && int.TryParse(tmp, out var binaryCount) && binaryCount > 0)
            {
                blocks = new List<ProgrammableRegion>();
                for (int i = 0; i < binaryCount; i++)
                {
                    string fn = service.MCU.Configuration[$"com.sysprogs.esp32.esptool.binaries[{i}].path"];

                    blocks.Add(new ProgrammableRegion
                    {
                        FileName = fn,
                        Size = File.ReadAllBytes(fn).Length,
                        Offset = int.Parse(service.MCU.Configuration[$"com.sysprogs.esp32.esptool.binaries[{i}].address"])
                    });
                }
                return true;
            }

            blocks = null;
            return false;
        }

        public static List<ProgrammableRegion> BuildProgrammableBlocksFromSettings(IDebugStartService service, IESP32Settings settings)
        {
            List<ProgrammableRegion> blocks;
            if (BuildProgrammableBlocksFromSynthesizedESPIDFVariables(service, out blocks))
            {
                //Nothing to do. Successfully built the block list.
            }
            else
            {
                bool patchBootloader = settings.PatchBootloader;
                blocks = ESP32StartupSequence.BuildFLASHImages(service.TargetPath, service.SystemDictionary, settings.FLASHSettings, patchBootloader);
            }

            if (settings.FLASHResources != null)
                foreach (var r in settings.FLASHResources)
                    if (r.Valid)
                        blocks.Add(r.ToProgrammableRegion(service));
            return blocks;
        }
    }

    public class ESP8266DebugController : ESPxxDebugController
    {
        public ESP8266DebugController()
            : base(false)
        {
        }

        protected override IGDBStubInstance CreateStub(DebugStartContext context, OpenOCDSettings settings, OpenOCDCommandLine cmdLine, int gdbPort, int telnetPort, string temporaryScript, IExternalToolInstance tool, string sharedSessionID)
        {
            return new ESP8266GDBStub(this, context, cmdLine, tool, (ESP8266OpenOCDSettings)settings, gdbPort, telnetPort, temporaryScript);
        }

        public class ESP8266GDBStub : OpenOCDGDBStub
        {
            private ESP8266DebugController _Controller;
            private DebugStartContext _Context;

            public ESP8266GDBStub(ESP8266DebugController controller, DebugStartContext context, OpenOCDCommandLine cmdLine, IExternalToolInstance tool, ESPxxOpenOCDSettings settings, int gdbPort, int telnetPort, string temporaryScript)
                : base(cmdLine, tool, settings, gdbPort, telnetPort, temporaryScript)
            {
                _Controller = controller;
                _Context = context;
            }

            public override ILiveMemoryEvaluator CreateLiveMemoryEvaluator(IDebugStartService service) => null;

            protected override bool SkipCommandOnAttach(string cmd) => throw new NotSupportedException();
            protected override bool RunLoadCommand(IDebugStartService service, ISimpleGDBSession session, string cmd) => throw new NotSupportedException();

            public override void ConnectGDBToStub(IDebugStartService service, ISimpleGDBSession session)
            {
                bool programNow;
                if (_Settings.ProgramFLASHUsingExternalTool)
                    programNow = false;
                else
                {
                    switch (_Settings.ProgramMode)
                    {
                        case ProgramMode.Disabled:
                            programNow = false;
                            break;
                        case ProgramMode.Auto:
                            programNow = !service.IsCurrentFirmwareAlreadyProgrammed();
                            break;
                        default:
                            programNow = true;
                            break;
                    }
                }

                foreach (var cmd in _Settings.StartupCommands)
                {
                    bool isLoad = cmd.Trim() == "load";

                    if (isLoad)
                    {
                        if (service.Mode == EmbeddedDebugMode.Attach)
                        {
                            session.RunGDBCommand("mon halt");
                        }
                        else
                        {
                            var sequence = ESP8266StartupSequence.BuildSequence(service, (ESP8266OpenOCDSettings)_Settings, (l, i) => session.SendInformationalOutput(l), programNow, _Context.Method?.Directory);
                            using (var ctx = session.CreateScopedProgressReporter("Programming FLASH", new string[] { "Programming FLASH..." }))
                            {
                                if (RunSequence(ctx, service, session, sequence))
                                    service.OnFirmwareProgrammedSuccessfully();
                            }
                        }
                    }
                    else
                        session.RunGDBCommand(cmd);
                }
            }
        }

        class RetriableException : Exception
        {
            public RetriableException(string msg)
                : base(msg)
            {
            }
        }

        public static bool RunSequence(ILoadProgressReporter reporter, IDebugStartService service, ISimpleGDBSession session, CustomStartupSequence sequence)
        {
            var espCmds = sequence.Steps;
            if (espCmds != null)
            {
                DateTime startTime = DateTime.Now;

                int total = 0, done = 0;
                foreach (var s in espCmds)
                    total += s.ProgressWeight;

                for (int retry = 0; ; retry++)
                {
                    try
                    {
                        foreach (var s in espCmds)
                        {
                            SimpleGDBCommandResult r = default(SimpleGDBCommandResult);

                            foreach (var scmd in s.Commands)
                            {
                                r = session.RunGDBCommand(scmd);
                            }

                            bool failed = false;
                            if (s.ResultVariable != null)
                            {
                                string val = session.EvaluateExpression(s.ResultVariable);
                                if (val != "0")
                                    failed = true;
                            }
                            if (s.CheckResult && !failed)
                                failed = r.MainStatus != "^done";

                            if (failed)
                            {
                                string msg = s.ErrorMessage ?? "Custom FLASH programming step failed";
                                if (s.CanRetry)
                                    throw new RetriableException(msg);
                                else
                                    throw new Exception(msg);
                            }

                            done += s.ProgressWeight;
                            reporter.ReportTaskProgress(done, total);
                        }
                        break;
                    }
                    catch (RetriableException)
                    {
                        if (retry < 2)
                            continue;
                        throw;
                    }
                }

                reporter.ReportTaskCompletion(true);
                session.SendInformationalOutput("Loaded image in " + (int)(DateTime.Now - startTime).TotalMilliseconds + " ms");
            }

            if (!string.IsNullOrEmpty(sequence.InitialHardBreakpointExpression))
                session.RequestInitialBreakpoint(service.ExpandProjectVariables(sequence.InitialHardBreakpointExpression, true, false));

            return true;
        }
    }

}
