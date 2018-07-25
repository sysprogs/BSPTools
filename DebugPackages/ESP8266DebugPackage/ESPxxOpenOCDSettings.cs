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
using System.Windows;
using System.Globalization;

namespace ESP8266DebugPackage
{
    public interface IESP8266Settings
    {
        FLASHResource[] FLASHResources { get; set; }
        string InitDataFile { get; set; }
        ESP8266BinaryImage.ESP8266ImageHeader FLASHSettings { get; set; }
    }

    public interface IESP32Settings
    {
        bool PatchBootloader { get; set; }
    }

    public abstract class ESPxxOpenOCDSettings : OpenOCDSettings
    {
        public FLASHResource[] FLASHResources { get; set; }

        public abstract ESP8266BinaryImage.IESPxxImageHeader GetFLASHSettings();
    }

    [XmlType("com.visualgdb.edp.openocd.settings.esp32")]
    public class ESP32OpenOCDSettings : ESPxxOpenOCDSettings, IESP32Settings
    {
        public bool CheckFLASHSize { get; set; } = true;

        public ESP8266BinaryImage.ESP32ImageHeader FLASHSettings { get; set; } = new ESP8266BinaryImage.ESP32ImageHeader();
        public bool PatchBootloader { get; set; } = true;
        public override ESP8266BinaryImage.IESPxxImageHeader GetFLASHSettings() => FLASHSettings;
        public bool ProgramUsingIDF { get; set; }

    }

    [XmlType("com.visualgdb.edp.openocd.settings.esp8266")]
    public class ESP8266OpenOCDSettings : ESPxxOpenOCDSettings, IESP8266Settings
    {
        public ESP8266BinaryImage.ESP8266ImageHeader FLASHSettings { get; set; } = new ESP8266BinaryImage.ESP8266ImageHeader();
        public string InitDataFile { get; set; }
        public ResetMode ResetMode;

        public override ESP8266BinaryImage.IESPxxImageHeader GetFLASHSettings() => FLASHSettings;


        public int ProgramSectorSize = 4096;
        public int EraseSectorSize = 4096;
    }

    public enum ResetMode
    {
        [ArgumentValue("soft_reset", "Emulate a CPU reset (non-OTA only)")]
        Soft,
        [ArgumentValue("entry_point", "Jump to entry point")]
        JumpToEntry,
        [ArgumentValue("hard_reset", "Reset entire chip")]
        Hard,
    }

    public class FLASHResource : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Offset { get; set; }
        public bool Valid => !string.IsNullOrEmpty(Path) && !string.IsNullOrEmpty(Offset);

        public event PropertyChangedEventHandler PropertyChanged;

        internal void UpdatePath(string path)
        {
            Path = path;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
        }

        public ProgrammableRegion ToProgrammableRegion(IDebugStartService service)
        {
            return new ProgrammableRegion { FileName = service.ExpandProjectVariables(Path, true, true), Offset = ParseAddress(Offset) };
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
    }

    public class ESPxxOpenOCDSettingsEditor : OpenOCDSettingsEditor
    {
        public bool IsESP32 { get; }



        public ESPxxOpenOCDSettingsEditor(IBSPConfiguratorHost host, string baseDir, ESPxxOpenOCDSettings settings, KnownInterfaceInstance context, bool isESP32)
            : base(host, baseDir, settings ?? (isESP32 ? (OpenOCDSettings)new ESP32OpenOCDSettings() : new ESP8266OpenOCDSettings()), context)
        {
            IsESP32 = isESP32;

            _ESPIDFMode = host.MCU.Configuration.ContainsKey("com.sysprogs.esp32.idf.sdkconfig");
            if (host.AdvancedModeContext is IExternallyProgrammableProjectDebugContext ectx)
            {
                ExternalFLASHModeVisibility = Visibility.Visible;
                ExternallyProgrammableProjectDebugContext = ectx;
            }

            Device.SelectedItem = new ScriptSelector<QuickSetupDatabase.TargetDeviceFamily>.Item { Script = isESP32 ? "target/esp32.cfg" : "target/esp8266.cfg" };
            if (settings == null)
            {
                ExplicitFrequencyEnabled = true;
                if (!isESP32)
                {
                    AutofeedWatchdog = true;
                    NoInterruptsDuringSteps = true;
                }
            }

            var loadCommand = ProvideLoadCommand();
            var idx = Settings.StartupCommands.IndexOf("mon reset halt");
            if (idx < 0 || idx > loadCommand)
            {
                Settings.StartupCommands.Insert(loadCommand, "mon reset halt");
            }

            if (Settings.FLASHResources != null)
                foreach (var r in Settings.FLASHResources)
                    FLASHResources.Add(r);

            FLASHResources.CollectionChanged += (s, e) => { Settings.FLASHResources = FLASHResources.ToArray(); OnPropertyChanged(nameof(FLASHResources)); };
        }

        public new ESPxxOpenOCDSettings Settings => (ESPxxOpenOCDSettings)base.Settings;

        bool _ESPIDFMode;

