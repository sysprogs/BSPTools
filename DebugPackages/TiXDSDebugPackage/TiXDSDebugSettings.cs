using BSPEngine;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace TiXDSDebugPackage
{
    public class TiXDSDebugSettings
    {
        public ProgramMode ProgramMode;

        public int ProgramTimeout { get; set; } = 15000;
        public FLASHResource[] FLASHResources;
    }

    public class FLASHResource : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Offset { get; set; }
        public bool Valid => !string.IsNullOrEmpty(Path) && !string.IsNullOrEmpty(Offset);

        [XmlIgnore]
        public string ExpandedPath;

        [XmlIgnore]
        public byte[] Data;

        public event PropertyChangedEventHandler PropertyChanged;

        internal void UpdatePath(string path)
        {
            Path = path;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
        }

        private int ParseAddress(string offset)
        {
            int r;
            if (int.TryParse(offset, out r))
                return r;
            if (offset.StartsWith("0x") && int.TryParse(offset.Substring(2), NumberStyles.HexNumber, null, out r))
                return r;
            throw new Exception($"Invalid address ({offset}) specified in the additional FLASH resources");
        }

        public int ParsedOffset => ParseAddress(Offset);
    }


    public class TiXDSSettingsEditor : INotifyPropertyChanged
    {
        const string RegistryPath = @"SOFTWARE\Sysprogs\VisualGDB\DebugMethods\TI_XDS\Settings";
        const string GDBAgentPath = "GDBAgentPath";
        public readonly TiXDSDebugSettings Settings;

        public ObservableCollection<FLASHResource> FLASHResources { get; } = new ObservableCollection<FLASHResource>();

        public TiXDSSettingsEditor(TiXDSDebugSettings settings)
        {
            Settings = settings ?? new TiXDSDebugSettings();

            if (Settings.FLASHResources != null)
                foreach (var r in Settings.FLASHResources)
                    FLASHResources.Add(r);

            FLASHResources.CollectionChanged += (s, e) => { Settings.FLASHResources = FLASHResources.ToArray(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FLASHResources))); };
        }

        public string GDBAgentExecutable
        {
            get => Registry.CurrentUser.OpenSubKey(RegistryPath)?.GetValue(GDBAgentPath) as string;
            set
            {
                var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key.SetValue(GDBAgentPath, value ?? "");
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GDBAgentExecutable)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ProgramMode ProgramMode
        {
            get => Settings.ProgramMode;
            set
            {
                Settings.ProgramMode = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgramMode)));
            }
        }

        public void Browse()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "TI GDB Agent Console|gdb_agent_console.exe",
                Title = "Locate Segger tools",
            };

            if (dlg.ShowDialog() == true)
            {
                GDBAgentExecutable = dlg.FileName;
            }
        }
    }
}
