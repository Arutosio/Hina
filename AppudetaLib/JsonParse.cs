using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using AppudetaLib.Entities.Tree;

namespace AppudetaLib
{
    public static class JsonParse
    {
        private static JsonSerializerOptions options = new JsonSerializerOptions
        {
            //ReferenceHandler = ReferenceHandler.Preserve,
            WriteIndented = true
        };

        public static string JsonTextReader()
        {
            return null;
        }

        public static string Serialize(object x)
        {
            return JsonSerializer.Serialize(x, options);
        }

        public static string SerializeHa(Ha ha)
        {
            return JsonSerializer.Serialize(ha, options);
        }
        public static Ha DeserializeHa(string json)
        {
            return JsonSerializer.Deserialize<Ha>(json);
        }

        public static string SerializeBuranchi(Buranchi buranchi)
        {
            return JsonSerializer.Serialize(buranchi, options);
        }
        public static Buranchi DeserializeBuranchi(string json)
        {
            return JsonSerializer.Deserialize<Buranchi>(json);
        }

        public static string SerializeToranku(Toranku toranku)
        {
            return JsonSerializer.Serialize(toranku, options);
        }

        public static Toranku DeserializeToranku(string json)
        {
            return JsonSerializer.Deserialize<Toranku>(json, options);
        }

        public static string SerializeShinrin(Shinrin shinrin)
        {
            return JsonSerializer.Serialize(shinrin, options);
        }
        public static Shinrin DeserializeShinrin(string json)
        {
            return JsonSerializer.Deserialize<Shinrin>(json);
        }
    }
}
