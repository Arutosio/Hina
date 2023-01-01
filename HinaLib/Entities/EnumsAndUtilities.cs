using System;
using System.Drawing;

namespace HinaLib.Entities
{
    public enum Status { Done = 0, Error = 1, Warning = 2, Working = 4, Unknow = 3 }

    public class StatusUtilities
    {
        public static Color ColorOfStatus(Status status)
        {
            Color myRgbColor = new Color();
            switch (status)
            {
                case Status.Done:
                    myRgbColor = Color.FromArgb(0, 255, 0);
                    break;
                case Status.Error:
                    myRgbColor = Color.FromArgb(255, 0, 0);
                    break;
                case Status.Warning:
                    myRgbColor = Color.FromArgb(255, 120, 0);
                    break;
                case Status.Working:
                    myRgbColor = Color.FromArgb(255, 230, 0);
                    break;
                case Status.Unknow:
                    myRgbColor = Color.FromArgb(150, 150, 150);
                    break;
            }
            return myRgbColor;
        }
    }
}