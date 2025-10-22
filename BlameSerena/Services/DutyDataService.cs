using System;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace BlameSerena.Services;

/// <summary>
/// Handles retrieval of duty information from game data
/// </summary>
public interface IDutyDataService
{
    string GetDutyName(ushort dutyId);
    string GetDutyCategory(ushort dutyId);
}

public class DutyDataService : IDutyDataService
{
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;

    // Cached reflection properties for ContentType extraction
    private static readonly System.Reflection.PropertyInfo? CtProp =
        typeof(ContentFinderCondition).GetProperty("ContentType");
    private static readonly System.Reflection.PropertyInfo? CtValueProp =
        CtProp?.PropertyType.GetProperty("Value");
    private static readonly System.Reflection.PropertyInfo? CtNameProp =
        CtValueProp?.PropertyType.GetProperty("Name");
    private static readonly System.Reflection.PropertyInfo? CtNameDirectProp =
        CtProp?.PropertyType.GetProperty("Name");

    public DutyDataService(IDataManager dataManager, IPluginLog log)
    {
        _dataManager = dataManager;
        _log = log;
    }

    public string GetDutyName(ushort dutyId)
    {
        var dutySheet = _dataManager.GetExcelSheet<ContentFinderCondition>();
        if (dutySheet == null)
        {
            _log.Warning($"[GetDutyName] Could not get ContentFinderCondition sheet.");
            return string.Empty;
        }
        var entry = dutySheet.GetRow(dutyId);
        // Lumina's GetRow returns a struct, not null. Check RowId.
        if (entry.RowId != dutyId)
        {
            _log.Warning($"[GetDutyName] Could not find name for duty ID: {dutyId}");
            return string.Empty;
        }
        string name = entry.Name.ToString();
        if (!string.IsNullOrEmpty(name) && char.IsLower(name[0]))
        {
            // Capitalize the first letter if it's lowercase
            char[] chars = name.ToCharArray();
            chars[0] = char.ToUpper(chars[0]);
            name = new string(chars);
        }

        // Special case: Move (Savage) to the end for Bahamut turns
        // e.g. "The Second Coil of Bahamut (Savage) - Turn 2" -> "The Second Coil of Bahamut - Turn 2 (Savage)"
        if (name.Contains("(Savage)") && name.Contains("Turn"))
        {
            // Find "(Savage)" and "Turn"
            int savageIdx = name.IndexOf("(Savage)");
            int turnIdx = name.IndexOf("Turn");
            if (savageIdx > 0 && turnIdx > savageIdx)
            {
                // Remove " (Savage)" from its current position
                string withoutSavage = name.Remove(savageIdx - 1, 9); // Remove space + "(Savage)"
                // Insert " (Savage)" at the end
                name = withoutSavage + " (Savage)";
            }
        }

        return name;
    }

    public string GetDutyCategory(ushort dutyId)
    {
        var cfcRow = _dataManager
            .GetExcelSheet<ContentFinderCondition>()?
            .GetRow(dutyId);

        if (cfcRow == null)
            return "Other";

        try
        {
            if (CtProp != null)
            {
                var contentTypeValue = CtProp.GetValue(cfcRow);
                if (contentTypeValue != null)
                {
                    // Try .Value?.Name
                    if (CtValueProp != null)
                    {
                        var valueObj = CtValueProp.GetValue(contentTypeValue);
                        if (valueObj != null && CtNameProp != null)
                        {
                            var nameObj = CtNameProp.GetValue(valueObj);
                            if (nameObj != null)
                            {
                                var nameStr = nameObj.ToString();
                                if (!string.IsNullOrEmpty(nameStr))
                                    return nameStr;
                            }
                        }
                    }
                    // Try .Name directly
                    if (CtNameDirectProp != null)
                    {
                        var nameObj = CtNameDirectProp.GetValue(contentTypeValue);
                        if (nameObj != null)
                        {
                            var nameStr = nameObj.ToString();
                            if (!string.IsNullOrEmpty(nameStr))
                                return nameStr;
                        }
                    }
                }
            }
            // If ContentType is an int or enum, just return its value as string
            if (CtProp != null)
            {
                var intVal = CtProp.GetValue(cfcRow);
                if (intVal != null && intVal is int ctInt && ctInt != 0)
                    return ctInt.ToString();
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "ContentType reflection");
        }

        return "Other";
    }
}
