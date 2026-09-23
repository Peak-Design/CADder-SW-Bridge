using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Peak.Cadder.Bridge;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The progress dialog of a send. It has no close box, because the only
    /// way out is the worker finishing. Alt+F4 still closed it, and then
    /// SolidWorks waited for the worker with no window and no message loop,
    /// for minutes, and looked hung. A user who sees a hung SolidWorks
    /// kills it and loses the unsaved work.
    /// </summary>
    public class ProgressDialogTests
    {
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_F4 = 0x73;

        private static ProgressDialog Find()
        {
            for (int i = 0; i < 200; i++)
            {
                foreach (Form form in Application.OpenForms)
                {
                    var dialog = form as ProgressDialog;
                    if (dialog != null && dialog.IsHandleCreated) return dialog;
                }
                Thread.Sleep(20);
            }
            return null;
        }

        [Fact]
        public void AltF4DoesNotCloseTheDialogWhileTheWorkerRuns()
        {
            bool stillOpen = false;
            string result = null;
            Exception failure = null;
            var ui = new Thread(() =>
            {
                try
                {
                    result = ProgressDialog.Run(null, "Test", "Working", () =>
                    {
                        var dialog = Find();
                        if (dialog == null) return "no dialog";
                        dialog.Invoke(new Action(() => dialog.Location = new Point(-20000, -20000)));
                        // What Alt+F4 sends. The window turns it into a close
                        // request with the reason UserClosing.
                        dialog.Invoke(new Action(() => SendMessage(
                            dialog.Handle, WM_SYSKEYDOWN, (IntPtr)VK_F4, (IntPtr)(1 << 29))));
                        Thread.Sleep(200);
                        // A closed dialog has no window left to ask.
                        try
                        {
                            stillOpen = (bool)dialog.Invoke(new Func<bool>(() => dialog.Visible));
                        }
                        catch (InvalidOperationException) { stillOpen = false; }
                        return "done";
                    });
                }
                catch (Exception ex) { failure = ex; }
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            Assert.True(ui.Join(TimeSpan.FromSeconds(30)), "the dialog did not close");
            Assert.Null(failure);
            Assert.Equal("done", result);
            Assert.True(stillOpen, "Alt+F4 closed the dialog while the worker ran");
        }

        [Fact]
        public void TheWorkerClosesTheDialog()
        {
            string result = null;
            var ui = new Thread(() => result = ProgressDialog.Run(null, "Test", "Working", () => "done"));
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            Assert.True(ui.Join(TimeSpan.FromSeconds(30)), "the dialog did not close");
            Assert.Equal("done", result);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
