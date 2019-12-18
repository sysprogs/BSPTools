using BSPEngine;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VendorSampleParserEngine;

namespace VendorSampleReportViewer
{
    class ControllerImpl : INotifyPropertyChanged
    {
        RegistryKey _Key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Sysprogs\BSPTools\VendorSampleReportViewer");

        public event PropertyChangedEventHandler PropertyChanged;

        public ControllerImpl()
        {
            var folder = _Key.GetValue("LastReportFolder") as string;
            if (folder != null && Directory.Exists(folder))
                LoadReportsFromFolder(folder);
        }

        public class ReportSummary
        {
            public ReportSummary(VendorSampleTestReport r)
            {
                Version = r.BSPVersion;
                SucceededTests = r.Records.Count(rec => rec.LastSucceededPass == VendorSamplePass.Final);
                FailedTests = r.Records.Count(rec => rec.LastSucceededPass != VendorSamplePass.Final);
                ReleaseFailedTests = r.Records.Count(rec => rec.LastSucceededPass == VendorSamplePass.InPlaceBuild && rec.BuildFailedExplicitly);
            }

            public string Version { get; }
            public int SucceededTests { get; }
            public int FailedTests { get; }
            public int ReleaseFailedTests { get; }
        }

        public class SampleRow
        {
            public SampleRow(string id, VendorSampleTestReport[] lastReports)
            {
                Name = id.ToString();
                Cells = lastReports.Select(r => new SampleCell(id, r.ProvideEntryForSample(id))).ToArray();
            }

            public enum SampleState
            {
                Skipped,
                Succeeded,
                Failed,
                FailedInBSP
            }

            public class SampleCell
            {
                public SampleCell(string id, VendorSampleTestReport.Record record)
                {
                    SampleSubdir = id.ToString();
                    if (!record.BuildFailedExplicitly)
                    {
                        if (record.LastSucceededPass == VendorSamplePass.Final)
                            State = SampleState.Succeeded;
                        else
                            State = SampleState.Skipped;
                    }
                    else
                    {
                        KnownError = record.KnownProblemID;
                        if (record.LastSucceededPass == VendorSamplePass.InPlaceBuild)
                            State = SampleState.FailedInBSP;
                        else
                            State = SampleState.Failed;
                    }
                }

                public SampleState State { get; }
                public string KnownError { get; }

                public string SampleSubdir { get; }
            }

            public SampleCell[] Cells { get; }
            public string Name { get; }

            public int SortOrder => -(int)(Cells.LastOrDefault()?.State ?? SampleState.FailedInBSP);
        }

        public class CategoryRow
        {
            public string Name { get; }

            public class CategoryCell
            {
                public int Count { get; }

                public CategoryCell(VendorSampleTestReport r, string categoryID)
                {
                    Count = r.Records.Count(rec => rec.KnownProblemID == categoryID && rec.BuildFailedExplicitly);
                }
            }

            public CategoryCell[] Cells { get; }
            public int SortOrder => -(Cells.LastOrDefault()?.Count ?? 0);

            public CategoryRow(string categoryID, VendorSampleTestReport[] lastReports)
            {
                Name = categoryID;
                Cells = lastReports.Select(r => new CategoryCell(r, categoryID)).ToArray();
            }
        }

        protected virtual void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        ReportSummary[] _Summaries;
        public ReportSummary[] Summaries
        {
            get => _Summaries;
            set
            {
                _Summaries = value;
                OnPropertyChanged(nameof(Summaries));
            }
        }

        SampleRow[] _AllSampleRows;

        SampleRow[] _SampleRows;
        public SampleRow[] SampleRows
        {
            get => _SampleRows;
            set
            {
                _SampleRows = value;
                OnPropertyChanged(nameof(SampleRows));
            }
        }

        CategoryRow[] _CategoryRows;
        public CategoryRow[] CategoryRows
        {
            get => _CategoryRows;
            set
            {
                _CategoryRows = value;
                OnPropertyChanged(nameof(CategoryRows));
            }
        }

        void ApplyFilter()
        {
            SampleRows = _AllSampleRows.Where(r => r.Name.IndexOf(_Filter ?? "", StringComparison.InvariantCultureIgnoreCase) != -1).ToArray();
        }

        string _BSPName;
        public string BSPName
        {
            get => _BSPName;
            set
            {
                _BSPName = value;
                OnPropertyChanged(nameof(BSPName));
            }
        }

        string _Filter;
        public string Filter
        {
            get => _Filter;
            set
            {
                _Filter = value;
                OnPropertyChanged(nameof(Filter));
                ApplyFilter();
            }
        }

        public string BSPID { get; private set; }

        public void LoadReportsFromFolder(string folder)
        {
            var lastReports = Directory.GetFiles(folder, "*.xml")
                .Select(f => XmlTools.LoadObject<VendorSampleTestReport>(f))
                .OrderBy(r => r.BSPVersion, new VersionComparer())
                .Reverse()
                .Take(5)
                .Reverse()
                .ToArray();

            if (lastReports.Length == 0)
                return;

            BSPID = lastReports.Last().BSPID;
            Summaries = lastReports.Select(r => new ReportSummary(r)).ToArray();

            HashSet<string> allIDs = new HashSet<string>();
            HashSet<string> categoryIDs = new HashSet<string>();
            foreach (var rep in lastReports)
                foreach (var rec in rep.Records)
                {
                    allIDs.Add(rec.UniqueID);
                    if (rec.KnownProblemID != null)
                        categoryIDs.Add(rec.KnownProblemID);
                }

            _AllSampleRows = allIDs.Select(id => new SampleRow(id, lastReports)).OrderBy(r => r.SortOrder).ToArray();
            CategoryRows = categoryIDs.Select(id => new CategoryRow(id, lastReports)).OrderBy(r => r.SortOrder).ToArray();

            ApplyFilter();
            _Key.SetValue("LastReportFolder", folder);
        }
    }
}
