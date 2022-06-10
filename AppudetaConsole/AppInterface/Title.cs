using System;
using AppudetaLib.Entities;

namespace AppudetaConsole.AppInterface
{
    public class Title
    {
        public string Name { get; set; }
        public ConsoleColor Color { get; private set; }
        private Status state;

        public Title(string name)
        {
            Name = name;
            State = Status.Unknow;
        }

        public Title(string name, Status state)
        {
            Name = name;
            State = state;
        }

        public Status State
        {
            get { return state; }
            set { state = value; Color = (ConsoleColor)value; }
        }

        public string ToStringPatter()
        {
            return $"{Color.ToString()}╠{Name}";
        }
    }
}