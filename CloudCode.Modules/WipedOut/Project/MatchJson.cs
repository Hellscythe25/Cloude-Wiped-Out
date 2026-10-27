using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace WipedOut;

/// <summary>
/// Un único sitio donde se decide cómo viaja una vista por la red: JSON en camelCase,
/// sin campos nulos. Así el cliente de Unity la lee con JsonUtility sin adaptadores.
/// </summary>
public static class MatchJson
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore
    };

    public static string Serialize(MatchView view) => JsonConvert.SerializeObject(view, Settings);
}
