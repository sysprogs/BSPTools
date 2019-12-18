using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace BSPGenerationTools
{
    public class BSPReportWriter : IDisposable
    {
        private readonly string _ReportDir, _ReportFileName;
        private readonly bool _StopOnErrors;

        bool _Disposed = false;

        public BSPReportWriter(string reportDir, string reportFileName = "BSPReport.txt")
        {
            _ReportDir = reportDir;
            _ReportFileName = reportFileName;
            Directory.CreateDirectory(reportDir);

            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        }

        private void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            if (!_Disposed)
                throw new Exception("The BSP report generator was not disposed properly. Make sure the BSPGenerator instance is surrounded with using()");
        }

        public enum MessageSeverity
        {
            Warning,
            Error,
        }

        class MergeableMessage
        {
            public struct Record
            {
                public string Text;

                public override string ToString() => Text;
            }

            public List<Record> Records = new List<Record>();
            internal string Message;
            internal bool OneLineFormat;

            internal void Add(string argument)
            {
                Records.Add(new Record { Text = argument });
            }
        }

        struct Key
        {
            public string Text;
            public MessageSeverity Severity;
        }

        Dictionary<Key, MergeableMessage> _MergeableMessages = new Dictionary<Key, MergeableMessage>();

        public void ReportMergeableError(string message, string argument, bool oneLine = false) => ReportMergeableMessage(MessageSeverity.Error, message, argument, oneLine);

        public void ReportMergeableMessage(MessageSeverity severity, string message, string argument, bool oneLineFormat)
        {
            var key = new Key { Severity = severity, Text = message };

            if (!_MergeableMessages.TryGetValue(key, out var list))
                _MergeableMessages[key] = list = new MergeableMessage { Message = message, OneLineFormat = oneLineFormat };

            list.Add(argument);

            RaiseDebuggerEventIfEnabled();
        }

        void RaiseDebuggerEventIfEnabled()
        {
            if (_StopOnErrors)
                Debugger.Break();
        }

        public void Dispose()
        {
            int warnings = _MergeableMessages.Count(kv => kv.Key.Severity == MessageSeverity.Warning);
            int errors = _MergeableMessages.Count(kv => kv.Key.Severity == MessageSeverity.Error);

            string reportFile = Path.Combine(_ReportDir, _ReportFileName);

            using (var sw = new StreamWriter(reportFile))
            {
                sw.WriteLine($"BSP generation completed with {warnings} warnings and {errors} errors");

                if (errors > 0)
                {
                    sw.WriteLine("Errors:");
                    foreach (var kv in _MergeableMessages)
                        if (kv.Key.Severity == MessageSeverity.Error)
                            LogMessage(sw, kv.Value);
                }

                sw.WriteLine("--------------------------------------");

                if (warnings > 0)
                {
                    sw.WriteLine("Warnings:");
                    foreach (var kv in _MergeableMessages)
                        if (kv.Key.Severity == MessageSeverity.Warning)
                            LogMessage(sw, kv.Value);
                }

                sw.WriteLine("--------------------------------------");

            }

            if (errors > 0)
                throw new Exception($"BSP generation failed. Examine {reportFile} for details.");

            _Disposed = true;
        }

        private void LogMessage(StreamWriter sw, MergeableMessage msg)
        {
            sw.WriteLine(msg.Message);
            if (msg.OneLineFormat)
                sw.WriteLine("\t" + string.Join(", ", msg.Records.Select(r => r.Text)));
            else
            {
                foreach (var rec in msg.Records)
                    sw.WriteLine("\t" + rec.Text);
            }
        }
    }
}
