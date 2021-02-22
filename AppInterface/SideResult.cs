using System;
using System.Collections.Generic;

namespace Appudeta.AppInterface
{
    public class SideResult
    {
        public string Title { get; private set; }
        public List<string> ResultRows { get; private set; }
        public int SizeWidth { get; private set; }
        public int NumMiddleRows { get; private set; }
        public char CharBorder { get; private set; }
        public ConsoleColor ConColor { get; private set; }
        public bool IsWithColorPattern { get; private set; }

        #region Costuctor
        public SideResult()
        {
            Title = "Proces Result";
            SizeWidth = 48;
            NumMiddleRows = 10 - 2;
            ResultRows = new List<string>(NumMiddleRows - 2);
            CharBorder = '█';
            ConColor = ConsoleColor.DarkMagenta;
            IsWithColorPattern = true;
        }
        public SideResult(string title)
        {
            Title = title;
            SizeWidth = 48;
            NumMiddleRows = 10 - 2;
            ResultRows = new List<string>(NumMiddleRows - 2);
            CharBorder = '█';
            ConColor = ConsoleColor.DarkMagenta;
            IsWithColorPattern = true;
        }
        public SideResult(string title, int sizeWidth, int sizeHeight, char charBorder, ConsoleColor c, bool isWithColorPattern)
        {
            Title = title;
            SizeWidth = sizeWidth >= 32 ? sizeWidth : 48;
            NumMiddleRows = sizeHeight - 2;
            ResultRows = new List<string>(NumMiddleRows - 2);
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
                return $"{ConColor.ToString()}╠{bBefore}║White╠>{Title}<║{ConColor.ToString()}╠{bAfter}║";
            }
            return $"{bBefore}>{Title}<{bAfter}║";
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

        public string GetBottomLine()
        {
            string bottomLine = string.Empty;
            for (int j = 0; j < SizeWidth; j++)
            {
                bottomLine = bottomLine.Insert(0, CharBorder.ToString());
            }

            if (IsWithColorPattern)
            {
                return $"{ConColor.ToString()}╠{bottomLine}";
            }
            return $"{bottomLine}";
        }

        public void AddRow(string strToAdd)
        {
            int freeRow = (NumMiddleRows - 2);
            int freeChar = (SizeWidth - 2);
            if (strToAdd.Length < freeChar)
            {
                ResultRows.Insert(0, strToAdd);
            }
            else
            {
                foreach (string line in strToAdd.Split())
                {
                    ResultRows.Insert(0, $"::{strToAdd}");
                }
            }
            while (ResultRows.Count > freeRow)
            {
                ResultRows.RemoveAt(ResultRows.Count - 1);
            }
        }
    }
}