using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Appudeta.ObjectsDefinitions;

namespace Appudeta.Utilities
{
    public static class JsonReader
    {
        public static Object Serialize(object x)
        {
            return JsonSerializer.Serialize(x);
        }

        public static RepositoryInfo[] Deserialize(string json)
        {
            return JsonSerializer.Deserialize<RepositoryInfo[]>(json);
        }
    }
}
