using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace RenesasToolchainManager
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new RenesasToolchainController();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        static string BrowseForToolDir(string filter)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = filter
            };

            if (dlg.ShowDialog() == true)
                return System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(dlg.FileName));
            else
                return null;
        }

        private void BrowseGCC_Click(object sender, RoutedEventArgs e)
        {
            var dir = BrowseForToolDir("RL78 GCC|rl78-elf-gcc.exe");
            if (dir != null)
                ((sender as FrameworkElement).DataContext as RenesasToolchainController.RenesasToolchain).GCCPath = dir;
        }

        private void BrowseE2_Click(object sender, RoutedEventArgs e)
        {
            var dir = BrowseForToolDir("E2 Studio|e2studio.exe");
            if (dir != null)
                ((sender as FrameworkElement).DataContext as RenesasToolchainController.RenesasToolchain).E2StudioPath = dir;
        }

        private void RemoveFromVisualGDB_Click(object sender, RoutedEventArgs e)
        {
            var tc = (sender as FrameworkElement)?.DataContext as RenesasToolchainController.RenesasToolchain;
            if (tc != null)
            {
                tc.CanEdit = false;

                new Thread(() =>
               {
                   try
                   {
                       tc.RemoveIntegration();
                        Dispatcher.BeginInvoke(new ThreadStart(() => MessageBox.Show("Successfully removed toolchain from VisualGDB.", Title, MessageBoxButton.OK, MessageBoxImage.Information)));
                   }
                   catch (Exception ex)
                   {
                       Dispatcher.BeginInvoke(new ThreadStart(() => MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error)));
                   }
                   finally
                   {
                       tc.CanEdit = true;
                   }
               }).Start();
            }
        }

        private void Integrate_Click(object sender, RoutedEventArgs e)
        {
            var tc = (sender as FrameworkElement)?.DataContext as RenesasToolchainController.RenesasToolchain;
            if (tc != null)
            {
                tc.CanEdit = false;

                new Thread(() =>
                {
                    try
                    {
                        tc.Integrate();
                        Dispatcher.BeginInvoke(new ThreadStart(() => MessageBox.Show("Successfully integrated toolchain with VisualGDB. Please restart Visual Studio in order to use it.", Title, MessageBoxButton.OK, MessageBoxImage.Information)));
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new ThreadStart(() => MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error)));
                    }
                    finally
                    {
                        tc.CanEdit = true;
                    }
                }).Start();

            }
        }
    }
}
