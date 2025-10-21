using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace BlameSerena.Services;

/// <summary>
/// Handles sending Discord notifications via HTTP
/// </summary>
public interface INotificationService
{
    Task SendPartyFinderNotificationAsync(
        string playerName,
        string dutyName,
        string description,
        string partyFinderPassword,
        ulong channelId,
        ulong roleId,
        string apiEndpoint,
        string category);
}

public class NotificationService : INotificationService
{
    private readonly IPluginLog _log;

    public NotificationService(IPluginLog log)
    {
        _log = log;
    }

    public async Task SendPartyFinderNotificationAsync(
        string playerName,
        string dutyName,
        string description,
        string partyFinderPassword,
        ulong channelId,
        ulong roleId,
        string apiEndpoint,
        string category)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);
            var payload = new
            {
                PlayerName = playerName,
                DutyName = dutyName,
                Description = description,
                Category = category,
                PartyFinderPassword = partyFinderPassword,
                ChannelId = channelId,
                RoleId = roleId
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            _log.Information($"[HTTP SEND] Attempting to send PF notification. Payload: {json}");
            var response = await httpClient.PostAsync(apiEndpoint, content);

            if (response.IsSuccessStatusCode)
            {
                _log.Information($"[HTTP SEND] Party Finder notification sent successfully for: {playerName} - {dutyName}");
            }
            else
            {
                string r = await response.Content.ReadAsStringAsync();
                _log.Error($"[HTTP SEND] Failed to send PF notification. Status: {response.StatusCode} ({response.ReasonPhrase}). Endpoint: {apiEndpoint}. Response: {r}");
            }
        }
        catch (HttpRequestException ex)
        {
            _log.Error(ex, $"[HTTP SEND] HTTP Request Exception to {apiEndpoint}.");
        }
        catch (TaskCanceledException ex)
        {
            _log.Error(ex, $"[HTTP SEND] Request timed out to {apiEndpoint}.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[HTTP SEND] Unexpected error sending notification to {apiEndpoint}.");
        }
    }
}
