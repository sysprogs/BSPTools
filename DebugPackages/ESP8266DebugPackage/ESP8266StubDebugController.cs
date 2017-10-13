using BSPEngine;
using ESP8266DebugPackage.GUI;
using OpenOCDPackage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ESP8266DebugPackage
{
    class ESP8266StubDebugController : IDebugMethodController, ICustomSettingsTypeProvider
    {
        public ICustomSettingsTypeProvider TypeProvider => this;

        public bool SupportsConnectionTesting => true;

        public Type[] SettingsObjectTypes => new[] { typeof(ESP8266GDBStubSettings) };

        public ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            return new ESPxxGDBStubSettingsControl(host, this);
        }

        public IGDBStubInstance StartGDBStub(IDebugStartService startService, DebugStartContext context)
        {
            if (startService.Mode == EmbeddedDebugMode.ConnectionTest)
                startService.GUIService.Report("Please ensure the firmware with the gdb stub is running on your board, then reset it and connect the COM port to your computer.");

            return new GDBStubInstance(context);
        }

        public object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration)
        {
            return null;
        }

        public class GDBStubInstance : IGDBStubInstance
        {
            private DebugStartContext _Context;
            private ESP8266GDBStubSettings _Settings;
            private string _COMPort;

            public GDBStubInstance(DebugStartContext context)
            {
                _Context = context;
                _Settings = (ESP8266GDBStubSettings)_Context.Configuration;

                if (context.ResolvedDevices?.BestMatch.COMPortNumber.HasValue == true)
                    _COMPort = "COM" + context.ResolvedDevices.BestMatch.COMPortNumber;
                else
                    _COMPort = _Settings.COMPort;
            }

            public IExternalToolInstance Tool => null;

            SerialPortStream _SerialPort;
            
            public object LocalGDBEndpoint
            {
                get
                {
                    if (_SerialPort == null)
                        _SerialPort = new SerialPortStream(_COMPort, _Settings.StubBaudRate, System.IO.Ports.Handshake.None);
                    return _SerialPort;
                }
            }

            public IConsoleOutputClassifier OutputClassifier { get; } = new OpenOCDOutputClassifier();

            public void ConnectGDBToStub(IDebugStartService service, ISimpleGDBSession session)
            {
                bool programFLASH = false;

                if (service.Mode != EmbeddedDebugMode.Attach && service.Mode != EmbeddedDebugMode.ConnectionTest)
                {
                    switch (_Settings.ProgramMode)
                    {
                        case ProgramMode.Auto:
                            programFLASH = !service.IsCurrentFirmwareAlreadyProgrammed();
                            break;
                        case ProgramMode.Enabled:
                            programFLASH = true;
                            break;
                    }
                }

                DoConnect(service, session, _Settings, _COMPort, programFLASH);
                if (_Settings.ProgramMode == ProgramMode.Auto)
                    service.OnFirmwareProgrammedSuccessfully();
            }

            public ILiveMemoryEvaluator CreateLiveMemoryEvaluator(IDebugStartService service) => null;

            public void Dispose()
            {
            }

            public string TryGetMeaningfulErrorMessageFromStubOutput() => null;

            public bool WaitForToolToStart(ManualResetEvent cancelEvent) => true;

            static void DoConnect(IDebugStartService service, ISimpleGDBSession session, ESP8266GDBStubSettings settings, string comPort, bool programFLASH)
            {
                var targetPath = service.TargetPath;
                if (targetPath != null) //When doing connection test without an active project, the targetPath will be NULL
                {
                    if (!File.Exists(targetPath))
                        throw new Exception(targetPath + " not found. Debugging will not be possible.");

                    bool stubFound = false;
                    using (var elf = new ELFFile(targetPath))
                    {
                        foreach (var sym in elf.LoadAllSymbols())
                        {
                            if (sym.Name == "gdbstub_init")
                            {
                                stubFound = true;
                                break;
                            }
                        }
                    }


                    if (!stubFound)
                    {
                        if (service.GUIService.Prompt("The programmed image does not contain the GDB stub. Do you want to open instructions on debugging with ESP8266 GDB stub?", MessageBoxIcon.Warning))
                        {
                            Process.Start("http://visualgdb.com/KB/esp8266gdbstub");
                            throw new OperationCanceledException();
                        }
                    }
                }

                List<string> steps = new List<string>();
                if (programFLASH)
                {
                    steps.Add("Connecting to bootloader");
                    steps.Add("Programming FLASH memory");
                }
                if (service.Mode != EmbeddedDebugMode.ProgramWithoutDebugging)
                    steps.Add("Connecting to GDB stub");

                using (var ctx = session.CreateScopedProgressReporter("Connecting to target device", steps.ToArray()))
                {
                    if (programFLASH)
                    {
                        if (!settings.SuppressResetConfirmation)
                            service.GUIService.Report("Please reboot your ESP8266 into the bootloader mode and press OK.");

                        using (var serialPort = new SerialPortStream(comPort, settings.BootloaderBaudRate, System.IO.Ports.Handshake.None))
                        {
                            serialPort.AllowTimingOutWithZeroBytes = true;

                            ESP8266BootloaderClient client = new ESP8266BootloaderClient(serialPort, settings.BootloaderResetDelay, settings.BootloaderActivationSequence);
                            client.Sync();
                            var regions = ESP8266StartupSequence.BuildFLASHImages(service, settings, (l, t) => session.SendInformationalOutput(l));

                            ctx.ReportTaskCompletion(true);

                            int totalSize = 0, writtenSize = 0;
                            foreach (var r in regions)
                                totalSize += r.Size;

                            ESP8266BootloaderClient.BlockWrittenHandler handler = (s, addr, len) => ctx.ReportTaskProgress(writtenSize += len, totalSize, $"Writing FLASH at 0x{addr:x8}...");
                            bool useDIO = false;

                            try
                            {
                                client.BlockWritten += handler;
                                foreach (var r in regions)
                                {
                                    var data = File.ReadAllBytes(r.FileName);
                                    if (r.Offset == 0 && data.Length >= 4)
                                        useDIO = (data[2] == 2);

                                    client.ProgramFLASH((uint)r.Offset, data);
                                }
                            }
                            finally
                            {
                                client.BlockWritten -= handler;
                            }

                            client.RunProgram(useDIO, false);
                        }
                    }

                    ctx.ReportTaskCompletion(true);

                    if (service.Mode != EmbeddedDebugMode.ProgramWithoutDebugging)
                    {
                        ctx.ReportTaskCompletion(true);
                        session.RunGDBCommand("set serial baud " + settings.StubBaudRate);
                        var result = session.RunGDBCommand(@"target remote \\.\" + comPort);
                        if (!result.IsDone)
                            throw new Exception("Failed to connect to the gdb stub. Please check your settings.");
                    }
                }
            }

        }
    }
}
