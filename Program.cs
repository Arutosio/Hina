using System;
using System.Runtime.InteropServices;
using Appudeta.AppInterface;
using Appudeta.Utilities;

namespace Appudeta
{
    class Program
    {
        private const int MF_BYCOMMAND = 0x00000000;
        public const int SC_CLOSE = 0xF060;
        public const int SC_MINIMIZE = 0xF020;
        public const int SC_MAXIMIZE = 0xF030;
        public const int SC_SIZE = 0xF000;

        [DllImport("user32.dll")]
        public static extern int DeleteMenu(IntPtr hMenu, int nPosition, int wFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetConsoleWindow();

        static void Main(string[] args)
        {
            IntPtr handle = GetConsoleWindow();
            IntPtr sysMenu = GetSystemMenu(handle, false);

            if (handle != IntPtr.Zero)
            {
                DeleteMenu(sysMenu, SC_CLOSE, MF_BYCOMMAND);
                DeleteMenu(sysMenu, SC_MINIMIZE, MF_BYCOMMAND);
                DeleteMenu(sysMenu, SC_MAXIMIZE, MF_BYCOMMAND);
                DeleteMenu(sysMenu, SC_SIZE, MF_BYCOMMAND);
            }

            //StartApp
            HeadInfo();
            if (false)
            {
                Title[] ts = new Title[] { new Title("Program"), new Title("App"), new Title("PROPROPROPROPRO"), new Title("") };
                PanelInterface panelInterface = new(10, 10, '▓', "~>", ts, ConsoleColor.DarkMagenta, true, "APP LIST", "PROCESS", 20);
                panelInterface.BuilderStrAppInterface();
            }
            else
            {
                //FileManager.GetAllFiles(@"../");
                foreach (string f in FileManager.GetAll(@"./"))
                {
                    string str = FileManager.PrintType(f);
                    Printer.PrintStringPatter(str);
                }
            }

            Console.ReadKey();
        }

        public static void HeadInfo()
        {
            Printer.PrintLine("Program is developed by ", ConsoleColor.Magenta, "@Arutosio");
            Printer.PrintLine("Open Source: ", ConsoleColor.Cyan, "https://github.com/Arutosio/Appudeta");
        }
    }
}
