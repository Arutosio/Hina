using System;
using Appudeta.AppInterface;
using Appudeta.Utilities;

namespace Appudeta
{
    class Program
    {
        static void Main(string[] args)
        {

            //StartApp
            //PanelInterface panelInterface = new();
            Title[] ts = new Title[] { new Title("Program"), new Title("App"), new Title("PROPROPROPROPRO"), new Title("") };
            PanelInterface panelInterface = new(10, 10, '▓', "~>", ts, ConsoleColor.Blue, true, "App List", 20);

            panelInterface.BuilderStrAppInterface();
        }
    }
}
