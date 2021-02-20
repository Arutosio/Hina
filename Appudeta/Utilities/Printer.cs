using System;

namespace Appudeta.Utilities
{
    public static class Printer
    {
        public static ConsoleColor defaultColor = ConsoleColor.White;

        public static void Print(ConsoleColor c, string strColor)
        {
            Console.ForegroundColor = c;
            Console.Write(strColor);
            Console.ForegroundColor = defaultColor;
        }

        public static void Print(ConsoleColor c, string strColor, string strStandard)
        {
            Console.ForegroundColor = c;
            Console.Write(strColor);
            Console.ForegroundColor = defaultColor;
            Console.Write(strStandard);
        }

        public static void Print(string strStandard, ConsoleColor c, string strColor)
        {
            Console.Write(strStandard);
            Console.ForegroundColor = c;
            Console.Write(strColor);
            Console.ForegroundColor = defaultColor;
        }

        public static void PrintLine(ConsoleColor c, string strColor)
        {
            Console.ForegroundColor = c;
            Console.WriteLine(strColor);
            Console.ForegroundColor = defaultColor;
        }

        public static void PrintLine(ConsoleColor c, string strColor, string strStandard)
        {
            Console.ForegroundColor = c;
            Console.Write(strColor);
            Console.ForegroundColor = defaultColor;
            Console.WriteLine(strStandard);
        }

        public static void PrintLine(string strStandard, ConsoleColor c, string strColor)
        {
            Console.Write(strStandard);
            Console.ForegroundColor = c;
            Console.WriteLine(strColor);
            Console.ForegroundColor = defaultColor;
        }

        public static void PrintStringPatter(string str, bool newLine = true)
        {
            string[] strStep;
            if (str.Contains('║'))
            {
                strStep = str.Split('║');
            }
            else
            {
                strStep = new string[] { str };
            }
            foreach (string s in strStep)
            {
                string[] strCP = s.Split('╠');
                try
                {
                    ConsoleColor c = defaultColor;
                    if (strCP.Length == 2)
                    {
                        c = (ConsoleColor)Enum.Parse(typeof(ConsoleColor), strCP[0]);
                        Print(c, strCP[1]);
                    }
                    else
                    {
                        Print(c, strCP[0]);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
            Console.WriteLine();
        }
    }
}