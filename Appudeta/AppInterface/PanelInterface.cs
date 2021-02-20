using System;
using System.Text;
using Appudeta.AppInterface;
using Appudeta.Utilities;

namespace Appudeta
{
    public enum Status { Unknow = 7, Working = 14, Done = 10, Error = 12 }
    public class PanelInterface
    {
        public int ConsoleWidth { get; private set; }
        public int ConsoleHeight { get; private set; }
        public char CharBorder { get; private set; } // █▓
        public string Arrow { get; private set; }
        public string heading = string.Empty;
        public Title[] MiddleTitles { get; private set; }
        public ConsoleColor ColorBoder { get; private set; }
        public LeftSide LeftSide { get; private set; }
        public bool IsWithColorPattern { get; private set; }

        public string[] Lines { get; private set; }

        private string strAppInterface = string.Empty;

        public PanelInterface()
        {
            ConsoleWidth = 30;
            ConsoleHeight = 10;
            CharBorder = '█';
            Arrow = "~>";
            MiddleTitles = new Title[] { new Title("Program"), new Title("App"), new Title("PROPROPROPROPRO"), new Title("") };
            ColorBoder = ConsoleColor.Magenta;
            IsWithColorPattern = true;
            LeftSide = new LeftSide();
            Lines = new string[ConsoleHeight];
        }
        public PanelInterface(int consoleWidth, int consoleHeight, char charBorder, string arrow, Title[] titles, ConsoleColor colorBoder, bool isWithColorPattern, string titlePrograms, int sizeLeftSide)
        {
            ConsoleWidth = consoleWidth;
            ConsoleHeight = consoleHeight;
            CharBorder = charBorder;
            Arrow = arrow;
            MiddleTitles = titles;
            ColorBoder = colorBoder;
            IsWithColorPattern = isWithColorPattern;
            LeftSide = new LeftSide(titlePrograms, sizeLeftSide, charBorder, colorBoder, isWithColorPattern);
            Lines = new string[consoleHeight];
        }

        public string StrAppInterface
        {
            get { if (String.IsNullOrWhiteSpace(strAppInterface)) { return "StrAppInterface NOT Compone"; } return strAppInterface; }
            private set { strAppInterface = value; }
        }

        private void TopRow()
        {
            Lines[0] = this.LeftSide.GetHeadLine(); ;
        }
        private void MiddleRows()
        {
            string middleRows = string.Empty;
            int l = ConsoleHeight - 2;
            for (int i = 0; i < l; i++)
            {
                if (MiddleTitles.Length > i && i < l)
                {
                    Lines[i + 1] = LeftSide.GetMiddleLine(MiddleTitles[i].Name);
                }
                else
                {
                    Lines[i + 1] = LeftSide.GetEmplyMiddleLine();
                }
            }
            //return $"{middleRows}";
        }
        private void BottomRow()
        {
            Lines[Lines.Length - 1] = LeftSide.GetBottomLine();
        }
        public void BuilderStrAppInterface()
        {
            TopRow();
            MiddleRows();
            BottomRow();
            foreach (string line in Lines)
            {
                Printer.PrintStringPatter(line);
            }
        }
    }
}