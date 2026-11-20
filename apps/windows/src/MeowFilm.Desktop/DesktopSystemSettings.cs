using System.Runtime.InteropServices;

namespace MeowFilm.Desktop;

public static class DesktopSystemSettings
{
    public static int WheelScrollLines
    {
        get
        {
            if (!SystemParametersInfo(0x68, 0, out uint lines, 0)) throw new System.ComponentModel.Win32Exception();
            return unchecked((int)lines);
        }
    }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
