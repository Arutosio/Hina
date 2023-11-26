using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using HinaLib.Entities.Tree;
using HinaLib.Entities.Vitis;

namespace HinaLib
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

        // Ha
        public static string SerializeHa(Ha ha)
        {
            return JsonSerializer.Serialize(ha, options);
        }
        public static Ha DeserializeHa(string json)
        {
            return JsonSerializer.Deserialize<Ha>(json);
        }

        // Buranchi
        public static string SerializeBuranchi(Buranchi buranchi)
        {
            return JsonSerializer.Serialize(buranchi, options);
        }
        public static Buranchi DeserializeBuranchi(string json)
        {
            return JsonSerializer.Deserialize<Buranchi>(json);
        }

        // Toranku
        public static string SerializeToranku(Toranku toranku)
        {
            return JsonSerializer.Serialize(toranku, options);
        }
        public static Toranku DeserializeToranku(string json)
        {
            return JsonSerializer.Deserialize<Toranku>(json, options);
        }

        // Acino
        public static string SerializeAcino(Acino acino)
        {
            return JsonSerializer.Serialize(acino, options); ;
        }

        public static Acino DeserializeAcino(string json)
        {
            return JsonSerializer.Deserialize<Acino>(json, options);
        }

        // Grappolo
        public static string SerializeGrappolo(Grappolo grappolo)
        {
            return JsonSerializer.Serialize(grappolo, options);;
        }

        public static Grappolo DeserializeGrappolo(string json)
        {
            return JsonSerializer.Deserialize<Grappolo>(json, options);
        }

        // Shinrin
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
