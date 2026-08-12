using System;
using System.Linq;
using System.Reflection;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Reflection boundary for the installed 0.7 runtime.</summary>
public sealed class LegacyRuntimeBridge
{
    private const string LegacyAssemblyName = "DarkwoodMultiplayerFramework";
    private Type? saveTransferType;
    private PropertyInfo? canUseWorldSync;
    private PropertyInfo? statusText;

    public bool IsAvailable => Resolve();

    public string Version
    {
        get
        {
            var assembly = FindAssembly();
            return assembly?.GetName().Version?.ToString() ?? string.Empty;
        }
    }

    public bool CanUseWorldSync
    {
        get
        {
            if (!Resolve()) return false;
            try { return canUseWorldSync?.GetValue(null) is bool value && value; }
            catch { return false; }
        }
    }

    public string StatusText
    {
        get
        {
            if (!Resolve()) return string.Empty;
            try { return statusText?.GetValue(null) as string ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    public void Refresh()
    {
        saveTransferType = null;
        canUseWorldSync = null;
        statusText = null;
        Resolve();
    }

    private bool Resolve()
    {
        if (saveTransferType != null && canUseWorldSync != null) return true;
        var assembly = FindAssembly();
        saveTransferType = assembly?.GetType("DarkwoodMultiplayerFramework.SaveTransferRuntime", false);
        canUseWorldSync = saveTransferType?.GetProperty("CanUseWorldSync", BindingFlags.Public | BindingFlags.Static);
        statusText = saveTransferType?.GetProperty("StatusText", BindingFlags.Public | BindingFlags.Static);
        return saveTransferType != null && canUseWorldSync != null;
    }

    private static Assembly? FindAssembly()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, LegacyAssemblyName, StringComparison.Ordinal));
    }
}
