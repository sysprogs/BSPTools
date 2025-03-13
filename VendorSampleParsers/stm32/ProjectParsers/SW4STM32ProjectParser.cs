using BSPEngine;
using BSPEngine.Eclipse;
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

        protected override void AdjustMCUName(ref string mcu, string cprojectFileDir)
        {
            base.AdjustMCUName(ref mcu, cprojectFileDir);

            if (mcu.EndsWith("x"))
            {
                if (mcu.StartsWith("STM32MP1"))
                    mcu = mcu.Substring(0, mcu.Length - 3) + "_M4";
                else
                    mcu = mcu.Remove(mcu.Length - 2, 2);
            }
            else if (mcu.EndsWith("xP"))
            {
                mcu = mcu.Remove(mcu.Length - 3, 3);
            }

            if (!_SupportedMCUNames.Contains(mcu) && mcu.EndsWith("xQ"))
            {
                mcu = mcu.Remove(mcu.Length - 3, 3);
            }

            if (cprojectFileDir.Contains("STM32MP235F-DK") && mcu.StartsWith("STM32MP25"))
            {
                //Appears to be a bug in the example files in SDK 1.1.0. Taking the MCU from the project file causes build errors.
                mcu = "STM32MP235F_M33";
            }
        }

        protected override void ValidateFinalMCUName(ref string mcu)
        {
            base.ValidateFinalMCUName(ref mcu);

            if (!_SupportedMCUNames.Contains(mcu))
            {
                if (mcu.Length > 11 && _SupportedMCUNames.Contains(mcu.Substring(0, 11)))
                {
                    mcu = mcu.Substring(0, 11);
                    return;
                }

                if (mcu.StartsWith("STM32G") && _SupportedMCUNames.Contains(mcu + "Ix"))
                {
                    //Appears to be a bug in the device definition list where multiple MCU variants are reported to have different FLASH sizes.
                    mcu += "Ix";
                    return;
                }

                for (int i = 11; i < mcu.Length; i++)
                {
                    var candidate = mcu.Substring(0, i) + "_M33";
                    if (_SupportedMCUNames.Contains(candidate))
                    {
                        mcu = candidate;
                        continue;
                    }
                }

                _Report.ReportMergeableError("Invalid MCU", mcu);
            }
        }

        public void Dispose()
        {
            _Report.Dispose();
        }

        protected override void OnFileNotFound(EclipseProject.FileNotFoundEventArgs args)
        {
            base.OnFileNotFound(args);

            _Report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "Missing file/directory", args.FullPath, false);
        }
    }

}
