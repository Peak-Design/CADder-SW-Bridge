using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

/// <summary>
/// Calls into the CADder Bridge add-in (the assembly Peak.Cadder) that runs
/// in this SolidWorks. The CADder recipes use it to run the code of the
/// product itself: the export, the parts of the send, the readers.
///
/// A type name is the name after "Peak.Cadder.", for example
/// "Sw.MateReader" or "SendToBlenderCommand+SendJob" (a nested type). The
/// names are strings, so the Bridge test RecipeContractTests reads every
/// Call, Static, New and Get of these files and checks each name against
/// the Bridge. A rename in the Bridge then fails a test, not a lab session.
/// </summary>
public static class Cadder
{
    /// <summary>The class id of the add-in (AddIn.cs).</summary>
    public const string AddInClsid = "{5A19BED7-5BAE-4520-A820-99C7466C42AC}";

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

    private static Action<string> _log;

    /// <summary>The Bridge assembly that SolidWorks loaded.</summary>
    public static Assembly Assembly
    {
        get
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "Peak.Cadder") return a;
            throw new InvalidOperationException(
                "The CADder Bridge add-in (Peak.Cadder) is not loaded in this SolidWorks. "
                + "Turn it on in Tools > Add-ins, or register a build with Register-Addin.bat and start SolidWorks again.");
        }
    }

    /// <summary>A type of the Bridge by its name after "Peak.Cadder.".</summary>
    public static Type Type(string name)
    {
        var t = Assembly.GetType("Peak.Cadder." + name, false);
        if (t == null)
            throw new InvalidOperationException("The CADder Bridge has no type Peak.Cadder." + name + ". The recipe does not match this Bridge build.");
        return t;
    }

    /// <summary>Calls a static method. Optional parameters that are not given get their default values.</summary>
    public static object Call(string type, string method, params object[] args)
    {
        return Invoke(Type(type), null, method, args);
    }

    /// <summary>Calls an instance method of a Bridge object.</summary>
    public static object CallOn(object target, string method, params object[] args)
    {
        if (target == null) throw new ArgumentNullException("target", "No object to call " + method + " on.");
        return Invoke(target.GetType(), target, method, args);
    }

    /// <summary>Makes a Bridge object with the constructor that fits the arguments.</summary>
    public static object New(string type, params object[] args)
    {
        var t = Type(type);
        args = args ?? new object[0];
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var full = Fill(c.GetParameters(), args);
            if (full == null) continue;
            try { return c.Invoke(full); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
        throw new InvalidOperationException("Peak.Cadder." + type + " has no constructor for " + args.Length + " argument(s).");
    }

    /// <summary>The value of a static field or property.</summary>
    public static object Static(string type, string member)
    {
        return Read(Type(type), null, member);
    }

    /// <summary>The value of a field or property of a Bridge object.</summary>
    public static object Get(object target, string member)
    {
        if (target == null) throw new ArgumentNullException("target", "No object to read " + member + " from.");
        return Read(target.GetType(), target, member);
    }

    /// <summary>Sets a field or property of a Bridge object.</summary>
    public static void Set(object target, string member, object value)
    {
        var t = target.GetType();
        var f = t.GetField(member, Any);
        if (f != null) { f.SetValue(target, value); return; }
        var p = t.GetProperty(member, Any);
        if (p != null && p.CanWrite) { p.SetValue(target, value, null); return; }
        throw new InvalidOperationException(t.FullName + " has no field or property " + member + " to set.");
    }

    /// <summary>The log of the Bridge (AddIn.Log), for the parameters that take one.</summary>
    public static Action<string> Log
    {
        get
        {
            if (_log == null)
            {
                var m = Type("AddIn").GetMethod("Log", Any, null, new[] { typeof(string) }, null);
                if (m == null) throw new InvalidOperationException("The CADder Bridge has no AddIn.Log(string).");
                _log = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), m);
            }
            return _log;
        }
    }

    /// <summary>The path of the log file of the Bridge.</summary>
    public static string LogPath
    {
        get { return (string)Static("AddIn", "LogPath"); }
    }

    /// <summary>The length of the log now. Give it to LogSince to get the lines that come after.</summary>
    public static long LogMark()
    {
        try { return File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0; }
        catch (IOException) { return 0; }
    }

    /// <summary>The lines that the Bridge logged after the mark.</summary>
    public static List<string> LogSince(long mark)
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(LogPath)) return lines;
            using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (mark > stream.Length) mark = 0;
                stream.Seek(mark, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        if (line.Length > 0) lines.Add(line);
                }
            }
        }
        catch (IOException) { }
        return lines;
    }

    /// <summary>
    /// What the Bridge listener answers one request of Blender (op status,
    /// poses, retessellate or export), run here, in this process, with no
    /// HTTP. The fields are the fields that Blender sends.
    /// </summary>
    public static Dictionary<string, object> Ask(object app, string op, Dictionary<string, object> fields = null)
    {
        var request = fields == null ? new Dictionary<string, object>() : new Dictionary<string, object>(fields);
        request["op"] = op;
        return (Dictionary<string, object>)Call("Bridge.SwCommandHandler", "Handle", app, request);
    }

    /// <summary>
    /// Releases the SolidWorks objects that the Bridge code read (Sw.ComRelease).
    /// A render material released late, after its document closed, crashed
    /// SolidWorks (2026-09-23). Call it at the end of each method that ran
    /// the readers of the Bridge.
    /// </summary>
    public static void Flush()
    {
        try { Call("Sw.ComRelease", "Flush"); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// A Bridge object as plain data for a result: the public fields and
    /// properties, lists and dictionaries, with SolidWorks objects left out.
    /// </summary>
    public static object Plain(object value, int depth = 6)
    {
        if (value == null) return null;
        if (value is string || value is bool || value is char) return value;
        if (value is Enum) return value.ToString();
        // JSON has no NaN or infinity. The Bridge uses NaN for "not set".
        if (value is double && (double.IsNaN((double)value) || double.IsInfinity((double)value))) return null;
        if (value is float && (float.IsNaN((float)value) || float.IsInfinity((float)value))) return null;
        var type = value.GetType();
        if (type.IsPrimitive || value is decimal) return value;
        if (Marshal.IsComObject(value)) return null;
        var matrix = value as double[,];
        if (matrix != null)
        {
            var flat = new List<object>();
            foreach (var x in matrix) flat.Add(x);
            return flat;
        }
        if (depth <= 0) return value.ToString();
        var dict = value as System.Collections.IDictionary;
        if (dict != null)
        {
            var d = new Dictionary<string, object>();
            foreach (System.Collections.DictionaryEntry e in dict)
                d[Convert.ToString(e.Key, System.Globalization.CultureInfo.InvariantCulture)] = Plain(e.Value, depth - 1);
            return d;
        }
        var list = value as System.Collections.IEnumerable;
        if (list != null)
        {
            var l = new List<object>();
            foreach (var item in list)
            {
                if (l.Count >= 5000) { l.Add("..."); break; }
                l.Add(Plain(item, depth - 1));
            }
            return l;
        }
        var o = new Dictionary<string, object>();
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            o[f.Name] = Plain(f.GetValue(value), depth - 1);
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            try { o[p.Name] = Plain(p.GetValue(value, null), depth - 1); }
            catch (Exception ex) { o[p.Name] = "error: " + (ex.InnerException ?? ex).Message; }
        }
        return o;
    }

    /// <summary>A number of a MiniJson reply as a double, or the default.</summary>
    public static double Num(Dictionary<string, object> d, string key, double fallback = 0)
    {
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
        try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception) { return fallback; }
    }

    private static object Read(Type type, object target, string member)
    {
        var f = type.GetField(member, Any);
        if (f != null) return f.GetValue(target);
        var p = type.GetProperty(member, Any);
        if (p != null) return p.GetValue(target, null);
        throw new InvalidOperationException(type.FullName + " has no field or property " + member + ".");
    }

    private static object Invoke(Type type, object target, string method, object[] args)
    {
        args = args ?? new object[0];
        MethodInfo best = null;
        object[] bestArgs = null;
        foreach (var m in type.GetMethods(Any))
        {
            if (m.Name != method || m.IsStatic != (target == null)) continue;
            var full = Fill(m.GetParameters(), args);
            if (full == null) continue;
            // The overload with the fewest parameters that fits.
            if (best == null || m.GetParameters().Length < best.GetParameters().Length)
            {
                best = m;
                bestArgs = full;
            }
        }
        if (best == null)
            throw new InvalidOperationException(type.FullName + " has no " + (target == null ? "static " : "") + "method " + method
                + " for " + args.Length + " argument(s). The recipe does not match this Bridge build.");
        try
        {
            return best.Invoke(target, bestArgs);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>The full argument list for the parameters, or null when the arguments do not fit.</summary>
    private static object[] Fill(ParameterInfo[] ps, object[] args)
    {
        if (args.Length > ps.Length) return null;
        var full = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            if (i < args.Length)
            {
                if (!Fits(ps[i].ParameterType, args[i])) return null;
                full[i] = args[i];
            }
            else if (ps[i].IsOptional)
            {
                full[i] = ps[i].DefaultValue is DBNull ? System.Type.Missing : ps[i].DefaultValue;
            }
            else return null;
        }
        return full;
    }

    private static bool Fits(Type p, object arg)
    {
        if (p.IsByRef) p = p.GetElementType();
        if (arg == null) return !p.IsValueType || Nullable.GetUnderlyingType(p) != null;
        if (p.IsInstanceOfType(arg)) return true;
        // The Bridge embeds its interop types. A SolidWorks object fits an
        // interface parameter when COM says it has the interface.
        if (p.IsInterface && Marshal.IsComObject(arg)) return true;
        return false;
    }
}

