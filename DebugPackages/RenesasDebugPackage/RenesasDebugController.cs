using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RenesasDebugPackage
{
    public class RenesasDebugController : IDebugMethodController, ICustomSettingsTypeProvider
    {
        public virtual Type[] SettingsObjectTypes => new[] { typeof(RenesasDebugSettings) };

        public virtual ICustomSettingsTypeProvider TypeProvider => this;

        public virtual bool SupportsConnectionTesting => false;

        public virtual ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            return new GUI.RenesasDebugSettingsControl(method, host, this).Controller;
        }

        public IGDBStubInstance StartGDBStub(IDebugStartService startService, DebugStartContext context)
        {
            var settings = (RenesasDebugSettings)context.Configuration ?? new RenesasDebugSettings();

            var cmdLine = new RenesasGDBServerCommandLine(settings.CommandLineArguments);
            int gdbPort;
            using (var allocator = startService.BeginAllocatingTCPPorts())
            {
                cmdLine.GDBPort = gdbPort = allocator.AllocateUnusedTCPPort("SYS:GDB_PORT");
                cmdLine.AuxiliaryPort = allocator.AllocateUnusedTCPPort("com.sysprogs.renesas.auxiliary_port");
            }

            string debugComponentLinkFile = Path.Combine(GetOpenOCDDirectory(context.Method.Directory), "DebugCompLink.txt");
            if (!File.Exists(debugComponentLinkFile))
                throw new Exception($"{debugComponentLinkFile} does not exist");

            cmdLine.DeviceID = startService.MCU.ExpandedMCU.ID;
            if (cmdLine.DebugInterface == null)
                cmdLine.DebugInterface = "EZ";

            string debugComponentDir = File.ReadAllText(debugComponentLinkFile);
            string e2gdbServer = Path.Combine(debugComponentDir, "e2-server-gdb.exe");
            if (!File.Exists(e2gdbServer))
                throw new Exception("Could not find " + e2gdbServer);

            var tool = startService.LaunchCommandLineTool(new CommandLineToolLaunchInfo
            {
                Command = e2gdbServer,
                Arguments = cmdLine.CommandLine,
                WorkingDirectory = Path.GetDirectoryName(e2gdbServer)
            });

            return new RenesasGDBStub(context, settings, cmdLine, gdbPort, tool);
        }

        protected virtual string GetOpenOCDDirectory(string methodDir)
        {
            return methodDir;
        }

        public virtual object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration) => null;

        class RenesasGDBStub : IGDBStubInstance
        {
            private readonly int _GDBPort;
            private readonly RenesasDebugSettings _Settings;
            private readonly RenesasGDBServerCommandLine _CmdLine;

            public RenesasGDBStub(DebugStartContext context, RenesasDebugSettings settings, RenesasGDBServerCommandLine cmdLine, int gdbPort, IExternalToolInstance tool)
            {
                Tool = tool;
                _CmdLine = cmdLine;
                _GDBPort = gdbPort;
                _Settings = settings;
            }

            public IExternalToolInstance Tool { get; private set; }


            public object LocalGDBEndpoint => _GDBPort;

            public IConsoleOutputClassifier OutputClassifier => null;

            public virtual void ConnectGDBToStub(IDebugStartService service, ISimpleGDBSession session)
            {
                string[] regularStartupCommands = new[]
                {
                    "-gdb-set breakpoint pending on",
                    "-gdb-set detach-on-fork on",
                    "-gdb-set python print-stack none",
                    "-gdb-set print object on",
                    "-gdb-set print sevenbit-strings on",
                    "-gdb-set host-charset UTF-8",
                    "-gdb-set target-charset WINDOWS-1252",
                    "-gdb-set target-wide-charset UTF-16",
                    "-gdb-set pagination off",
                    "-gdb-set auto-solib-add on",
                    "inferior 1",
                    "set remotetimeout 10",
                    "set tcp connect-timeout 30",
                };

                SimpleGDBCommandResult result;
                foreach(var cmd in regularStartupCommands)
                {
                    result = session.RunGDBCommand(cmd);
                    if (!result.IsDone)
                        throw new Exception("GDB command failed: " + cmd);
                }

                session.EnableAsyncMode(GDBAsyncMode.AsyncWithTemporaryBreaks, true, true);
                session.ConnectToExtendedRemote(null, _GDBPort, true);

                result = session.RunGDBCommand("mon is_target_connected");
                if (!result.IsDone)
                    throw new Exception("The target did not report connection state");

                if (result.StubOutput?.FirstOrDefault(l=>l.Trim() == "Connection status=connected.") == null)
                    throw new Exception("The Renesas gdb stub is not connected to the target.");

                result = session.RunGDBCommand("monitor get_no_hw_bkpts_available");
                if (result.IsDone && result.StubOutput != null)
                    foreach(var line in result.StubOutput)
                    {
                        if (int.TryParse(line.Trim(), out var tmp))
                        {
                            session.RunGDBCommand($"set remote hardware-breakpoint-limit " + tmp);
                            break;
                        }
                    }

                result = session.RunGDBCommand("monitor get_target_max_address");
                if (result.IsDone && result.StubOutput != null)
                    foreach (var line in result.StubOutput)
                    {
                        string trimmedLine = line.Trim();
                        if (trimmedLine.StartsWith("0x") && int.TryParse(trimmedLine.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var tmp))
                        {
                            session.RunGDBCommand($"mem 0x0 0x{tmp+1:x} rw 8 nocache");
                            break;
                        }
                    }

                result = session.RunGDBCommand("monitor configuration_complete");
                result = session.RunGDBCommand("monitor prg_download_start_on_connect");

                result = session.RunGDBCommand("load");
                if (!result.IsDone)
                    throw new Exception("Failed to program FLASH memory");

                string[] finalCommands = new[]
                {
                    "monitor prg_download_end",
                    "monitor reset",
                    "monitor enable_stopped_notify_on_connect",
                    "monitor enable_execute_on_connect",
                };

                using (var awaiter = session.InterceptFirstStoppedEvent())
                {
                    foreach (var cmd in finalCommands)
                    {
                        result = session.RunGDBCommand(cmd);
                        if (!result.IsDone)
                            throw new Exception("GDB command failed: " + cmd);
                    }

                    while (!awaiter.WaitForStop(100))
                        session.RunGDBCommand("monitor do_nothing");
                }
            }

            public ILiveMemoryEvaluator CreateLiveMemoryEvaluator(IDebugStartService service) => null;

            public void Dispose()
            {
            }

            public string TryGetMeaningfulErrorMessageFromStubOutput()
            {
                return null;
            }

            public bool WaitForToolToStart(ManualResetEvent cancelEvent)
            {
                while (!cancelEvent.WaitOne(100))
                {
                    if (!Tool.IsRunning || Tool.AllText.Contains("\nFinished target connection"))
                        return true;
                }
                return true;
            }
        }
    }

}
