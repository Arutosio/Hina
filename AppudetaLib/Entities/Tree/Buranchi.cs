using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AppudetaLib.Entities.Tree
{
    public class Buranchi
    {
        [JsonPropertyOrder(1)]
        public string Name { get; set; }

        [JsonPropertyOrder(2)]
        public List<Buranchi> Branches { get; set; }

        [JsonPropertyOrder(3)]
        public List<Ha> Leaves { get; set; }

        public Buranchi(string name)
        {
            Name = name;
            Leaves = null;
        }
        public Buranchi(string name, List<Ha> leaves)
        {
            Name = name;
            Leaves = leaves;
        }
        public Buranchi(string name, List<Buranchi> branches)
        {
            Name = name;
            Branches = branches;
        }
        public Buranchi(string name, List<Buranchi> branches, List<Ha> leaves)
        {
            Name = name;
            Branches = branches;
            Leaves = leaves;
        }

        public List<string> Michi()
        {
            List<string> ret = new();
            foreach (Ha ha in Leaves)
            {
                ret.Add(Path.Combine(Name, ha.Name));
            }
            foreach (Buranchi faiba in Branches)
            {
                foreach (string haOnBurachi in faiba.Michi())
                {
                    ret.Add(Path.Combine(Name, haOnBurachi));
                }
            }
            return ret;
        }
    }
}