        public Visibility ESP32Visibility => IsESP32 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ESP8266Visibility => !IsESP32 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FLASHSettingVisibility => _ESPIDFMode ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ESPIDFHintVisibility => _ESPIDFMode ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ESP32FLASHVisibility => (IsESP32 && !_ESPIDFMode) ? Visibility.Visible : Visibility.Collapsed;

        public IExternallyProgrammableProjectDebugContext ExternallyProgrammableProjectDebugContext { get; }

        public Visibility ExternalFLASHModeVisibility { get; } = Visibility.Collapsed;

        public ESP8266BinaryImage.IESPxxImageHeader FLASHSettings => Settings.GetFLASHSettings();

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

        void InsertAfterLoadCommand(string cmd)
        {
            int idxLoad = ProvideLoadCommand();
            Settings.StartupCommands.Insert(idxLoad + 1, cmd);
        }

        public bool NoInterruptsDuringSteps
        {
            get
            {
                return Settings.StartupCommands.Contains("mon xtensa_no_interrupts_during_steps on");
            }
            set
            {
                if (value == NoInterruptsDuringSteps)
                    return;

                if (value)
                    InsertAfterLoadCommand("mon xtensa_no_interrupts_during_steps on");
                else
                    Settings.StartupCommands.Remove("mon xtensa_no_interrupts_during_steps on");

                OnPropertyChanged(nameof(NoInterruptsDuringSteps));
                OnPropertyChanged(nameof(StartupCommands));
            }
        }

        public bool AutofeedWatchdog
        {
            get
            {
                return Settings.StartupCommands.Contains("mon esp8266_autofeed_watchdog on");
            }
            set
            {
                if (value == AutofeedWatchdog)
                    return;

                if (value)
                    InsertAfterLoadCommand("mon esp8266_autofeed_watchdog on");
                else
                    Settings.StartupCommands.Remove("mon esp8266_autofeed_watchdog on");

                OnPropertyChanged(nameof(AutofeedWatchdog));
                OnPropertyChanged(nameof(StartupCommands));
            }
        }

        public bool ProgramUsingIDF
        {
            get => (Settings as ESP32OpenOCDSettings)?.ProgramUsingIDF ?? false;
            set => (Settings as ESP32OpenOCDSettings).ProgramUsingIDF = value;
        }

        public const string DefaultInitDataFile = "$$SYS:BSP_ROOT$$/IoT-SDK/bin/esp_init_data_default.bin";

        public ESP8266OpenOCDSettings ESP8266Settings => (ESP8266OpenOCDSettings)Settings;

        public string InitDataFile
        {
            get => IsESP32 ? null : (ESP8266Settings.InitDataFile ?? DefaultInitDataFile);
            set
            {
                if (IsESP32)
                    return;

                if (value == DefaultInitDataFile)
                    ESP8266Settings.InitDataFile = null;
                else
                    ESP8266Settings.InitDataFile = value;
                OnPropertyChanged(nameof(InitDataFile));
            }
        }

        public ResetMode ResetMode
        {
            get => IsESP32 ? default(ResetMode) : ESP8266Settings.ResetMode;
            set
            {
                if (IsESP32)
                    return;

                ESP8266Settings.ResetMode = value;
                OnPropertyChanged(nameof(ResetMode));
            }
        }

        public int ProgramSectorSize
        {
            get => IsESP32 ? 0 : ESP8266Settings.ProgramSectorSize;
            set
            {
                if (!IsESP32)
                {
                    ESP8266Settings.ProgramSectorSize = value;
                    OnPropertyChanged(nameof(ProgramSectorSize));
                }
            }
        }

        public int EraseSectorSize
        {
            get => IsESP32 ? 0 : ESP8266Settings.EraseSectorSize;
            set
            {
                if (!IsESP32)
                {
                    ESP8266Settings.EraseSectorSize = value;
                    OnPropertyChanged(nameof(EraseSectorSize));
                }
            }
        }

        public bool ShowRTOSThreads
        {
            get
            {
                var cmdLine = ParsedCommandLine;
                var value = cmdLine.Items.OfType<OpenOCDCommandLine.CommandItem>().FirstOrDefault(c => c.Command.StartsWith("set ESP32_RTOS"))?.Command?.Split(' ')?.Last();
                return value != "none";
            }
            set
            {
                if (value == ShowRTOSThreads)
                    return;

                var cmdLine = ParsedCommandLine;
                var cmd = cmdLine.Items.OfType<OpenOCDCommandLine.CommandItem>().FirstOrDefault(c => c.Command.StartsWith("set ESP32_RTOS"));
                if (!value)
                {
                    int? idx = cmdLine.FindTargetScript();
                    if (!idx.HasValue)
                        idx = cmdLine.Items.Count;

                    if (cmd == null)
                        cmdLine.Items.Insert(idx.Value, cmd = new OpenOCDCommandLine.CommandItem());

                    cmd.Command = "set ESP32_RTOS none";
                }
                else
                {
                    if (cmd != null)
                        cmdLine.Items.Remove(cmd);
                }

                ApplyCommandLine(cmdLine);
            }
        }
    }

}
