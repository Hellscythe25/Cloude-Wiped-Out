using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Unity.Services.CloudCode.Apis;
using Unity.Services.CloudCode.Core;

namespace WipedOut;

/// <summary>
/// Wiped-Out online: buscaminas por turnos para dos jugadores con el servidor como única
/// autoridad. Sigue el patrón de TTTXO:
///
///   - <c>CreateMatch</c> / <c>JoinMatch</c>: crean y llenan la sala (aquí por código de sala).
///   - <c>PlayMove</c>: valida y aplica una jugada con el mismo motor de reglas.
///   - <c>GetMatchState</c>: devuelve la vista; sirve para reconectar y, como el servidor no
///     tiene relojes, también resuelve los plazos vencidos y el abandono.
///   - **Push de Cloud Code (Wire):** al terminar cada jugada se avisa al rival con un
///     mensaje LIVIANO <c>{ type, matchId }</c> que nunca lleva el estado. El rival no se fía
///     del contenido: lo usa como señal para volver a pedir <c>GetMatchState</c>. Si el push
///     falla, la jugada ya quedó guardada: el rival se pondrá al día en su siguiente consulta.
///
/// Todas las funciones devuelven el JSON de una <see cref="MatchView"/> (texto).
/// </summary>
public class WipedOutModule
{
    /// <summary>Tipo (y campo <c>type</c> del payload) de los mensajes push. El cliente filtra por él.</summary>
    public const string PushMessageType = "match_updated";

    private const int MaxCodeAttempts = 5;
    private const int MaxRequestIdLength = 64;

    private readonly MatchRepository m_Repository;
    private readonly IPushClient m_PushClient;
    private readonly ILogger<WipedOutModule> m_Logger;

    public WipedOutModule(IGameApiClient gameApiClient, IPushClient pushClient, ILogger<WipedOutModule> logger)
    {
        m_Repository = new MatchRepository(gameApiClient);
        m_PushClient = pushClient;
        m_Logger = logger;
    }

    /// <summary>Crea una sala y devuelve su código. Idempotente: el código se deriva del requestId.</summary>
    [CloudCodeFunction("CreateMatch")]
    public async Task<string> CreateMatch(IExecutionContext context, string requestId)
    {
        var playerId = RequirePlayer(context);
        RequireRequestId(requestId);
        var now = DateTime.UtcNow;

        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = MatchCode.FromRequestId(requestId, attempt);
            var stored = await m_Repository.LoadAsync(context, code);

            if (stored == null)
            {
                var state = MatchRules.NewMatch(code, playerId, requestId, now);
                if (!await m_Repository.TrySaveAsync(context, state, null))
                {
                    return Respond(state, playerId, MatchOutcome.Conflict,
                        "Otra escritura se adelantó al crear la sala. Vuelve a intentarlo.", now);
                }

                m_Logger.LogInformation("Sala {Code} creada por {PlayerId}", code, playerId);
                return Respond(state, playerId, MatchOutcome.Applied, string.Empty, now);
            }

            // Salió de esta misma petición: el intento anterior sí llegó.
            if (stored.State.CreatedByRequestId == requestId && stored.State.HostPlayerId == playerId)
            {
                return Respond(stored.State, playerId, MatchOutcome.Replayed,
                    "Esta sala ya se había creado con esta misma petición.", now);
            }

            // Colisión con la sala de otra persona: siguiente candidato.
        }

