using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace RedLinkDebugPackage
{
    class RedLinkGDBStub : IGDBStubInstance
    {
        public RedLinkGDBStub(
            DebugStartContext context,
            RedLinkDebugSettings settings,
            RedLinkServerCommandLine cmdLine,
            int gdbPort,
            IExternalToolInstance tool,
            bool programNow)
        {
            Tool = tool;
            _GDBPort = gdbPort;
            _Settings = settings;
            _LoadFLASH = programNow;
        }

        readonly int _GDBPort;
        private readonly RedLinkDebugSettings _Settings;
        private readonly bool _LoadFLASH;

        public IExternalToolInstance Tool { get; }

        public object LocalGDBEndpoint => _GDBPort;

        public IConsoleOutputClassifier OutputClassifier => null;

        public void ConnectGDBToStub(IDebugStartService service, ISimpleGDBSession session)
        {
            SimpleGDBCommandResult result;
            foreach (var cmd in _Settings.StartupCommands ?? new string[0])
            {
                result = session.RunGDBCommand(cmd);
                if (!result.IsDone)
                    throw new Exception("GDB command failed: " + cmd);
            }

            if (_LoadFLASH)
            {
                result = session.RunGDBCommand("load");
                if (!result.IsDone)
                    throw new Exception("Failed to program FLASH memory");

                service.OnFirmwareProgrammedSuccessfully();
            }
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
            DateTime start = DateTime.Now;
            while (!Tool.AllText.Contains("crt_emu_cm_redlink"))
            {
                if ((DateTime.Now - start).TotalSeconds > 3)
                    return true;

                if (cancelEvent.WaitOne(100))
                    return false;
            }

            return true;
        }
    }
}
