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
            
            // Warm up AnyCAD OpenCASCADE modeling kernel
            AnyCAD.Foundation.GlobalInstance.Initialize();
        }
    }
}

