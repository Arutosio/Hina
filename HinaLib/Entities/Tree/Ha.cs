using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace HinaLib.Entities.Tree
{
    public class Ha
    {
        [JsonPropertyOrder(1)]
        public string Name { get; set; }

        [JsonPropertyOrder(2)]
        public float Version { get; set; }

        [JsonPropertyOrder(3)]
        public DateTime LastUpdate { get; set; }

        public Ha() { }

        public Ha(string name)
        {
            Name = name;
            LastUpdate = DateTime.Now;
        }

        public Ha(string name, float version, string lastUpdate)
        {
            Name = name;
            Version = version;
            LastUpdate = Convert.ToDateTime(lastUpdate);
        }

        public static Ha InstanceLeafFromPath(string path)
        {
            return new Ha(Path.GetFileName(path));
        }
    }
}
