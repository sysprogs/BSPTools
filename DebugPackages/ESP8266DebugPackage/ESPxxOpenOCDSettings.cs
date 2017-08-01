using OpenOCDPackage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using BSPEngine;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ESP8266DebugPackage
{
    [XmlType("com.visualgdb.edp.openocd.settings.espxx")]
    public class ESPxxOpenOCDSettings : OpenOCDSettings
    {
        public ESP8266BinaryImage.ParsedHeader FLASHSettings = new ESP8266BinaryImage.ParsedHeader();
        public bool FeedWatchdog;

        public FLASHResource[] FLASHResources;
    }

    public class FLASHResource : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Offset { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        internal void UpdatePath(string path)
        {
            Path = path;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
        }
    }

    public class ESPxxOpenOCDSettingsEditor : OpenOCDSettingsEditor
    {
        public ESPxxOpenOCDSettingsEditor(IBSPConfiguratorHost host, string baseDir, ESPxxOpenOCDSettings settings, KnownInterfaceInstance context)
            : base(host, baseDir, settings, context)
        {
            Device.SelectedItem = new ScriptSelector<QuickSetupDatabase.TargetDeviceFamily>.Item { Script = "target/esp32.cfg" };
            if (settings == null)
                ExplicitFrequencyEnabled = true;

            if (Settings.FLASHResources != null)
                foreach (var r in Settings.FLASHResources)
                    FLASHResources.Add(r);
            FLASHResources.CollectionChanged += (s, e) => { Settings.FLASHResources = FLASHResources.ToArray(); OnPropertyChanged(nameof(FLASHResources)); };
        }

        public new ESPxxOpenOCDSettings Settings => (ESPxxOpenOCDSettings)base.Settings;

        public ESP8266BinaryImage.ParsedHeader FLASHSettings => Settings.FLASHSettings;

        public ObservableCollection<FLASHResource> FLASHResources { get; } = new ObservableCollection<FLASHResource>();


        protected override string GetScriptDir(string baseDir)
        {
            return Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\OpenOCD\share\openocd\scripts"));
        }

        protected override void InsertResetAndHaltCommands(int idxLoad, QuickSetupDatabase.ProgrammingInterface iface, QuickSetupDatabase.TargetDeviceFamily device)
        {
/*            int idxHalt = Settings.StartupCommands.IndexOf("mon reset halt");
            if (idxHalt == -1)
                Settings.StartupCommands.Insert(idxLoad, "mon reset halt");*/
        }

        protected override bool SuppressCommandLineReset => true;
        protected override OpenOCDSettings CreateDefaultSettings() => new ESPxxOpenOCDSettings();

        public string FLASHVoltage
        {
            get
            {
                var cmdLine = ParsedCommandLine;
                return cmdLine.Items.OfType<OpenOCDCommandLine.CommandItem>().FirstOrDefault(c => c.Command.StartsWith("set ESP32_FLASH_VOLTAGE"))?.Command?.Split(' ')?.Last();
            }
            set
            {
                var cmdLine = ParsedCommandLine;
                var cmd = cmdLine.Items.OfType<OpenOCDCommandLine.CommandItem>().FirstOrDefault(c => c.Command.StartsWith("set ESP32_FLASH_VOLTAGE"));
                if (value == null || value == "none")
                {
                    if (cmd != null)
                        cmdLine.Items.Remove(cmd);
                }
                else
                {
                    int? idx = cmdLine.FindTargetScript();
                    if (!idx.HasValue)
                        idx = cmdLine.Items.Count;

                    if (cmd == null)
                        cmdLine.Items.Insert(idx.Value, cmd = new OpenOCDCommandLine.CommandItem());

                    cmd.Command = "set ESP32_FLASH_VOLTAGE " + value;
                }

                ApplyCommandLine(cmdLine);
            }
        }

        protected override void ApplyCommandLine(OpenOCDCommandLine cmdLine)
        {
            base.ApplyCommandLine(cmdLine);
            OnPropertyChanged(nameof(FLASHVoltage));
        }
    }
}
