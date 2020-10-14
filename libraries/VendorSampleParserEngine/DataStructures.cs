using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace VendorSampleParserEngine
{
    public enum VendorSamplePass
    {
        None,
        InitialParse,          //This involves parsing the sample description, or building it using the original scripts and parsing the log file.
        InPlaceBuild,          //This involves building the sample definition using Sysprogs tools. Samples failing here will be excluded from the BSP.
        RelocatedBuild,        //This is the final check that the sample inserted into the BSP builds successfully. If any samples fail here, it must be investigated before releasing the BSP.

        Final = RelocatedBuild
    }

    public class VendorSampleTestReport
    {
        public class Record
        {
            [XmlAnyElement("ID")]
            public XmlNode[] LegacyID
            {
                get => null;
                set
                {
                    if (value != null)
                        UniqueID = value[0].SelectSingleNode("SampleName")?.InnerText + "-" + value[0].SelectSingleNode("DeviceID")?.InnerText;
                }
            }

            public string UniqueID;

            public bool BuildFailedExplicitly;
            public VendorSamplePass LastSucceededPass;
            public string KnownProblemID;
            public string ExtraInformation;
            public int BuildDuration;   //In milliseconds
            public DateTime TimeOfLastBuild;

            public override string ToString()
            {
                return $"{UniqueID} => {LastSucceededPass}";
            }
        }

        Dictionary<string, Record> _RecordDictionary = new Dictionary<string, Record>();

        public string BSPID;
        public string BSPVersion;

        public Record[] Records
        {
            get => _RecordDictionary.Values.ToArray();
            set
            {
                _RecordDictionary.Clear();
                if (value != null)
                    foreach (var rec in value)
                        _RecordDictionary[rec.UniqueID] = rec;
            }
        }

        public Record ProvideEntryForSample(string id)
        {
            if (_RecordDictionary.TryGetValue(id, out var rec))
                return rec;
            else
                return _RecordDictionary[id] = new Record { UniqueID = id };
        }

        public bool ShouldBuildIncrementally(string id, VendorSamplePass pass)
        {
            if (!_RecordDictionary.TryGetValue(id, out var rec))
                return true;    //The sample was never built. build it now.

            var prevPass = pass - 1;

            if (rec.LastSucceededPass < prevPass)
                return false;   //The sample failed at an earlier pass and won't build now. Skip it
            else if (rec.LastSucceededPass == VendorSamplePass.Final)
                return false;   //The sample passed all tests. No need to rebuild it.
            else
                return true;    //The sample must have failed at later point, or was never tested all the way to the final pass. Rebuild it.
        }

        internal bool HasSampleFailed(string id)
        {
            if (!_RecordDictionary.TryGetValue(id, out var rec))
                return false;
            return rec.BuildFailedExplicitly;
        }
    }

    public class KnownSampleProblemDatabase
    {
        public class ErrorEntry
        {
            Regex _RegexObject;

            public string ErrorMessage;
            public bool ErrorMessageIsRegex;
            public string ID;
            public string Description;

            public override string ToString()
            {
                return Description ?? ID;
            }

            public bool IsMatch(string[] lines)
            {
                if (ErrorMessageIsRegex)
                {
                    if (_RegexObject == null)
                        _RegexObject = new Regex(ErrorMessage);

                    foreach (var line in lines)
                        if (_RegexObject?.IsMatch(line) == true)
                            return true;
                }
                else
                {
                    foreach (var line in lines)
                        if (line.Contains(ErrorMessage))
                            return true;
                }
                return false;
            }
        }

        public ErrorEntry[] Entries;

        public ErrorEntry TryClassifyError(string logFile)
        {
            if (string.IsNullOrEmpty(logFile) || !File.Exists(logFile))
                return null;

            var lines = File.ReadAllLines(logFile);
            var matchingEntries = Entries?.Where(e => e.IsMatch(lines))?.ToArray();
            if (matchingEntries == null || matchingEntries.Length == 0)
                return null;

            return matchingEntries.First();
        }
    }
}
