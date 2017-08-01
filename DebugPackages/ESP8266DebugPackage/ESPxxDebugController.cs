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
        public override Type[] SettingsObjectTypes => new[] { typeof(ESPxxOpenOCDSettings) };

        public override ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            return new GUI.ESPxxOpenOCDSettingsControl(method, host, this);
        }

        public override object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration)
        {
            ESPxxOpenOCDSettingsEditor editor = new ESPxxOpenOCDSettingsEditor(host, methodDirectory, null, default(KnownInterfaceInstance));
            string value;
            if (legacyConfiguration.TryGetValue("com.sysprogs.esp8266.openocd.iface_script", out value))
                editor.ReplaceScript(true, value);

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

        protected override bool SupportsScriptEditing => false;

        public class ESPxxGDBStub : OpenOCDGDBStub
        {
            private ESPxxDebugController _Controller;
            private DebugStartContext _Context;

            public ESPxxGDBStub(ESPxxDebugController controller, DebugStartContext context, OpenOCDCommandLine cmdLine, IExternalToolInstance tool, ESPxxOpenOCDSettings settings, int gdbPort, int telnetPort, string temporaryScript)
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

        protected abstract bool LoadFLASH(DebugStartContext context, IDebugStartService service, ISimpleGDBSession session, ESPxxOpenOCDSettings settings);

        protected override IGDBStubInstance CreateStub(DebugStartContext context, OpenOCDSettings settings, OpenOCDCommandLine cmdLine, int gdbPort, int telnetPort, string temporaryScript, IExternalToolInstance tool)
        {
            return new ESPxxGDBStub(this, context, cmdLine, tool, (ESPxxOpenOCDSettings)settings, gdbPort, telnetPort, temporaryScript);
        }

        protected override string GetOpenOCDDirectory(string methodDir)
        {
            return Path.GetFullPath(Path.Combine(methodDir, @"..\..\..\OpenOCD"));
        }
    }

    public class ESP32DebugController : ESPxxDebugController
    {
        protected override bool LoadFLASH(DebugStartContext context, IDebugStartService service, ISimpleGDBSession session, ESPxxOpenOCDSettings settings)
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
                    {
                        if (r.Offset == null || r.Path == null)
                            continue;
                        blocks.Add(new ProgrammableRegion { FileName = service.ExpandProjectVariables(r.Path, true, true), Offset = ParseAddress(r.Offset) });
                    }

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

        private int ParseAddress(string offset)
        {
            int r;
            if (int.TryParse(offset, out r))
                return r;
            if (offset.StartsWith("0x") && int.TryParse(offset.Substring(2), NumberStyles.HexNumber, null, out r))
                return r;
            throw new Exception($"Invalid address ({offset}) specified in the additional FLASH resources");
        }
    }

}