public sealed partial class SwMcpScript
{
    /// <summary>The CADder Bridge in this SolidWorks: its version, the file it loaded from, its listener, its settings and its log file.</summary>
    private object CadderStatus()
    {
        var asm = Cadder.Assembly;
        string file = asm.Location;
        var settings = Cadder.Call("Core.AppSettings", "Load", Cadder.Log, null);
        int pid;
        using (var p = Process.GetCurrentProcess()) pid = p.Id;
        string registry = Path.Combine((string)Cadder.Static("Bridge.SwCommandServer", "RegistryDir"), pid + ".json");
        return new
        {
            version = Cadder.Static("AddIn", "AddInVersion"),
            file,
            built = File.Exists(file) ? File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm") : null,
            debugBuild = file.IndexOf(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) >= 0,
            listener = new
            {
                running = Cadder.Static("Bridge.SwCommandServer", "Running"),
                port = Cadder.Static("Bridge.SwCommandServer", "Port"),
                registryFile = File.Exists(registry) ? registry : null,
            },
            log = Cadder.LogPath,
            settings = Cadder.Plain(settings, 2),
        };
    }

    /// <summary>The last lines of the log of the CADder Bridge (cadder-debug.log).</summary>
    private object CadderLog(int lines = 50)
    {
        var all = Cadder.LogSince(0);
        int start = Math.Max(0, all.Count - Math.Max(1, lines));
        return new { path = Cadder.LogPath, lines = all.GetRange(start, all.Count - start) };
    }

    /// <summary>What the Bridge listener answers Blender for one request (op status, poses, retessellate or export), run in this process. Give the fields as Blender sends them, for example document_path.</summary>
    private object CadderAsk(string op, Dictionary<string, object> fields = null)
    {
        long mark = Cadder.LogMark();
        try
        {
            var reply = Cadder.Ask(swApp, op, fields);
            reply["log"] = Cadder.LogSince(mark);
            return reply;
        }
        finally { Cadder.Flush(); }
    }
}
