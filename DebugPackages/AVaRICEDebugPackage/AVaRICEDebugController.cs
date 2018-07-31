using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AVaRICEDebugPackage
{
    class AVaRICEDebugController : IDebugMethodController, ICustomSettingsTypeProvider
    {
        public Type[] SettingsObjectTypes => new[] { typeof(AVaRICEDebugSettings) };

        public ICustomSettingsTypeProvider TypeProvider => this;

        public bool SupportsConnectionTesting => true;

        public ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            return new GUI.AVaRICESettingsControl(method, host, this).Controller;
        }

        public IGDBStubInstance StartGDBStub(IDebugStartService startService, DebugStartContext context)
        {
            int gdbPort;
            using (var allocator = startService.BeginAllocatingTCPPorts())
                gdbPort = allocator.AllocateUnusedTCPPort("SYS:GDB_PORT");

            var settings = context.Configuration as AVaRICEDebugSettings ?? throw new Exception("Invalid AVaRICE debug settings");

            var exe = Path.Combine(context.Method.Directory, "avarice.exe");
            if (!File.Exists(exe))
                exe = Path.GetFullPath(Path.Combine(context.Method.Directory, @"..\..\..\bin\avarice.exe"));

            List<string> args = new List<string>();
            if (!string.IsNullOrEmpty(settings.DebugInterface))
                args.Add(settings.DebugInterface);
            if (!string.IsNullOrEmpty(settings.DebugBitrate))
                args.Add("-B " + settings.DebugBitrate);

            if (context.ResolvedDevices?.Interface?.ID != null)
            {
                args.Add("-j usb");
                int idx = context.ResolvedDevices.Interface.ID.IndexOf(':');
                if (idx != -1)
                    args.Add(context.ResolvedDevices.Interface.ID.Substring(idx + 1));
            }
            else
            {
                if (!string.IsNullOrEmpty(settings.DebugAdapterType))
                    args.Add(settings.DebugAdapterType);
                if (!string.IsNullOrEmpty(settings.DebugPort))
                    args.Add("-j " + settings.DebugPort.ToLower());
            }

            if (startService.Mode != EmbeddedDebugMode.Attach && startService.Mode != EmbeddedDebugMode.ConnectionTest && startService.TargetPath != null &&
                (settings.EraseFLASH || settings.ProgramFLASH || settings.VerifyFLASH))
            {
                args.Insert(0, $"--file \"{startService.TargetPath}\"");

                if (settings.EraseFLASH)
                    args.Add("--erase");
                if (settings.ProgramFLASH)
                    args.Add("--program");
                if (settings.VerifyFLASH)
                    args.Add("--verify");
            }

            if (!string.IsNullOrEmpty(settings.ExtraArguments))
                args.Add(settings.ExtraArguments);

            args.Add(":" + gdbPort);

            var tool = startService.LaunchCommandLineTool(new CommandLineToolLaunchInfo
            {
                Command = exe,
                Arguments = string.Join(" ", args.ToArray())
            });

            return new StubInstance(context, tool, gdbPort);
        }

        class StubInstance : IGDBStubInstance
        {
            readonly int _GDBPort;

            public StubInstance(DebugStartContext context, IExternalToolInstance tool, int GDBPort)
            {
                Tool = tool;
                _GDBPort = GDBPort;
            }

            public IExternalToolInstance Tool { get; }

            public object LocalGDBEndpoint => _GDBPort;

            public IConsoleOutputClassifier OutputClassifier => null;

            public void ConnectGDBToStub(IDebugStartService service, ISimpleGDBSession session)
            {
                session.RunGDBCommand($"set remotetimeout 60"); //We may need to wait for the AVaRICE to complete programming the FLASH memory.

                var result = session.RunGDBCommand($"target remote :{_GDBPort}");
                if (!result.IsDone)
                    throw new Exception("Failed to connect to AVaRICE");
            }

            public ILiveMemoryEvaluator CreateLiveMemoryEvaluator(IDebugStartService service)
            {
                return null;
            }

            public void Dispose()
            {
            }

            public string TryGetMeaningfulErrorMessageFromStubOutput()
            {
                return null;
            }

            public bool WaitForToolToStart(ManualResetEvent cancelEvent)
            {
                /*
                 
                do
                {
                    if (Tool.AllText.Contains("Waiting for connection on port"))
                        return true;
                    if (!Tool.IsRunning)
                        return false;
                } while (!cancelEvent.WaitOne(100));

                 */

                //AVaRICE doesn't flush the output properly when stdout is redirected, so we cannot rely on the 'waiting for connection' message.
                var start = DateTime.Now;
                while ((DateTime.Now - start).TotalMilliseconds < 500)
                {
                    if (cancelEvent.WaitOne(100))
                        return false;
                    if (!Tool.IsRunning)
                        return false;
                }

                return true;
            }
        }

        public object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration)
        {
            if (legacyConfiguration == null)
                return null;

            AVaRICEDebugSettings result = new AVaRICEDebugSettings();
            string tmp;
            if (legacyConfiguration.TryGetValue("com.sysprogs.avr.avarice.adapter", out tmp))
                result.DebugAdapterType = tmp;
            else
                return null;

            if (legacyConfiguration.TryGetValue("com.sysprogs.avr.avarice.iface", out tmp))
                result.DebugInterface = tmp;

            if (legacyConfiguration.TryGetValue("com.sysprogs.avr.avarice.bitrate", out tmp))
                result.DebugBitrate = tmp;

            result.EraseFLASH = legacyConfiguration.TryGetValue("com.sysprogs.avr.avarice.erase", out tmp) && tmp == "--erase";
            result.ProgramFLASH = legacyConfiguration.TryGetValue("com.sysprogs.avr.avarice.program", out tmp) && tmp == "--program";
            result.VerifyFLASH = legacyConfiguration.TryGetValue("com.sysprogs.avr.avarice.verify", out tmp) && tmp == "--verify";

            if (legacyConfiguration.TryGetValue("com.sysprogs.avr.avarice.extraargs", out tmp))
                result.ExtraArguments = tmp;

            return result;
        }
    }
}
