using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Appudeta.ObjectsDefinitions
{
    public class RepositoryInfo
    {
        private Status state;

        public RepositoryInfo(string name, float version, string pathLocal, string origin)
        {
            this.Name = name;
            this.Version = version;
            this.PathLocal = pathLocal;
            this.Origin = origin;
        }

        public string Name { get; private set; }
        public float Version { get; private set; }
        public string PathLocal { get; private set; }
        public string Origin { get; private set; }
        public ConsoleColor Color { get; private set; }

        public Status State
        {
            get { return state; }
            set { state = value; Color = (ConsoleColor)value; }
        }

        public string NameToStringPatter()
        {
            return $"{Color.ToString()}╠{Name}";
        }
    }
}