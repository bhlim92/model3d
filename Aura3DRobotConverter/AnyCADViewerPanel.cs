using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AnyCAD.Foundation;

namespace Aura3DRobotConverter
{
    /// <summary>
    /// Windows Forms panel that hosts the AnyCAD native GPU 3D viewer.
    /// Used inside a WindowsFormsHost in WPF to get a raw HWND for AnyCAD to render into.
    /// </summary>
    public class AnyCADViewerPanel : Panel
    {
        public AnyCADViewerPanel()
        {
            // Prevent flicker and enable resize
            this.SetStyle(ControlStyles.AllPaintingInWmPaint
                        | ControlStyles.UserPaint
                        | ControlStyles.Opaque
                        | ControlStyles.ResizeRedraw, true);
            this.BackColor = System.Drawing.Color.FromArgb(0xEC, 0xEC, 0xEC);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { /* suppress GDI background paint */ }
        protected override void OnPaint(PaintEventArgs e) { /* AnyCAD owns this surface */ }
    }
}
