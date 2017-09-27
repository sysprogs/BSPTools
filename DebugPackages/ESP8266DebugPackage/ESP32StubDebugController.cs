using BSPEngine;
using ESP8266DebugPackage.GUI;
using OpenOCDPackage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ESP8266DebugPackage
{
    class ESP32StubDebugController : IDebugMethodController, ICustomSettingsTypeProvider
    {
        public ICustomSettingsTypeProvider TypeProvider => this;

        public bool SupportsConnectionTesting => false;

        public Type[] SettingsObjectTypes => new[] { typeof(ESP32GDBStubSettings) };

        public ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            return new ESP32GDBStubSettingsControl(host, this);
        }

        public IGDBStubInstance StartGDBStub(IDebugStartService startService, DebugStartContext context)
        {
            var settings = context.Configuration as ESP32GDBStubSettings;
            if (settings == null)
                throw new Exception("Missing ESP32 stub settings");

            int comPort;
            if (context.ResolvedDevices?.BestMatch.COMPortNumber.HasValue == true)
                comPort = context.ResolvedDevices.BestMatch.COMPortNumber ?? 1;
            else
            {
                if (settings.COMPort?.StartsWith("COM", StringComparison.InvariantCultureIgnoreCase) != true)
                    throw new Exception("Invalid COM port specifier: " + settings.COMPort);
                comPort = int.Parse(settings.COMPort.Substring(3));
            }


            var regions = ESP32StartupSequence.BuildFLASHImages(startService.TargetPath, startService.SystemDictionary, settings.FLASHSettings);

            if (settings.FLASHResources != null)
                foreach (var r in settings.FLASHResources)
                    if (r.Valid)
                        regions.Add(r.ToProgrammableRegion(startService));

            foreach (var r in regions)
            {
                var pythonExe = Path.GetFullPath(context.Method.Directory + @"\..\..\..\..\bin\bash.exe");

                string args = $"python /esp32-bsp/esp-idf/components/esptool_py/esptool/esptool.py --port /dev/ttyS{comPort - 1} {settings.AdditionalToolArguments} write_flash";
                foreach (var region in regions)
                {
                    if (region.FileName.Contains(" "))
                        throw new Exception($"{region.FileName} contains spaces in path. Cannot program it using esptool.py. Please move your project to a directory without spaces.");
                    args += $" 0x{region.Offset:x} " + region.FileName.Replace('\\', '/');
                }

                var tool = startService.LaunchCommandLineTool(new CommandLineToolLaunchInfo
                {
                    Command = pythonExe,
                    Arguments = $"--login -c \"{args}\"",
                });

                return new GDBStubInstance(context, regions) { Tool = tool };
            }

            throw new OperationCanceledException();
        }

        public object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration)
        {
            return null;
        }

        public class GDBStubInstance : IGDBStubInstance
        {
            private DebugStartContext _Context;
            private readonly List<ProgrammableRegion> _RegionCount;

            public GDBStubInstance(DebugStartContext context, List<ProgrammableRegion> regions)
            {
                _Context = context;
                _RegionCount = regions;
            }

            public IExternalToolInstance Tool { get; set; }

            public object LocalGDBEndpoint => null;

            public IConsoleOutputClassifier OutputClassifier => null;

            public void ConnectGDBToStub(IDebugStartService service, ISimpleGDBSession session)
            {
                using (var r = session.CreateScopedProgressReporter("Programming FLASH memory", new string[] { "Loading FLASH memory" }))
                {
                    Regex rgPercentage = new Regex(@"(.*)\( *([0-9]+) *% *\)");

                    EventHandler<LineReceivedEventArgs> handler = (s, e) =>
                    {
                        session.SendInformationalOutput(e.Line);
                        r.AppendLogLine(e.Line);
                        var m = rgPercentage.Match(e.Line);
                        if (m.Success)
                        {
                            int value = int.Parse(m.Groups[2].Value);
                            r.ReportTaskProgress(value, 100, m.Groups[1].ToString());
                        }
                    };

                    try
                    {
                        Tool.LineReceived += handler;
                        while (Tool.IsRunning && !r.IsAborted)
                            Thread.Sleep(100);

                        if (r.IsAborted)
                        {
                            Tool.TerminateProgram();
                            throw new OperationCanceledException();
                        }
                    }
                    finally
                    {
                        Tool.LineReceived -= handler;
                    }
                }

                string text = Tool.AllText;
                int validLines = text.Split('\n').Count(l => l.Trim() == "Hash of data verified.");
                if (validLines >= _RegionCount.Count)
                    service.GUIService.Report("The FLASH memory has been programmed, however the ESP32 GDB stub does not support live debugging yet. Please use JTAG if you want to debug your program or reset your board to run the program without debugging.");
                else
                    throw new Exception("Warning: some of the FLASH regions could not be programmed. Please examine the stub output for details.");

                throw new OperationCanceledException();
            }

            public ILiveMemoryEvaluator CreateLiveMemoryEvaluator(IDebugStartService service) => null;

            public void Dispose()
            {
            }

            public string TryGetMeaningfulErrorMessageFromStubOutput() => null;

            public bool WaitForToolToStart(ManualResetEvent cancelEvent)
            {
                return true;
            }
        }
    }
}
