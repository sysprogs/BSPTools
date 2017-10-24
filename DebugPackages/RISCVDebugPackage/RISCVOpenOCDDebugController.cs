using BSPEngine;
using OpenOCDPackage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RISCVDebugPackage
{
    public class RISCVOpenOCDDebugController : IDebugMethodController, ICustomSettingsTypeProvider
    {
        public virtual Type[] SettingsObjectTypes => new[] { typeof(RISCVOpenOCDSettings) };

        public virtual ICustomSettingsTypeProvider TypeProvider => this;

        public virtual bool SupportsConnectionTesting => true;

        public virtual ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            return new GUI.RISCVOpenOCDSettingsControl(method, host, this);
        }

        class PortAllocationHelper
        {
            public static int AllocateUnusedTCPPort(ITCPPortAllocator allocator, string varName, int preferredPort)
            {
                var mtd = allocator.GetType().GetMethod(nameof(allocator.AllocateUnusedTCPPort), new Type[] { typeof(string), typeof(int) });
                if (mtd != null)
                {
                    //VisualGDB 5.3R2+
                    return (int)mtd.Invoke(allocator, new object[] { varName, preferredPort });
                }
                else
                    return allocator.AllocateUnusedTCPPort(varName);
            }
        }

        public IGDBStubInstance StartGDBStub(IDebugStartService startService, DebugStartContext context)
        {
            var settings = (RISCVOpenOCDSettings)context.Configuration;

            OpenOCDCommandLine cmdLine = OpenOCDCommandLine.Parse(settings.CommandLine, startService.CommandLineHelper);
            if (context.ResolvedDevices?.Interface != null)
            {
                if (context.ResolvedDevices.AllCompatibleDevices.Length > 1)
                {
                    var db = new QuickSetupDatabase(false, context.Method.Directory);
                    var matchingIface = db.AllInterfaces.FirstOrDefault(i => i.ID == context.ResolvedDevices.Interface.ID);
                    if (matchingIface?.SerialNumberCommand != null)
                        cmdLine.InsertAfterInterfaceScript(new OpenOCDCommandLine.CommandItem { Command = $"{matchingIface.SerialNumberCommand} {EscapeSerialNumber(context.ResolvedDevices.BestMatch.Device.SerialNumber)}" });
                }
            }

            if (startService.Mode == EmbeddedDebugMode.Attach)
            {
                for (; ; )
                {
                    var item = cmdLine.Items.OfType<OpenOCDCommandLine.CommandItem>().FirstOrDefault(c => c.Command.StartsWith("reset"));
                    if (item == null)
                        break;
                    cmdLine.Items.Remove(item);
                }
            }

            int gdbPort, telnetPort;
            using (var allocator = startService.BeginAllocatingTCPPorts())
            {
                gdbPort = PortAllocationHelper.AllocateUnusedTCPPort(allocator, "SYS:GDB_PORT", settings.PreferredGDBPort);
                telnetPort = PortAllocationHelper.AllocateUnusedTCPPort(allocator, "com.sysprogs.openocd.telnet_port", settings.PreferredTelnetPort);
            }

            cmdLine.Items.Insert(0, new OpenOCDCommandLine.CommandItem { Command = "gdb_port " + gdbPort });
            cmdLine.Items.Insert(1, new OpenOCDCommandLine.CommandItem { Command = "telnet_port " + telnetPort });
            cmdLine.Items.Add(new OpenOCDCommandLine.CommandItem { Command = "echo VisualGDB_OpenOCD_Ready" });

            var tool = startService.LaunchCommandLineTool(new CommandLineToolLaunchInfo { Command = Path.Combine(GetOpenOCDDirectory(context.Method.Directory), "bin\\openocd.exe"), Arguments = cmdLine.ToString(), WorkingDirectory = Path.Combine(GetOpenOCDDirectory(context.Method.Directory), "share\\openocd\\scripts") });
            return CreateStub(context, settings, cmdLine, gdbPort, telnetPort, null, tool);
        }

        protected virtual string GetOpenOCDDirectory(string methodDir)
        {
            return methodDir;
        }

        protected virtual IGDBStubInstance CreateStub(DebugStartContext context, OpenOCDSettings settings, OpenOCDCommandLine cmdLine, int gdbPort, int telnetPort, string temporaryScript, IExternalToolInstance tool)
        {
            return new OpenOCDGDBStub(cmdLine, tool, settings, gdbPort, telnetPort, temporaryScript);
        }

        protected string EscapeSerialNumber(string serialNumber)
        {
            bool needEscaping = false;
            foreach (var ch in serialNumber)
            {
                if (ch >= '0' && ch <= '9')
                    continue;
                if (ch == '_' || ch == '-')
                    continue;
                if (char.ToLower(ch) >= 'a' && char.ToLower(ch) <= 'z')
                    continue;
                needEscaping = true;
                break;
            }

            if (!needEscaping)
                return serialNumber;

            StringBuilder result = new StringBuilder();
            foreach (var ch in serialNumber)
                result.Append($"\\x{(int)ch:x2}");

            return "\\\"" + result + "\\\"";
        }

        public virtual object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration) => null;

        public class OpenOCDGDBStub : IGDBStubInstance
        {
            protected readonly OpenOCDCommandLine _CmdLine;
            protected readonly OpenOCDSettings _Settings;
            private readonly int _GDBPort, _TelnetPort;
            private readonly string _TemporaryScript;

            public OpenOCDGDBStub(OpenOCDCommandLine cmdLine, IExternalToolInstance tool, OpenOCDSettings settings, int gdbPort, int telnetPort, string temporaryScript)
            {
                _CmdLine = cmdLine;
                Tool = tool;
                _GDBPort = gdbPort;
                _TelnetPort = telnetPort;
                _Settings = settings;
                _TemporaryScript = temporaryScript;
            }

            public IExternalToolInstance Tool { get; private set; }

            public object LocalGDBEndpoint => _GDBPort;

            public IConsoleOutputClassifier OutputClassifier { get; } = new OpenOCDOutputClassifier();

            protected virtual bool SkipCommandOnAttach(string cmd) => cmd.Trim() == "load" || cmd.Trim().StartsWith("mon reset") || cmd.Trim().StartsWith("monitor reset");

            public virtual void ConnectGDBToStub(IDebugStartService service, ISimpleGDBSession session)
            {
                foreach (var cmd in _Settings.StartupCommands)
                {
                    if (service.Mode == EmbeddedDebugMode.Attach)
                    {
                        if (SkipCommandOnAttach(cmd))
                            continue;
                    }

                    bool isLoad = cmd.Trim() == "load";
                    if (isLoad)
                    {
                        switch (_Settings.ProgramMode)
                        {
                            case ProgramMode.Disabled:
                                continue;
                            case ProgramMode.Auto:
                                if (service.IsCurrentFirmwareAlreadyProgrammed())
                                    continue;
                                break;
                        }
                    }

                    if (isLoad)
                    {
                        if (isLoad && RunLoadCommand(service, session, cmd))
                            service.OnFirmwareProgrammedSuccessfully();
                    }
                    else
                        session.RunGDBCommand(cmd);
                }
            }

            protected virtual bool RunLoadCommand(IDebugStartService service, ISimpleGDBSession session, string cmd)
            {
                return session.RunGDBCommand(cmd).IsDone;
            }

            public ILiveMemoryEvaluator CreateLiveMemoryEvaluator(IDebugStartService service)
            {
                if (_TelnetPort == 0)
                    return null;

                return new LiveMemoryEvaluator(_TelnetPort);
            }

            public void Dispose()
            {
                if (!string.IsNullOrEmpty(_TemporaryScript))
                {
                    try
                    {
                        File.Delete(_TemporaryScript);
                    }
                    catch { }
                }
            }

            public string TryGetMeaningfulErrorMessageFromStubOutput()
            {
                var lines = Tool.AllText.Split('\n').Select(l => l.Trim()).ToArray();
                var errorLine = lines.FirstOrDefault(l => l.StartsWith("Error:", StringComparison.InvariantCultureIgnoreCase) && !l.Contains("libusb_open()"));
                if (errorLine != null)
                {
                    string error = errorLine.Substring(6).Trim();
                    if (error.Contains("interrogation"))
                    {
                        error += "\r\nThis error typically indicates JTAG/SWD wiring problems.\r\nPlease double-check your board layout and connections.";
                    }
                    return error;
                }

                return null;
            }

            public bool WaitForToolToStart(ManualResetEvent cancelEvent)
            {
                while (!cancelEvent.WaitOne(100))
                {
                    if (!Tool.IsRunning || Tool.AllText.Contains("\nVisualGDB_OpenOCD_Ready"))
                        return true;
                }
                return true;
            }
        }
    }

}
