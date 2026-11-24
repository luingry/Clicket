using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Clicket.Core;

/// <summary>
/// The macro library on disk (macros.json). Atomic writes keep a .bak; a file that does not parse is quarantined
/// as .corrupt-&lt;timestamp&gt; and the backup is used, so a bad write never silently empties the library.
/// </summary>
public static class MacroStore
{
    private static readonly DataContractJsonSerializer LibrarySerializer = new DataContractJsonSerializer(typeof(LibraryFile));
    private static readonly DataContractJsonSerializer MacroSerializer = new DataContractJsonSerializer(typeof(Macro));

    public static List<Macro> Load(string path, Action<string>? log = null)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var bytes = File.ReadAllBytes(candidate);
                if (bytes.Length == 0) continue;
                using var stream = new MemoryStream(bytes);
                var file = (LibraryFile)LibrarySerializer.ReadObject(stream);
                return Normalize(file.Macros);
            }
            catch (Exception e) when (e is SerializationException || e is System.Xml.XmlException || e is InvalidCastException || e is ArgumentException || e is FormatException)
            {
                log?.Invoke("Library file unreadable, quarantined: " + candidate + " (" + e.GetType().Name + ")");
                Quarantine(candidate);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                log?.Invoke("Library file could not be read: " + candidate + " (" + e.GetType().Name + ")");
            }
        }
        return new List<Macro>();
    }

    public static void Save(string path, IEnumerable<Macro> macros)
    {
        var file = new LibraryFile { Macros = new List<Macro>(macros) };
        AtomicFile.WriteAllText(path, Serialize(LibrarySerializer, file));
    }

    public static string ExportMacro(Macro macro) => Serialize(MacroSerializer, macro);

    /// <summary>A macro read from an exported file, with a fresh id so it never replaces an existing one.</summary>
    public static Macro? ImportMacro(string json)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var macro = (Macro)MacroSerializer.ReadObject(stream);
            var list = Normalize(new List<Macro> { macro });
            if (list.Count == 0 || list[0].Name.Length == 0 && list[0].Steps.Count == 0) return null;
            list[0].Id = Guid.NewGuid().ToString("N");
            return list[0];
        }
        catch (Exception e) when (e is SerializationException || e is System.Xml.XmlException || e is InvalidCastException || e is ArgumentException || e is FormatException)
        {
            return null;
        }
    }

    private static string Serialize(DataContractJsonSerializer serializer, object value)
    {
        using var stream = new MemoryStream();
        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, new UTF8Encoding(false), ownsStream: false, indent: true))
        {
            serializer.WriteObject(writer, value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Deserialization skips constructors, so missing fields arrive null: give every macro a usable shape.</summary>
    private static List<Macro> Normalize(List<Macro>? macros)
    {
        var result = new List<Macro>();
        var ids = new HashSet<string>();
        if (macros == null) return result;
        foreach (var macro in macros)
        {
            if (macro == null) continue;
            if (string.IsNullOrWhiteSpace(macro.Id) || !ids.Add(macro.Id)) macro.Id = Guid.NewGuid().ToString("N");
            ids.Add(macro.Id);
            macro.Name ??= "";
            macro.Steps.RemoveAll(step => step == null);
            result.Add(macro);
        }
        return result;
    }

    private static void Quarantine(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // Left in place; the backup is still tried.
        }
    }
}
