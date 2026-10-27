using System.Net;
using Newtonsoft.Json.Linq;
using Unity.Services.CloudCode.Apis;
using Unity.Services.CloudCode.Core;
using Unity.Services.CloudCode.Shared;
using Unity.Services.CloudSave.Model;

namespace WipedOut;

/// <summary>El estado de la partida, más el write lock con el que se leyó.</summary>
public record StoredMatch(MatchState State, string? WriteLock);

/// <summary>
/// Lectura y escritura del estado en Cloud Save como custom data PRIVADO (mismo patrón que
/// TurnMatch). Privado quiere decir que ningún jugador puede leerlo ni escribirlo desde su
/// cliente: ahí dentro está la semilla de las minas. Por eso todo va con
/// <c>context.ServiceToken</c> y no con el token del jugador.
/// </summary>
public class MatchRepository
{
    private const string ItemKey = "state";

    private readonly IGameApiClient m_ApiClient;

    public MatchRepository(IGameApiClient apiClient)
    {
        m_ApiClient = apiClient;
    }

    /// <summary>Devuelve null si no existe ninguna partida con ese código.</summary>
    public async Task<StoredMatch?> LoadAsync(IExecutionContext context, string matchCode)
    {
        try
        {
            var response = await m_ApiClient.CloudSaveData.GetPrivateCustomItemsAsync(
                context,
                context.ServiceToken,
                context.ProjectId,
                matchCode,
                new List<string> { ItemKey },
                null!,
                default);

            var item = response.Data?.Results?.FirstOrDefault(i => i.Key == ItemKey);
            if (item?.Value == null)
            {
                return null;
            }

            var state = ToMatchState(item.Value);
            return state == null ? null : new StoredMatch(state, item.WriteLock);
        }
        catch (ApiException e) when (e.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Guarda el estado enviando el write lock de la lectura. Si otra escritura se coló en
    /// medio, Cloud Save responde 409 y esto devuelve false en lugar de pisarla.
    /// </summary>
    public async Task<bool> TrySaveAsync(IExecutionContext context, MatchState state, string? writeLock)
    {
        // writeLock nulo = escritura incondicional; correcto al crear la sala.
        var body = new SetItemBody(ItemKey, JObject.FromObject(state), writeLock!);

        try
        {
            await m_ApiClient.CloudSaveData.SetPrivateCustomItemAsync(
                context,
                context.ServiceToken,
                context.ProjectId,
                state.MatchCode,
                body,
                default);

            return true;
        }
        catch (ApiException e) when (e.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    private static MatchState? ToMatchState(object value)
    {
        return value switch
        {
            JObject json => json.ToObject<MatchState>(),
            string text when text.Length > 0 => JObject.Parse(text).ToObject<MatchState>(),
            _ => JObject.FromObject(value).ToObject<MatchState>()
        };
    }
}
