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
    public abstract class ESPxxGDBStubSettingsBase
    {
        public string COMPort { get; set; }
        public int StubBaudRate { get; set; } = 74880;
        public int BootloaderBaudRate { get; set; } = 115200;
        public int BootloaderResetDelay { get; set; } = 50;
        public string BootloaderActivationSequence { get; set; } = "!DTR;RTS;SLEEP;DTR;!RTS;SLEEP;!DTR;SLEEP";

        public FLASHResource[] FLASHResources { get; set; }
        public ProgramMode ProgramMode { get; set; }

        public string InitDataFile { get; set; }
        public bool SuppressResetConfirmation { get; set; }
        public abstract ESP8266BinaryImage.IESPxxImageHeader GetFLASHSettings();
    }

    [XmlType("com.visualgdb.edp.espxx.settings.gdbstub")]
    public class ESP8266GDBStubSettings : ESPxxGDBStubSettingsBase, IESP8266Settings
    {
        public ESP8266BinaryImage.ESP8266ImageHeader FLASHSettings { get; set; } = new ESP8266BinaryImage.ESP8266ImageHeader();
        public override ESP8266BinaryImage.IESPxxImageHeader GetFLASHSettings() => FLASHSettings;
    }

    [XmlType("com.visualgdb.edp.espxx.settings.gdbstub.esp32")]
    public class ESP32GDBStubSettings : ESPxxGDBStubSettingsBase, IESP32Settings
    {
        public string AdditionalToolArguments { get; set; }
        public bool PatchBootloader { get; set; } = true;
        public ESP8266BinaryImage.ESP32ImageHeader FLASHSettings { get; set; } = new ESP8266BinaryImage.ESP32ImageHeader();

        public override ESP8266BinaryImage.IESPxxImageHeader GetFLASHSettings() => FLASHSettings;
    }


    public class ESPxxGDBStubSettingsEditor
    {
        readonly KnownInterfaceInstance _Context;
        readonly IBSPConfiguratorHost _Host;

        public ESPxxGDBStubSettingsBase Settings { get; private set; }
        public ObservableCollection<FLASHResource> FLASHResources { get; } = new ObservableCollection<FLASHResource>();

        public bool IsESP32 { get; }

        public string ExternalCOMPortSelectionHint { get; }

        public ESPxxGDBStubSettingsEditor(ESPxxGDBStubSettingsBase settings, KnownInterfaceInstance context, IBSPConfiguratorHost host, bool esp32Mode)
        {
            _Context = context;
            _Host = host;
            IsESP32 = esp32Mode;
            Settings = settings;
            if (Settings == null)
            {
                if (esp32Mode)
                    Settings = new ESP32GDBStubSettings();
                else
                    Settings = new ESP8266GDBStubSettings();
            }

            if (context.COMPortNumber.HasValue)
            {
                Settings.COMPort = "COM" + context.COMPortNumber;
                COMPortSelectorVisibility = Visibility.Collapsed;
            }
            else if (host.AdvancedModeContext is IExternallyProgrammableProjectDebugContext ectx)
            {
                ExternalCOMPortSelectionHint = ectx.ExternalProgrammingOptionHint;
                if (ExternalCOMPortSelectionHint != null)
                    COMPortSelectorVisibility = DirectFLASHProgrammingOptionVisibility = Visibility.Collapsed;
            }

            if (Settings.FLASHResources != null)
                foreach (var r in Settings.FLASHResources)
                    FLASHResources.Add(r);
            FLASHResources.CollectionChanged += (s, e) => { Settings.FLASHResources = FLASHResources.ToArray(); OnSettingsChanged(); };
        }

        public Visibility COMPortSelectorVisibility { get; private set; }
        public Visibility DirectFLASHProgrammingOptionVisibility { get; private set; } = Visibility.Visible;

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

        public string SelectedCOMPortText
        {
            get => Settings.COMPort;
            set
            {
                Settings.COMPort = value;
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
