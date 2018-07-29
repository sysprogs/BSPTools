using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using BSPEngine;

namespace AVaRICEDebugPackage.GUI
{
    /// <summary>
    /// Interaction logic for AVaRICESettignsControl.xaml
    /// </summary>
    internal partial class AVaRICESettingsControl : UserControl
    {
        public AVaRICESettingsControl(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host, AVaRICEDebugController controller)
        {
            InitializeComponent();
            DataContext = Controller = new ControllerImpl(this, method, host, controller);
        }

        public readonly ControllerImpl Controller;

        public class ControllerImpl : ICustomDebugMethodConfigurator, INotifyPropertyChanged
        {
            public ControllerImpl(AVaRICESettingsControl control, LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host, AVaRICEDebugController controller)
            {
                Control = control;
                TypeProvider = controller;
                _Settings = new AVaRICEDebugSettings();
            }

            Visibility _DebugAdapterVisibility;
            public Visibility DebugAdapterVisibility
            {
                get => _DebugAdapterVisibility;
                set
                {
                    _DebugAdapterVisibility = value;
                    OnPropertyChanged(nameof(DebugAdapterVisibility));
                }
            }


            protected void OnPropertyChanged(string name)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }

            public object Control { get; }

            AVaRICEDebugSettings _Settings;

            public object Configuration => _Settings;

            public ICustomSettingsTypeProvider TypeProvider { get; }

            public bool SupportsLiveVariables => false;

            public event EventHandler SettingsChanged;
            public event PropertyChangedEventHandler PropertyChanged;

            public void SetConfiguration(object configuration, KnownInterfaceInstance context)
            {
                if (configuration is AVaRICEDebugSettings settings)
                    _Settings = settings.Clone();
                else
                    _Settings = new AVaRICEDebugSettings();

                _Settings.PropertyChanged += (s, e) => SettingsChanged?.Invoke(s, e);

                OnPropertyChanged(nameof(Configuration));

                if (context.Device.InstanceID == null)
                    DebugAdapterVisibility = Visibility.Visible;
                else
                    DebugAdapterVisibility = Visibility.Collapsed;
            }

            public bool TryFixSettingsFromStubError(IGDBStubInstance stub)
            {
                return false;
            }

            public string ValidateSettings()
            {
                if (string.IsNullOrEmpty(_Settings.DebugAdapterType))
                    return "Please specify the debug adapter";
                if (_Settings.DebugInterface == null)
                    return "Please specify the debug interface";
                return null;
            }
        }
    }
}
