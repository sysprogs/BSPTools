using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace VendorSampleReportViewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            DataContext = _Controller = new ControllerImpl();
        }

        ControllerImpl _Controller;

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Open report files", Filter = "XML Reports|*.xml" };
            if (dlg.ShowDialog() == true)
            {
                _Controller.LoadReportsFromFolder(System.IO.Path.GetDirectoryName(dlg.FileName));
            }
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ctx = (sender as FrameworkElement).DataContext as ControllerImpl.SampleRow.SampleCell ?? throw new Exception("Invalid data context");
                var testDir = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Sysprogs\BSPTools\VendorSampleParsers").GetValue("TestDirectory") as string;
                foreach(var subdir in System.IO.Directory.GetDirectories(System.IO.Path.Combine(testDir, _Controller.BSPID)))
                {
                    var file = subdir + $@"\{ctx.SampleSubdir}\build.log";
                    if (System.IO.File.Exists(file))
                    {
                        Process.Start(file);
                    }
                }
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message, "Vendor Sample Result Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
