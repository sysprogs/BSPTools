using BSPEngine;
using BSPGenerationTools;
using BSPGenerationTools.Parsing;
using STM32ProjectImporter;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using VendorSampleParserEngine;

namespace GeneratorSampleStm32.ProjectParsers
{
    class SW4STM32ProjectParser : SW4STM32ProjectParserBase, IDisposable
    {
        private MCU[] _SupportedMCUs;
        HashSet<string> _SupportedMCUNames = new HashSet<string>();
        BSPReportWriter _Report;

        public List<VendorSampleParser.UnparseableVendorSample> FailedSamples = new List<VendorSampleParser.UnparseableVendorSample>();

        public SW4STM32ProjectParser(string reportDir, MCU[] supportedMCUs)
        {
            _Report = new BSPReportWriter(reportDir, "ParseReport.txt");
            _SupportedMCUs = supportedMCUs;
            foreach (var mcu in _SupportedMCUs)
                _SupportedMCUNames.Add(mcu.ID);
        }

        protected override void OnMultipleConfigurationsFound(string projectFile)
        {
            base.OnMultipleConfigurationsFound(projectFile);
            _Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "Found multiple configurations with the same ID", projectFile, false);
        }

        protected override void OnParseFailed(Exception ex, string sampleID, string projectFileDir, string warningText)
        {
            base.OnParseFailed(ex, sampleID, projectFileDir, warningText);
            if (sampleID != null)
                FailedSamples.Add(new VendorSampleParser.UnparseableVendorSample { UniqueID = sampleID, ErrorDetails = ex.ToString() });
            _Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, warningText, projectFileDir, false);
        }

        protected override void AdjustMCUName(ref string mcu)
        {
            base.AdjustMCUName(ref mcu);

            if (!_SupportedMCUNames.Contains(mcu) && mcu.EndsWith("xQ"))
            {
                mcu = mcu.Remove(mcu.Length - 3, 3);
            }
        }

        protected override void ValidateFinalMCUName(string mcu)
        {
            base.ValidateFinalMCUName(mcu);

            if (!_SupportedMCUNames.Contains(mcu))
            {
                _Report.ReportMergeableError("Invalid MCU", mcu);
            }
        }

        public void Dispose()
        {
            _Report.Dispose();
        }

        protected override void OnFileNotFound(string fullPath)
        {
            base.OnFileNotFound(fullPath);
            _Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "Missing file/directory", fullPath, false);
        }
    }

}
