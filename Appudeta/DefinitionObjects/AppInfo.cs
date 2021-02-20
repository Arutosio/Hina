using System;
using System.Collections.Generic;
using System.Text;

namespace Appudeta
{
    class AppInfo
    {
        public string Name { get; private set; }
        public float Version { get; private set; }
        public string Path { get; private set; }
        public Uri Repository { get; private set; }

        public AppInfo(string name, float version, string path, Uri repository)
        {
            Name = name;
            Version = version;
            Path = path;
            Repository = repository;
        }
    }
}
