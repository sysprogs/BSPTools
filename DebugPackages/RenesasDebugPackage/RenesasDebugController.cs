using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

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

            bool programNow;
            switch (settings.ProgramMode)
            {
                case ProgramMode.Disabled:
                    programNow = false;
                    break;
                case ProgramMode.Auto:
                    programNow = !startService.IsCurrentFirmwareAlreadyProgrammed();
                    break;
                default:
                    programNow = true;
                    break;
            }

            cmdLine.SetSeparatedValue("-ueraseRom=", programNow ? "1" : "0");

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

            return new RenesasGDBStub(context, settings, cmdLine, gdbPort, tool, $@"{debugComponentDir}\IoFiles\{startService.MCU.ExpandedMCU.ID}.sfrx", programNow);
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
            private readonly string _PeripheralFile;
            private readonly bool _LoadFLASH;
            private readonly RenesasGDBServerCommandLine _CmdLine;

            public RenesasGDBStub(DebugStartContext context, RenesasDebugSettings settings, RenesasGDBServerCommandLine cmdLine, int gdbPort, IExternalToolInstance tool, string peripheralFile, bool loadFLASH)
            {
                Tool = tool;
                _CmdLine = cmdLine;
                _GDBPort = gdbPort;
                _Settings = settings;
                _PeripheralFile = peripheralFile;
                _LoadFLASH = loadFLASH;
            }

            public IExternalToolInstance Tool { get; private set; }


            public object LocalGDBEndpoint => _GDBPort;

            public IConsoleOutputClassifier OutputClassifier => null;

            struct AccessKey
            {
                public string Direction;
                public int Size;
            }

            class MemoryRange
            {
                private int _FirstAddress, _LastAddress;
                private readonly int _Size;

                public MemoryRange(int addr, int size)
                {
                    _FirstAddress = _LastAddress = addr;
                    _Size = size;
                }

                public bool TryAppendAddress(int addr)
                {
                    if (addr == (_LastAddress + _Size))
                    {
                        _LastAddress = addr;
                        return true;
                    }
                    return false;
                }

                public override string ToString()
                {
                    if (_FirstAddress == _LastAddress)
                        return $"{_FirstAddress:x4}";
                    else
                        return $"{_FirstAddress:x4}-{_LastAddress:x4}";
                }
            }

            static IEnumerable<IEnumerable<Ty>> Slice<Ty>(IEnumerable<Ty> sequence, int sliceSize)
            {
                List<Ty> result = new List<Ty>();
                foreach (var obj in sequence)
                {
                    result.Add(obj);
                    if (result.Count >= sliceSize)
                    {
                        yield return result;
                        result = new List<Ty>();
                    }
                }

                if (result.Count > 0)
                    yield return result;
            }

            static int TryParseAddress(string addrString)
            {
                if (addrString?.StartsWith("0x") != true || !int.TryParse(addrString.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var addr))
                    return 0;
                return addr;
            }

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
                foreach (var cmd in regularStartupCommands)
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

                if (result.StubOutput?.FirstOrDefault(l => l.Trim() == "Connection status=connected.") == null)
                    throw new Exception("The Renesas gdb stub is not connected to the target.");

                result = ParseAndApplyIOAccessWidths(service, session, result);

                result = session.RunGDBCommand("monitor get_no_hw_bkpts_available");
                if (result.IsDone && result.StubOutput != null)
                    foreach (var line in result.StubOutput)
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
                            session.RunGDBCommand($"mem 0x0 0x{tmp + 1:x} rw 8 nocache");
                            break;
                        }
                    }

                result = session.RunGDBCommand("monitor configuration_complete");

                if (_LoadFLASH)
                {
                    result = session.RunGDBCommand("monitor prg_download_start_on_connect");

                    result = session.RunGDBCommand("load");
                    if (!result.IsDone)
                        throw new Exception("Failed to program FLASH memory");

                    service.OnFirmwareProgrammedSuccessfully();

                    result = session.RunGDBCommand("monitor prg_download_end");
                }

                string[] finalCommands = new[]
                {
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

            private SimpleGDBCommandResult ParseAndApplyIOAccessWidths(IDebugStartService service, ISimpleGDBSession session, SimpleGDBCommandResult result)
            {
                try
                {
                    if (!File.Exists(_PeripheralFile))
                        throw new Exception("Missing " + _PeripheralFile);

                    var doc = new XmlDocument();
                    doc.Load(_PeripheralFile);

                    Dictionary<AccessKey, List<MemoryRange>> rangesByAccess = new Dictionary<AccessKey, List<MemoryRange>>();

                    foreach (var el in doc.DocumentElement.SelectNodes("moduletable/module/register").OfType<XmlElement>().OrderBy(e => TryParseAddress(e.GetAttribute("address"))))
                    {
                        int sizeinBytes;
                        switch (el.GetAttribute("size") ?? "")
                        {
                            case "B":
                                sizeinBytes = 1;
                                break;
                            case "W":
                                sizeinBytes = 2;
                                break;
                            default:
                                continue;
                        }

                        string addrString = el.GetAttribute("address");
                        int addr = TryParseAddress(addrString);

                        var key = new AccessKey { Direction = el.GetAttribute("access"), Size = sizeinBytes };
                        if (!rangesByAccess.TryGetValue(key, out var list))
                            rangesByAccess[key] = list = new List<MemoryRange>();

                        bool found = false;
                        foreach (var range in list)
                            if (range.TryAppendAddress(addr))
                            {
                                found = true;
                                break;
                            }

                        if (!found)
                            list.Add(new MemoryRange(addr, key.Size));
                    }

                    foreach (var kv in rangesByAccess)
                    {
                        foreach (var range in Slice(kv.Value, 10))
                        {
                            string cmd = $"monitor set_io_access_width,{kv.Key.Direction},{kv.Key.Size}," + string.Join(",", range.Select(r => r.ToString()));
                            result = session.RunGDBCommand(cmd);
                        }
                    }
                }
                catch (Exception ex)
                {
                    service.GUIService.LogToDiagnosticsConsole($"Failed to parse device peripheral list: " + ex.Message);
                }

                return result;
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
