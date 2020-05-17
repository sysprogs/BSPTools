using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
            bool programNow,
            string explicitSerialNumber,
            int coreIndex)
        {
            Tool = tool;
            _GDBPort = gdbPort;
            _Settings = settings;
            _LoadFLASH = programNow;
            _ExplicitSerialNumber = explicitSerialNumber;
            _CoreIndex = coreIndex;
        }

        class OutputClassifierImpl : IConsoleOutputClassifier
        {
            Regex rgError = new Regex("^E[a-z]:[0-9]+:.*$");
            Regex rgWarning = new Regex(@"^W[a-z]\(.*$");

            public ClassifiedOutputLine ClassifyOutputLine(string line)
            {
                if (rgError.IsMatch(line))
                    return new ClassifiedOutputLine(line, ClassifiedLineKind.Error);
                else if (rgWarning.IsMatch(line))
                    return new ClassifiedOutputLine(line, ClassifiedLineKind.Warning);
                else
                    return default;
            }
        }

        readonly int _GDBPort;
        private readonly RedLinkDebugSettings _Settings;
        private readonly bool _LoadFLASH;
        private readonly string _ExplicitSerialNumber;
        readonly int _CoreIndex;

        public IExternalToolInstance Tool { get; }

        public object LocalGDBEndpoint => _GDBPort;

        public IConsoleOutputClassifier OutputClassifier { get; } = new OutputClassifierImpl();

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
            return new RedLinkLiveMemoryEvaluator(_ExplicitSerialNumber, _CoreIndex);
        }

        public void Dispose()
        {
        }

        public string TryGetMeaningfulErrorMessageFromStubOutput()
        {
            var errorLines = (Tool.AllText ?? "").Split('\n').Select(l => l.Trim()).Where(l => OutputClassifier.ClassifyOutputLine(l).Kind == ClassifiedLineKind.Error).ToArray();
            if (errorLines.Length > 0)
                return string.Join("\r\n", errorLines);
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
