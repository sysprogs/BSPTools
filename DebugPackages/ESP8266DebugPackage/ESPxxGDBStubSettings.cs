using BSPEngine;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Xml.Serialization;

namespace ESP8266DebugPackage
{
    [XmlType("com.visualgdb.edp.espxx.settings.gdbstub")]
    public class ESPxxGDBStubSettings : IESP8266Settings
    {
        public string COMPort { get; set; }
        public int StubBaudRate { get; set; } = 74880;
        public int BootloaderBaudRate { get; set; } = 115200;
        public int BootloaderResetDelay { get; set; } = 50;
        public string BootloaderActivationSequence { get; set; } = "!DTR;RTS;SLEEP;DTR;!RTS;SLEEP;!DTR;SLEEP";

        public ESP8266BinaryImage.ParsedHeader FLASHSettings { get; set; } = new ESP8266BinaryImage.ParsedHeader();
        public FLASHResource[] FLASHResources { get; set; }
        public ProgramMode ProgramMode { get; set; }

        public string InitDataFile { get; set; }
        public ResetMode ResetMode { get; set; }
        public bool SuppressResetConfirmation { get; set; }
    }

    [XmlType("com.visualgdb.edp.espxx.settings.gdbstub.esp32")]
    public class ESP32GDBStubSettings : ESPxxGDBStubSettings
    {
        public string AdditionalToolArguments { get; set; }
    }


    public class ESPxxGDBStubSettingsEditor
    {
        readonly KnownInterfaceInstance _Context;
        readonly IBSPConfiguratorHost _Host;

        public ESPxxGDBStubSettings Settings { get; private set; }
        public ObservableCollection<FLASHResource> FLASHResources { get; } = new ObservableCollection<FLASHResource>();

        public ESPxxGDBStubSettingsEditor(ESPxxGDBStubSettings settings, KnownInterfaceInstance context, IBSPConfiguratorHost host, bool esp32Mode)
        {
            _Context = context;
            _Host = host;
            Settings = settings;
            if (Settings == null)
            {
                if (esp32Mode)
                    Settings = new ESP32GDBStubSettings();
                else
                    Settings = new ESPxxGDBStubSettings();
            }

            if (context.COMPortNumber.HasValue)
            {
                Settings.COMPort = "COM" + context.COMPortNumber;
                COMPortSelectorVisibility = Visibility.Collapsed;
            }

            if (Settings.FLASHResources != null)
                foreach (var r in Settings.FLASHResources)
                    FLASHResources.Add(r);
            FLASHResources.CollectionChanged += (s, e) => { Settings.FLASHResources = FLASHResources.ToArray(); OnSettingsChanged(); };
        }

        public Visibility COMPortSelectorVisibility { get; private set; }

        public string ValidateSettings()
        {
            if (string.IsNullOrEmpty(Settings.COMPort) && !_Context.COMPortNumber.HasValue)
                return "Missing COM port for gdb stub";
            return null;
        }

        public event EventHandler SettingsChanged;
        private void OnSettingsChanged()
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public object SelectedCOMPort
        {
            get => new COMPortInfo { Port = Settings.COMPort};
            set
            {
                if (value is COMPortInfo cpi)
                    Settings.COMPort = cpi.Port;
                else if (value is string s)
                    Settings.COMPort = s;
                else
                    Settings.COMPort = null;

                OnSettingsChanged();
            }
        }

        public COMPortInfo[] COMPorts => _Host.GetAvailableCOMPorts();

        public string InitDataFile
        {
            get => Settings.InitDataFile ?? ESPxxOpenOCDSettingsEditor.DefaultInitDataFile;
            set
            {
                if (value == ESPxxOpenOCDSettingsEditor.DefaultInitDataFile)
                    Settings.InitDataFile = null;
                else
                    Settings.InitDataFile = value;
            }
        }
    }
}
