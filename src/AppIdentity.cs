using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MonitorControl
{
    internal static class AppIdentity
    {
        internal static readonly uint ShowMessage = RegisterWindowMessage("LumaDock.ShowWindow.v1");
        internal static Icon LoadIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MonitorControl.ico"))
            using (var icon = new Icon(stream, 32, 32)) return (Icon)icon.Clone();
        }
        internal static BitmapFrame WindowIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MonitorControl.ico"))
                return new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(x => x.PixelWidth).First();
        }
        internal static void ShowExisting()
        {
            IntPtr handle = FindWindow(null, "LumaDock");
            if (handle != IntPtr.Zero) PostMessage(handle, ShowMessage, IntPtr.Zero, IntPtr.Zero);
        }
        internal static void Hook(Window window, Action show)
        {
            window.SourceInitialized += delegate {
                HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(delegate(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) {
                    if ((uint)msg == ShowMessage) { show(); handled = true; }
                    return IntPtr.Zero;
                });
            };
        }
        internal static void DarkTitlebar(Window window)
        {
            window.SourceInitialized += delegate {
                int enabled = 1; IntPtr handle = new WindowInteropHelper(window).Handle;
                if (DwmSetWindowAttribute(handle, 20, ref enabled, 4) != 0) DwmSetWindowAttribute(handle, 19, ref enabled, 4);
            };
        }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    }
}
