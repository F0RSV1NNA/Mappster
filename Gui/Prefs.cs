namespace MapExtract.Gui;

/// Two lines next to the exe: the install folder and product that last opened cleanly,
/// plus the export folder. Only written on success, so a bad path never sticks.
public static class Prefs
{
    static string Path_ => Path.Combine(AppContext.BaseDirectory, "mappster.cfg");

    public static (string Install, string Product, string OutDir) Load()
    {
        if (!File.Exists(Path_)) return ("", "", "");
        var l = File.ReadAllLines(Path_);
        return (l.Length > 0 ? l[0] : "", l.Length > 1 ? l[1] : "", l.Length > 2 ? l[2] : "");
    }

    public static void Save(string install, string product, string outDir) =>
        File.WriteAllLines(Path_, [install, product, outDir]);
}
