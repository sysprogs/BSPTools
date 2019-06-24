using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BSPGenerationTools.Parsing
{
    public class ParseReportWriter : IDisposable
    {
        FoldedWarningCollection _OrphanedSubregisters = new FoldedWarningCollection();
        FoldedWarningCollection _InvalidMasks = new FoldedWarningCollection();

        List<SingleDeviceFamilyHandle> _FamilyHandles = new List<SingleDeviceFamilyHandle>();
        private readonly string _LogFile;

        public ParseReportWriter(string logFile)
        {
            _LogFile = logFile;
        }

        public class SingleDeviceFamilyHandle : IDisposable
        {
            private ParseReportWriter _ParseReportWriter;
            private string _PeripheralHeaderFile;
            public readonly string ShortName;

            public SingleDeviceFamilyHandle(ParseReportWriter parseReportWriter, string peripheralHeaderFile)
            {
                _ParseReportWriter = parseReportWriter;
                _PeripheralHeaderFile = peripheralHeaderFile;

                ShortName = Path.GetFileNameWithoutExtension(peripheralHeaderFile);
            }

            public void Dispose()
            {
            }

            public readonly List<string> GenericWarnings = new List<string>();

            public void HandleUnexpectedToken(SimpleToken token)
            {
                GenericWarnings.Add($"{_PeripheralHeaderFile}:{token.Line + 1}: unexpected '{token.Value}'");
            }

            public void HandleInvalidNewStyleBitMask(string macroName, ulong value)
            {
                lock (_ParseReportWriter._InvalidMasks)
                    _ParseReportWriter._InvalidMasks.AddWarning(ShortName, macroName);
            }

            public void HandleOrphanedSubregisterMacro(PreprocessorMacroGroup grp, NamedSubregister subreg)
            {
                lock (_ParseReportWriter._OrphanedSubregisters)
                    _ParseReportWriter._OrphanedSubregisters.AddWarning(ShortName, subreg.Name);
            }
        }

        class FoldedWarningCollection
        {
            Dictionary<string, HashSet<string>> _DevicesPerWarning = new Dictionary<string, HashSet<string>>();

            public void AddWarning(string device, string canonicalWarning)
            {
                if (!_DevicesPerWarning.TryGetValue(canonicalWarning, out var lst))
                    _DevicesPerWarning[canonicalWarning] = lst = new HashSet<string>();

                lst.Add(device);
            }

            public void Dump(StreamWriter fw)
            {
                foreach (var entry in _DevicesPerWarning)
                    fw.WriteLine(entry.Key + " (" + string.Join(", ", entry.Value) + ")");
            }
        }


        public SingleDeviceFamilyHandle BeginParsingFile(string peripheralHeaderFile)
        {
            var result = new SingleDeviceFamilyHandle(this, peripheralHeaderFile);
            lock (_FamilyHandles)
                _FamilyHandles.Add(result);
            return result;
        }

        public void Dispose()
        {
            using (var fw = File.CreateText(_LogFile))
            {
                foreach(var h in _FamilyHandles)
                {
                    if (h.GenericWarnings.Count == 0)
                        continue;

                    fw.WriteLine($"{h.ShortName}: {h.GenericWarnings} warnings:");
                    foreach (var w in h.GenericWarnings)
                        fw.WriteLine("\t" + w);
                    fw.WriteLine();
                }

                fw.WriteLine("Possible orphaned subregister macros:");
                _OrphanedSubregisters.Dump(fw);
                fw.WriteLine("");

                fw.WriteLine("Inconsistent subregister masks:");
                _InvalidMasks.Dump(fw);
                fw.WriteLine("");
            }
        }
    }
}
