using System.Windows;

namespace Aura3DRobotConverter
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Force InvariantCulture for all file parsing (like STL ASCII parsing in HelixToolkit)
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            
            // Warm up AnyCAD OpenCASCADE modeling kernel
            AnyCAD.Foundation.GlobalInstance.Initialize();

            var mainWindow = new MainWindow();
            this.MainWindow = mainWindow;
            mainWindow.Show();

            if (e.Args.Length >= 2)
            {
                string command = e.Args[0];
                string file = e.Args[1];
                
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1000); // Wait for UI
                    
                    if (command == "--qa-urdf")
                    {
                        var task = await mainWindow.Dispatcher.InvokeAsync(async () => {
                            await mainWindow.LoadModelFromFileAsync(file);
                        });
                        await task;
                    }
                    else if (command == "--qa-step")
                    {
                        var task = await mainWindow.Dispatcher.InvokeAsync(async () => {
                            mainWindow.UpAxisComboBox.SelectedIndex = 0; // Force Y-Up 90 rotation test
                            await mainWindow.LoadStepFileAsync(file);
                        });
                        await task;
                    }
                    
                    await System.Threading.Tasks.Task.Delay(2000); // Wait 2 seconds for DirectX rendering to settle
                    
                    await mainWindow.Dispatcher.InvokeAsync(() => {
                        mainWindow.ExecuteQACapture(command);
                        // App stays open so TestAutomator can capture the screen
                    });
                });
            }
        }
    }

}
