using System;
using System.Collections.Generic;
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
using Microsoft.Win32;

namespace TiXDSDebugPackage
{
    /// <summary>
    /// Interaction logic for XDSDebugMethodConfigurator.xaml
    /// </summary>
    public partial class XDSDebugMethodConfigurator : UserControl, ICustomDebugMethodConfigurator
    {
        private readonly XDSDebugController _DebugController;
        readonly LoadedBSP.LoadedDebugMethod _Method;
        readonly IBSPConfiguratorHost _Host;

        public XDSDebugMethodConfigurator(XDSDebugController debugController, LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            host.InstallStyles(this);

            InitializeComponent();
            _DebugController = debugController;
            _Method = method;
            _Host = host;

            SetConfiguration(null, default(KnownInterfaceInstance));
        }

        TiXDSSettingsEditor _Editor;

        public object Control => this;

        public object Configuration => _Editor?.Settings;

        public ICustomSettingsTypeProvider TypeProvider => _DebugController;

        public bool SupportsLiveVariables => false;

        public event EventHandler SettingsChanged;

        public void SetConfiguration(object configuration, KnownInterfaceInstance context)
        {
            var settings = configuration as TiXDSDebugSettings ?? new TiXDSDebugSettings();
            DataContext = _Editor = new TiXDSSettingsEditor(settings);
            _Editor.PropertyChanged += _Editor_PropertyChanged;
        }

        private void _Editor_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool TryFixSettingsFromStubError(IGDBStubInstance stub) => false;

        public string ValidateSettings() => null;

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            _Editor?.Browse();
        }

        private void AddNewResource(object sender, RoutedEventArgs e)
        {
            _Editor.FLASHResources.Add(new FLASHResource());
        }

        private void BrowseResource_Click(object sender, RoutedEventArgs e)
        {
            var rsrc = (sender as FrameworkElement)?.DataContext as FLASHResource;
            if (rsrc != null)
            {
                var dlg = new OpenFileDialog { Filter = "All files|*.*" };
                if (dlg.ShowDialog() == true)
                    rsrc.UpdatePath(dlg.FileName);
            }
        }

        private void RemoveResource_Click(object sender, RoutedEventArgs e)
        {
            var rsrc = (sender as FrameworkElement)?.DataContext as FLASHResource;
            if (rsrc != null)
                _Editor.FLASHResources.Remove(rsrc);
        }
    }
}
