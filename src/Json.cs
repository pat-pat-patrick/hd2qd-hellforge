using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace Hd2Qd.HellForge
{
    /// <summary>Reading HellForge's JSON (JavaScriptSerializer: objects are dictionaries,
    /// arrays object[], numbers int, long or decimal) and writing the plugin's own files.</summary>
    static class Json
    {
        static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        /// <summary>The parsed text, or null when it is not JSON.</summary>
        public static object Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return Serializer().DeserializeObject(text); }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return null; }
        }

        public static Dictionary<string, object> Obj(string text) => Parse(text) as Dictionary<string, object>;

        public static string Write(object value) => Serializer().Serialize(value);

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static string Str(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as string : null;

        public static bool Bool(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v is bool b && b;

        public static bool? MaybeBool(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v is bool b ? b : (bool?)null;

        public static long Long(Dictionary<string, object> d, string key, long otherwise = 0)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return otherwise;
            try { return Convert.ToInt64(v); }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException) { return otherwise; }
        }

        public static List<object> List(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v is object[] a ? a.ToList() : new List<object>();

        public static List<Dictionary<string, object>> Objs(Dictionary<string, object> d, string key) =>
            List(d, key).OfType<Dictionary<string, object>>().ToList();

        public static List<string> Strs(Dictionary<string, object> d, string key) =>
            List(d, key).OfType<string>().ToList();
    }
}
