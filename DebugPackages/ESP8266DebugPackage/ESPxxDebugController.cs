using BSPEngine;
using OpenOCDPackage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

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

        protected override abstract IGDBStubInstance CreateStub(DebugStartContext context, OpenOCDSettings settings, OpenOCDCommandLine cmdLine, int gdbPort, int telnetPort, string temporaryScript, IExternalToolInstance tool);

        public override object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration)
        {
            ESPxxOpenOCDSettingsEditor editor = new ESPxxOpenOCDSettingsEditor(host, methodDirectory, null, default(KnownInterfaceInstance), _IsESP32);
            string value;
            if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.openocd.iface_script", out value))
                editor.ReplaceScript(true, value);
            else
                return null;    //Not an OpenOCD configuration

            editor.RebuildStartupCommands();

            if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_size", out value))
                editor.FLASHSettings.Size = ESP8266BinaryImage.ParseEnumValue<ESP8266BinaryImage.FLASHSize>(value);
            if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_mode", out value))
                editor.FLASHSettings.Mode = ESP8266BinaryImage.ParseEnumValue<ESP8266BinaryImage.FLASHMode>(value);
            if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_freq", out value))
                editor.FLASHSettings.Frequency = ESP8266BinaryImage.ParseEnumValue<ESP8266BinaryImage.FLASHFrequency>(value);

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

            public ESP32GDBStub(ESP32DebugController controller, DebugStartContext context, OpenOCDCommandLine cmdLine, IExternalToolInstance tool, ESPxxOpenOCDSettings settings, int gdbPort, int telnetPort, string temporaryScript)
                : base(cmdLine, tool, settings, gdbPort, telnetPort, temporaryScript)
            {
                _Controller = controller;
                _Context = context;
            }

            protected override bool SkipCommandOnAttach(string cmd)
            {
                return base.SkipCommandOnAttach(cmd);
            }

            protected override bool RunLoadCommand(IDebugStartService service, ISimpleGDBSession session, string cmd) => _Controller.LoadFLASH(_Context, service, session, (ESPxxOpenOCDSettings)_Settings);
        }


        protected override IGDBStubInstance CreateStub(DebugStartContext context, OpenOCDSettings settings, OpenOCDCommandLine cmdLine, int gdbPort, int telnetPort, string temporaryScript, IExternalToolInstance tool)
        {
            return new ESP32GDBStub(this, context, cmdLine, tool, (ESPxxOpenOCDSettings)settings, gdbPort, telnetPort, temporaryScript);
        }

        bool LoadFLASH(DebugStartContext context, IDebugStartService service, ISimpleGDBSession session, ESPxxOpenOCDSettings settings)
        {
            if (!session.RunGDBCommand("mon reset halt").IsDone)
                throw new Exception("Failed to reset target");

            string val;
            if (!service.SystemDictionary.TryGetValue("com.sysprogs.esp32.load_flash", out val) || val != "1")
            {
                //This is a RAM-only configuration
                return session.RunGDBCommand("load").IsDone;
            }
            else
            {
                var blocks = ESP32StartupSequence.BuildFLASHImages(service.TargetPath, service.SystemDictionary, settings.FLASHSettings);

                if (settings.FLASHResources != null)
                    foreach (var r in settings.FLASHResources)
                        if (r.Valid)
                            blocks.Add(r.ToProgrammableRegion(service));

                using (var ctx = session.CreateScopedProgressReporter("Programming FLASH...", new[] { "Programming FLASH memory" }))
                {
                    int blkNum = 0;
                    foreach (var blk in blocks)
                    {
                        ctx.ReportTaskProgress(blkNum++, blocks.Count);
                        string path = blk.FileName.Replace('\\', '/');
                        var result = session.RunGDBCommand($"mon program_esp32 \"{path}\" 0x{blk.Offset:x}");
                        bool succeeded = result.StubOutput?.FirstOrDefault(l => l.Contains("** Programming Finished **")) != null;
                        if (!succeeded)
                            throw new Exception("FLASH programming failed. Please review the gdb/OpenODC logs for details.");
                    }
                }
            }

            if (!session.RunGDBCommand("mon reset halt").IsDone)
                throw new Exception("Failed to reset target after programming");

            return true;
        }

    }

    public class ESP8266DebugController : ESPxxDebugController
    {
        public ESP8266DebugController()
            : base(false)
        {
        }

        protected override IGDBStubInstance CreateStub(DebugStartContext context, OpenOCDSettings settings, OpenOCDCommandLine cmdLine, int gdbPort, int telnetPort, string temporaryScript, IExternalToolInstance tool)
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

            protected override bool SkipCommandOnAttach(string cmd) => throw new NotSupportedException();
            protected override bool RunLoadCommand(IDebugStartService service, ISimpleGDBSession session, string cmd) => throw new NotSupportedException();

            public override void ConnectGDBToStub(IDebugStartService service, ISimpleGDBSession session)
            {
                bool programNow;
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
                            var sequence = ESP8266StartupSequence.BuildSequence(service, (ESP8266OpenOCDSettings)_Settings, (l, i) => session.SendInformationalOutput(l), programNow);
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
