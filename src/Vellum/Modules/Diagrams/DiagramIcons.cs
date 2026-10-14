using Vellum.Modules.Modelling;

namespace Vellum.Modules.Diagrams;

/// <summary>
/// The icons an element can have on the canvas (ARCHITECTURE_ICONS in the web client), as the
/// SVG shapes of Lucide 1.21 on a 24 × 24 grid. Lucide is under the ISC licence. An unknown icon
/// name falls back to the default icon of the kind, as on the canvas.
/// </summary>
public static class DiagramIcons
{
    public static string For(string? icon, ElementKind kind) =>
        icon is not null && Shapes.TryGetValue(icon, out var shape) ? shape : Shapes[DefaultFor(kind)];

    private static string DefaultFor(ElementKind kind) => kind switch
    {
        ElementKind.Store => "database",
        ElementKind.App => "server",
        ElementKind.System => "monitor",
        ElementKind.Actor => "user",
        ElementKind.Component => "component",
        _ => "box",
    };

    private static readonly Dictionary<string, string> Shapes = new(StringComparer.Ordinal)
    {
        ["database"] = "<ellipse cx='12' cy='5' rx='9' ry='3'/><path d='M3 5V19A9 3 0 0 0 21 19V5'/><path d='M3 12A9 3 0 0 0 21 12'/>",
        ["server"] = "<rect width='20' height='8' x='2' y='2' rx='2' ry='2'/><rect width='20' height='8' x='2' y='14' rx='2' ry='2'/><line x1='6' x2='6.01' y1='6' y2='6'/><line x1='6' x2='6.01' y1='18' y2='18'/>",
        ["monitor"] = "<rect width='20' height='14' x='2' y='3' rx='2'/><line x1='8' x2='16' y1='21' y2='21'/><line x1='12' x2='12' y1='17' y2='21'/>",
        ["user"] = "<path d='M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2'/><circle cx='12' cy='7' r='4'/>",
        ["component"] = "<path d='M15.536 11.293a1 1 0 0 0 0 1.414l2.376 2.377a1 1 0 0 0 1.414 0l2.377-2.377a1 1 0 0 0 0-1.414l-2.377-2.377a1 1 0 0 0-1.414 0z'/><path d='M2.297 11.293a1 1 0 0 0 0 1.414l2.377 2.377a1 1 0 0 0 1.414 0l2.377-2.377a1 1 0 0 0 0-1.414L6.088 8.916a1 1 0 0 0-1.414 0z'/><path d='M8.916 17.912a1 1 0 0 0 0 1.415l2.377 2.376a1 1 0 0 0 1.414 0l2.377-2.376a1 1 0 0 0 0-1.415l-2.377-2.376a1 1 0 0 0-1.414 0z'/><path d='M8.916 4.674a1 1 0 0 0 0 1.414l2.377 2.376a1 1 0 0 0 1.414 0l2.377-2.376a1 1 0 0 0 0-1.414l-2.377-2.377a1 1 0 0 0-1.414 0z'/>",
        ["box"] = "<path d='M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z'/><path d='m3.3 7 8.7 5 8.7-5'/><path d='M12 22V12'/>",
        ["globe"] = "<circle cx='12' cy='12' r='10'/><path d='M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20'/><path d='M2 12h20'/>",
        ["cloud"] = "<path d='M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z'/>",
        ["shield"] = "<path d='M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z'/>",
        ["zap"] = "<path d='M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z'/>",
        ["cpu"] = "<path d='M12 20v2'/><path d='M12 2v2'/><path d='M17 20v2'/><path d='M17 2v2'/><path d='M2 12h2'/><path d='M2 17h2'/><path d='M2 7h2'/><path d='M20 12h2'/><path d='M20 17h2'/><path d='M20 7h2'/><path d='M7 20v2'/><path d='M7 2v2'/><rect x='4' y='4' width='16' height='16' rx='2'/><rect x='8' y='8' width='8' height='8' rx='1'/>",
        ["hard-drive"] = "<path d='M10 16h.01'/><path d='M2.212 11.577a2 2 0 0 0-.212.896V18a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-5.527a2 2 0 0 0-.212-.896L18.55 5.11A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z'/><path d='M21.946 12.013H2.054'/><path d='M6 16h.01'/>",
        ["smartphone"] = "<rect width='14' height='20' x='5' y='2' rx='2' ry='2'/><path d='M12 18h.01'/>",
        ["mail"] = "<path d='m22 7-8.991 5.727a2 2 0 0 1-2.009 0L2 7'/><rect x='2' y='4' width='20' height='16' rx='2'/>",
        ["lock"] = "<rect width='18' height='11' x='3' y='11' rx='2' ry='2'/><path d='M7 11V7a5 5 0 0 1 10 0v4'/>",
        ["key"] = "<path d='m15.5 7.5 2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4'/><path d='m21 2-9.6 9.6'/><circle cx='7.5' cy='15.5' r='5.5'/>",
        ["wifi"] = "<path d='M12 20h.01'/><path d='M2 8.82a15 15 0 0 1 20 0'/><path d='M5 12.859a10 10 0 0 1 14 0'/><path d='M8.5 16.429a5 5 0 0 1 7 0'/>",
        ["file-text"] = "<path d='M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z'/><path d='M14 2v5a1 1 0 0 0 1 1h5'/><path d='M10 9H8'/><path d='M16 13H8'/><path d='M16 17H8'/>",
        ["folder"] = "<path d='M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z'/>",
        ["git-branch"] = "<path d='M15 6a9 9 0 0 0-9 9V3'/><circle cx='18' cy='6' r='3'/><circle cx='6' cy='18' r='3'/>",
        ["layers"] = "<path d='M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z'/><path d='M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12'/><path d='M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17'/>",
        ["network"] = "<rect x='16' y='16' width='6' height='6' rx='1'/><rect x='2' y='16' width='6' height='6' rx='1'/><rect x='9' y='2' width='6' height='6' rx='1'/><path d='M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3'/><path d='M12 12V8'/>",
        ["terminal"] = "<path d='M12 19h8'/><path d='m4 17 6-6-6-6'/>",
        ["code"] = "<path d='m16 18 6-6-6-6'/><path d='m8 6-6 6 6 6'/>",
        ["cog"] = "<path d='M11 10.27 7 3.34'/><path d='m11 13.73-4 6.93'/><path d='M12 22v-2'/><path d='M12 2v2'/><path d='M14 12h8'/><path d='m17 20.66-1-1.73'/><path d='m17 3.34-1 1.73'/><path d='M2 12h2'/><path d='m20.66 17-1.73-1'/><path d='m20.66 7-1.73 1'/><path d='m3.34 17 1.73-1'/><path d='m3.34 7 1.73 1'/><circle cx='12' cy='12' r='2'/><circle cx='12' cy='12' r='8'/>",
        ["bell"] = "<path d='M10.268 21a2 2 0 0 0 3.464 0'/><path d='M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326'/>",
        ["search"] = "<path d='m21 21-4.34-4.34'/><circle cx='11' cy='11' r='8'/>",
        ["bar-chart"] = "<path d='M5 21v-6'/><path d='M12 21V9'/><path d='M19 21V3'/>",
        ["clock"] = "<circle cx='12' cy='12' r='10'/><path d='M12 6v6l4 2'/>",
        ["archive"] = "<rect width='20' height='5' x='2' y='3' rx='1'/><path d='M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8'/><path d='M10 12h4'/>",
    };
}
