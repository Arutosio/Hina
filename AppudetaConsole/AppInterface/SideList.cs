using System;

namespace AppudetaConsole.AppInterface
{
    public class SideList
    {
        public string Title { get; private set; }
        public string[] MiddleElements { get; private set; }
        public int SizeWidth { get; private set; }
        //public int SizeHeight { get; private set; }
        public char CharBorder { get; private set; }
        public ConsoleColor ConColor { get; private set; }
        public bool IsWithColorPattern { get; private set; }

        #region Costuctor
        public SideList()
        {
            Title = "List";
            SizeWidth = 20;
            //SizeHeight = 12;
            CharBorder = '█';
            ConColor = ConsoleColor.DarkMagenta;
            IsWithColorPattern = true;
        }
        public SideList(string title)
        {
            Title = title;
            SizeWidth = 20;
            //SizeHeight = 12;
            CharBorder = '█';
            ConColor = ConsoleColor.DarkMagenta;
            IsWithColorPattern = true;

        }
        public SideList(string title, int sizeSide, char charBorder, ConsoleColor c, bool isWithColorPattern)
        {
            Title = title;
            SizeWidth = sizeSide >= 5 ? sizeSide : 20;
            //SizeHeight = sizeHeight >= 8 ? sizeHeight : 12;
            CharBorder = charBorder;
            ConColor = c;
            IsWithColorPattern = isWithColorPattern;
        }
        #endregion Costuctor

        public string GetHeadLine()
        {
            string bBefore = string.Empty;
            string bAfter = string.Empty;
            int freeChar = (SizeWidth - Title.Length - 2);
            if (freeChar >= 2)
            {
                // Side
                bool isPari = (freeChar % 2) == 0;
                for (int i = 0; i < 2; i++)
                {
                    for (int j = 0; j < (freeChar / 2); j++)
                    {
                        if (i == 0)
                        {
                            bBefore = bBefore.Insert(0, CharBorder.ToString());
                        }
                        else
                        {
                            if (j == 0 && i == 1 && !isPari)
                            {
                                j = j + 1;
                            }
                            bAfter = bAfter.Insert(bAfter.Length, CharBorder.ToString());
                        }
                    }
                }
            }
            if (IsWithColorPattern)
            {
                return $"{ConColor.ToString()}╠{bBefore}║White╠>{Title}<║{ConColor.ToString()}╠{bAfter}║"; // ██>Programs<██
            }
            return $"{bBefore}>{Title}<{bAfter}║"; // ██>Programs<██
        }

        private string FixMiddileString(string strFix)
        {
            int limitLength = SizeWidth - 4;
            if (strFix.Length == limitLength)
            {
                return strFix;
            }
            else if (strFix.Length < limitLength)
            {
                int l = limitLength - strFix.Length;
                do
                {
                    strFix = strFix.Insert(strFix.Length, " ");
                } while (strFix.Length < limitLength);
            }
            else if (strFix.Length > limitLength)
            {
                strFix = strFix.Insert(limitLength - 2, ".. ");
                strFix = strFix.Substring(0, limitLength);
            }
            return strFix;
        }

        public string GetEmplyMiddleLine()
        {
            string res = string.Empty;

            for (int i = 0; i < (SizeWidth - 2); i++)
            {
                res = res.Insert(0, " ");
            }

            if (IsWithColorPattern)
            {
                res = $"{ConColor.ToString()}╠{CharBorder}║White╠{res}║{ConColor.ToString()}╠{CharBorder}║";
            }
            else
            {
                res = $"{CharBorder}{res}{CharBorder}║";
            }
            return res;
        }

        public string GetMiddleLine(string strMiddle)
        {
            int limitLength = SizeWidth - 4; // 4 = 2 Border 2 Space 
            string toPut = FixMiddileString(strMiddle);
            if (IsWithColorPattern)
            {
                toPut = $"{ConColor.ToString()}╠{CharBorder}║White╠ {toPut} ║{ConColor.ToString()}╠{CharBorder}║";
            }
            else
            {
                toPut = $"{CharBorder} {toPut} {CharBorder}║";
            }
            return toPut;
        }

        public string[] GetMiddleLine(string[] middleElements)
        {
            string[] res = new string[middleElements.Length];
            int limitLength = SizeWidth - 4; // 4 = 2 Border 2 Space 

            for (int i = 0; i < middleElements.Length; i++)
            {
                string toPut = FixMiddileString(middleElements[i]);
                if (IsWithColorPattern)
                {
                    res[i] = $"{ConColor.ToString()}╠{CharBorder}║White╠ {toPut} ║{ConColor.ToString()}╠{CharBorder}║";
                }
                else
                {
                    res[i] = $"{CharBorder} {toPut} {CharBorder}║";
                }
            }
            return res;
        }

        public string GetBottomLine()
        {
            string bottomLine = string.Empty;
            for (int j = 0; j < SizeWidth; j++)//
            {
                bottomLine = bottomLine.Insert(0, CharBorder.ToString());
            }

            if (IsWithColorPattern)
            {
                return $"{ConColor.ToString()}╠{bottomLine}"; // ██>Programs<██
            }
            return $"{bottomLine}"; // ██>Programs<██
        }
    }
}