        throw new Exception("No se ha encontrado un código de sala libre. Reintenta con una petición nueva.");
    }

    /// <summary>Une al jugador a una sala, sortea en secreto la semilla de las minas y avisa al anfitrión.</summary>
    [CloudCodeFunction("JoinMatch")]
    public async Task<string> JoinMatch(IExecutionContext context, string matchId, string requestId)
    {
        var playerId = RequirePlayer(context);
        RequireRequestId(requestId);
        var code = RequireCode(matchId);
        var now = DateTime.UtcNow;

        var stored = await LoadOrThrowAsync(context, code);
        var state = stored.State;

        // Reconexión: quien ya participa (anfitrión o invitado) y vuelve a "unirse" con el código
        // recibe el estado actual en lugar de un error. Es lo que permite volver a una partida
        // después de cerrar la app. Tampoco cambia nada, así que un reintento es inofensivo.
        if (state.IsParticipant(playerId))
        {
            return Respond(state, playerId, MatchOutcome.Replayed, "Ya estabas en esta partida.", now);
        }

        if (state.GuestPlayerId.Length > 0)
        {
            throw new Exception("Esta sala ya tiene dos jugadores.");
        }

        // La semilla de todas las minas la elige el servidor con aleatoriedad criptográfica.
        MatchRules.StartWithGuest(state, playerId, RandomNumberGenerator.GetInt32(1, int.MaxValue), now);

        if (!await m_Repository.TrySaveAsync(context, state, stored.WriteLock))
        {
            return await RespondConflictAsync(context, code, playerId, now);
        }

        m_Logger.LogInformation("{PlayerId} se unió a la sala {Code}", playerId, code);
        await NotifyOpponentAsync(context, state, playerId);
        return Respond(state, playerId, MatchOutcome.Applied, string.Empty, now);
    }

    /// <summary>
    /// Valida y aplica una jugada. El cliente envía solo la intención; el servidor decide.
    /// </summary>
    /// <param name="kind"><c>reveal</c> (con <paramref name="cell"/>), <c>advance</c> o <c>forfeit</c>.</param>
    /// <param name="requestId">
    /// Identificador de ESTA jugada: se reutiliza en los reintentos. Se comprueba antes que
    /// nada, porque si no un reintento legítimo se rechazaría con "no es tu turno" (la primera
    /// petición sí llegó y el turno ya pasó).
    /// </param>
    /// <param name="expectedTurnNumber">Turno sobre el que el cliente cree estar jugando.</param>
    [CloudCodeFunction("PlayMove")]
    public async Task<string> PlayMove(
        IExecutionContext context, string matchId, string requestId, int expectedTurnNumber, string kind, int cell)
    {
        var playerId = RequirePlayer(context);
        RequireRequestId(requestId);
        var code = RequireCode(matchId);
        var now = DateTime.UtcNow;

        var stored = await LoadOrThrowAsync(context, code);
        var state = stored.State;

        // 1. ¿Ya aplicamos esta petición?
        var processed = MatchRules.FindProcessed(state, requestId);
        if (processed != null)
        {
            return Respond(state, playerId, MatchOutcome.Replayed,
                $"Esta jugada ya se había aplicado; dejó la partida en el turno {processed.ResultingTurnNumber}.", now);
        }

        // 2. ¿Participas en esta partida?
        RequireParticipant(state, playerId);

        // 3. Plazos, turno y reglas del juego.
        var result = MatchRules.ApplyPlay(state, playerId, requestId, expectedTurnNumber, kind ?? string.Empty, cell, now);

        if (!result.Changed)
        {
            return Respond(state, playerId, result.Outcome, Describe(result.Outcome), now);
        }

        // 4. Guardar con write lock. Si otra escritura ganó la carrera, se devuelve el estado
        //    REAL (no el que acabamos de calcular y no se guardó).
        if (!await m_Repository.TrySaveAsync(context, state, stored.WriteLock))
        {
            return await RespondConflictAsync(context, code, playerId, now);
        }

        // 5. Avisar al rival. Un fallo del push nunca estropea la jugada.
        if (result.GameChanged)
        {
            await NotifyOpponentAsync(context, state, playerId);
        }

        return Respond(state, playerId, result.Outcome, Describe(result.Outcome), now);
    }

    /// <summary>
    /// Devuelve el estado actual (la vista de quien llama). Además registra su actividad y
    /// aplica lo que ya venció: plazos de turno y abandono del rival. Cloud Code no tiene
    /// temporizadores, así que cualquier jugador que consulta "despierta" la partida.
    /// </summary>
    [CloudCodeFunction("GetMatchState")]
    public async Task<string> GetMatchState(IExecutionContext context, string matchId)
    {
        var playerId = RequirePlayer(context);
        var code = RequireCode(matchId);
        var now = DateTime.UtcNow;

        var stored = await LoadOrThrowAsync(context, code);
        var state = stored.State;
        RequireParticipant(state, playerId);

        var observed = MatchRules.Observe(state, playerId, now);
        if (observed.Changed)
        {
            if (!await m_Repository.TrySaveAsync(context, state, stored.WriteLock))
            {
                // Otra escritura se adelantó (por ejemplo, una jugada del rival). Una consulta no
                // es una jugada: basta devolver el estado real.
                var fresh = await LoadOrThrowAsync(context, code);
                return Respond(fresh.State, playerId, MatchOutcome.Applied, string.Empty, now);
            }

            if (observed.GameChanged)
            {
                await NotifyOpponentAsync(context, state, playerId);
            }
        }

        return Respond(state, playerId, MatchOutcome.Applied, string.Empty, now);
    }

    private async Task NotifyOpponentAsync(IExecutionContext context, MatchState state, string callerId)
    {
        var opponentId = state.OpponentOf(callerId);
        if (opponentId.Length == 0)
        {
            return;
        }

        try
        {
            // Payload liviano: solo "algo cambió en esta partida". Nunca lleva el estado.
            var payload = JsonConvert.SerializeObject(new { type = PushMessageType, matchId = state.MatchCode });
            await m_PushClient.SendPlayerMessageAsync(context, payload, PushMessageType, opponentId);
        }
        catch (Exception e)
        {
            // El rival se enterará en su siguiente GetMatchState (sondeo de respaldo).
            m_Logger.LogWarning(e, "No se pudo enviar el push a {PlayerId} en la sala {Code}", opponentId, state.MatchCode);
        }
    }

    private async Task<string> RespondConflictAsync(
        IExecutionContext context, string code, string playerId, DateTime now)
    {
        var fresh = await LoadOrThrowAsync(context, code);
        return Respond(fresh.State, playerId, MatchOutcome.Conflict,
            "Otra jugada se adelantó a la tuya. Este es el estado real; vuelve a intentarlo.", now);
    }

    private static string Respond(MatchState state, string playerId, string outcome, string message, DateTime now)
    {
        return MatchJson.Serialize(MatchRules.BuildView(state, playerId, outcome, message, now));
    }

    private async Task<StoredMatch> LoadOrThrowAsync(IExecutionContext context, string code)
    {
        return await m_Repository.LoadAsync(context, code)
               ?? throw new Exception($"No existe ninguna sala con el código {code}.");
    }

    private static string Describe(string outcome)
    {
        return outcome switch
        {
            MatchOutcome.NotStarted => "La partida todavía no ha empezado: falta el rival.",
            MatchOutcome.MatchFinished => "La partida ya terminó.",
            MatchOutcome.Stale => "Tu jugada era sobre un estado viejo. Este es el estado actual.",
            MatchOutcome.NotYourTurn => "No es tu turno.",
            MatchOutcome.NotLevelOwner => "Solo el responsable del nivel puede pasar al siguiente (o cualquiera si el tablero está limpio).",
            MatchOutcome.AlreadyRevealed => "Esa casilla ya está revelada.",
            MatchOutcome.InvalidCell => "Esa casilla no existe en el tablero.",
            MatchOutcome.InvalidMove => "Jugada desconocida.",
            _ => string.Empty
        };
    }

    private static string RequirePlayer(IExecutionContext context)
    {
        if (string.IsNullOrEmpty(context.PlayerId))
        {
            throw new Exception("Hay que iniciar sesión para jugar.");
        }

        return context.PlayerId;
    }

    private static void RequireParticipant(MatchState state, string playerId)
    {
        if (!state.IsParticipant(playerId))
        {
            throw new Exception("No participas en esta partida.");
        }
    }

    private static void RequireRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > MaxRequestIdLength)
        {
            throw new Exception($"requestId es obligatorio y admite hasta {MaxRequestIdLength} caracteres.");
        }
    }

    private static string RequireCode(string matchId)
    {
        var code = MatchCode.Normalize(matchId);
        if (!MatchCode.IsWellFormed(code))
        {
            throw new Exception("El código de sala no es válido.");
        }

        return code;
    }
}
