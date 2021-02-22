using System;
using Newtonsoft.Json;

namespace Appudeta.ObjectsDefinitions
{
    public class RepositoryInfo
    {
        private Status state;

        public RepositoryInfo(string name, float version, string pathLocal, Uri origin)
        {
            this.Name = name;
            this.Version = version;
            this.PathLocal = pathLocal;
            this.Origin = origin;
        }

        public RepositoryInfo(string json)
        {
            RepositoryInfo obj = JsonConvert.DeserializeObject<RepositoryInfo>(json);
        }

        public string Name { get; private set; }
        public float Version { get; private set; }
        public string PathLocal { get; private set; }
        public Uri Origin { get; private set; }
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