using Dynamo.Core;

namespace Dynamo.Modules.Settings;

/// <summary>
/// Which sections show in the menu. A section can be hidden from Settings (kept
/// in the <c>setting</c> table); its pages still answer at their own address, so
/// nothing that points there breaks. Settings itself can never be hidden.
/// </summary>
public sealed class MenuOptions(SettingsRepository settings)
{
    private const string Prefix = "menu.hidden.";
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _hidden = [];

    public bool IsVisible(IModule module) => module.Id == "settings" || IsVisible(module.Id);

    public bool IsVisible(string moduleId)
    {
        lock (_gate)
        {
            if (!_hidden.TryGetValue(moduleId, out var hidden))
                _hidden[moduleId] = hidden = settings.Get(Prefix + moduleId) == "1";
            return !hidden;
        }
    }

    public void SetVisible(string moduleId, bool visible)
    {
        if (moduleId == "settings")
            return;
        if (visible)
            settings.Delete(Prefix + moduleId);
        else
            settings.Set(Prefix + moduleId, "1");
        lock (_gate) _hidden[moduleId] = !visible;
    }
}
