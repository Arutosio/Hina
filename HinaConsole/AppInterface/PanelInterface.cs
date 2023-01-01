using System;
using System.Text;
using HinaConsole.AppInterface;
using HinaLib;

namespace HinaConsole
{
    // public enum Status { Unknow = 7, Working = 14, Done = 10, Error = 12 }
    public class PanelInterface
    {
        public int ConsoleWidth { get; private set; }
        public int ConsoleHeight { get; private set; }
        public char CharBorder { get; private set; } // █▓
        public string Arrow { get; private set; }
        public ConsoleColor ColorBoder { get; private set; }
        public Title[] MiddleTitles { get; private set; }
        public SideList SideList { get; private set; }
        public SideResult SideResult { get; private set; }
        public bool IsWithColorPattern { get; private set; }
        public string[] Lines { get; private set; }

        #region Costuctor

        public PanelInterface()
        {
            ConsoleWidth = 30;
            ConsoleHeight = 10;
            CharBorder = '█';
            Arrow = "~>";
            MiddleTitles = new Title[] { new Title("Program"), new Title("App"), new Title("PROPROPROPROPRO"), new Title("") };
            ColorBoder = ConsoleColor.Magenta;
            IsWithColorPattern = true;
            SideList = new SideList();
            SideResult = new SideResult();
            Lines = new string[ConsoleHeight];
        }
        public PanelInterface(int consoleWidth, int consoleHeight, char charBorder, string arrow, Title[] titles, ConsoleColor colorBoder, bool isWithColorPattern
            , string titleList, string titleProces, int sizeLeftSide)
        {
            ConsoleWidth = consoleWidth;
            ConsoleHeight = consoleHeight;
            CharBorder = charBorder;
            Arrow = arrow;
            MiddleTitles = titles;
            ColorBoder = colorBoder;
            IsWithColorPattern = isWithColorPattern;
            SideList = new SideList(titleList, sizeLeftSide, charBorder, colorBoder, isWithColorPattern);
            SideResult = new SideResult(titleProces, 48, 16, charBorder, colorBoder, isWithColorPattern);
            Lines = new string[consoleHeight];
        }

        #endregion Costuctor

        private void TopRow()
        {
            Lines[0] = SideList.GetHeadLine();
            Lines[0] += SideResult.GetHeadLine();

        }
        private void MiddleRows()
        {
            string middleRows = string.Empty;
            int l = ConsoleHeight - 2;
            for (int i = 0; i < l; i++)
            {
                if (MiddleTitles.Length > i && i < l)
                {
                    Lines[i + 1] = SideList.GetMiddleLine(MiddleTitles[i].Name);
                }
                else
                {
                    Lines[i + 1] = SideList.GetEmplyMiddleLine();
                }

                if (SideResult.ResultRows.Count > i && i < l)
                {
                    Lines[i + 1] += SideResult.ResultRows[i];
                }
                else
                {
                    Lines[i + 1] += SideResult.GetEmplyMiddleLine();
                }
            }
            //return $"{middleRows}";
        }
        private void BottomRow()
        {
            Lines[Lines.Length - 1] = SideList.GetBottomLine();
            Lines[Lines.Length - 1] = SideResult.GetBottomLine();
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