using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace VendorSampleParserEngine
{
    public enum VendorSamplePass
    {
        None,
        InitialParse,          //This involves parsing the sample description, or building it using the original scripts and parsing the log file.
        InPlaceBuild,          //This involves building the sample definition using Sysprogs tools. Samples failing here will be excluded from the BSP.
        RelocatedBuild,        //This is the final check that the sample inserted into the BSP builds successfully. If any samples fail here, it must be investigated before releasing the BSP.
    }

    public struct VendorSampleID
    {
        public readonly string SampleName;   //Must be the same as VendorSample.UserFriendlyName
        public readonly string DeviceID;     //Optional. If set, must match VendorSample.DeviceID

        public override string ToString()
        {
            return $"{SampleName}-{DeviceID}";
        }

        public VendorSampleID(VendorSample sample)
        {
            SampleName = sample.UserFriendlyName;
            DeviceID = sample.DeviceID;
        }

        public VendorSampleID(string sampleName, string deviceID)
        {
            SampleName = sampleName;
            DeviceID = deviceID;
        }
    }


    public class VendorSampleTestReport
    {
        public class Record
        {
            public VendorSampleID ID;

            public bool BuildFailed;
            public VendorSamplePass LastPerformedPass;
            public string KnownProblemID;
            public string ExtraInformation;
            public int BuildDuration;   //In milliseconds
            public DateTime TimeOfLastBuild;
        }

        Dictionary<VendorSampleID, Record> _RecordDictionary = new Dictionary<VendorSampleID, Record>();

        public Record[]  Records
        {
            get => _RecordDictionary.Values.ToArray();
            set
            {
                _RecordDictionary.Clear();
                if (value != null)
                    foreach (var rec in value)
                        _RecordDictionary[rec.ID] = rec;
            }
        }

        public Record ProvideEntryForSample(VendorSampleID id)
        {
            if (_RecordDictionary.TryGetValue(id, out var rec))
                return rec;
            else
                return _RecordDictionary[id] = new Record { ID = id };
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

            if (matchingEntries.Length == 1)
                return matchingEntries[0];

            throw new Exception($"The error in {logFile} matches several error rules. Change the rule definitions to be mutually exclusive!");
        }
    }
}
