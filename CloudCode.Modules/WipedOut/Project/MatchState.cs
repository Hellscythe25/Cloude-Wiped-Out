namespace WipedOut;

/// <summary>Estado de la sala.</summary>
public static class MatchStatus
{
    public const string WaitingForGuest = "waiting_for_guest";
    public const string Playing = "playing";
    public const string Finished = "finished";
}

/// <summary>Tipos de jugada que el cliente puede pedir en <c>PlayMove</c>.</summary>
public static class MoveKind
{
    public const string Reveal = "reveal";
    public const string Advance = "advance";
    public const string Forfeit = "forfeit";

    // Estas dos las registra el SERVIDOR, el cliente no puede pedirlas.
    public const string Timeout = "timeout";
    public const string Abandon = "abandon";
}

/// <summary>
/// Veredicto de una petición. Las jugadas rechazadas viajan aquí, no como excepción:
/// no son errores, son parte normal del juego y el cliente necesita distinguirlas.
/// </summary>
public static class MatchOutcome
{
    public const string Applied = "applied";
    public const string Replayed = "replayed";
    public const string NotStarted = "not_started";
    public const string MatchFinished = "match_finished";
    public const string Stale = "stale";
    public const string NotYourTurn = "not_your_turn";
    public const string NotLevelOwner = "not_level_owner";
    public const string AlreadyRevealed = "already_revealed";
    public const string InvalidCell = "invalid_cell";
    public const string InvalidMove = "invalid_move";
    public const string Conflict = "conflict";
}

/// <summary>Qué fue lo último que pasó en la partida. Se envía en la vista para animar.</summary>
public class LastEvent
{
    /// <summary>"joined" o un <see cref="MoveKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    public int Seat { get; set; }
    public int Cell { get; set; } = -1;
    public bool WasMine { get; set; }
    public int LivesLost { get; set; }
}

/// <summary>Una jugada ya aplicada. La lista de jugadas es solo de agregar: nunca se edita ni se borra.</summary>
public class MoveRecord
{
    /// <summary>Vacío en las jugadas que registra el servidor (tiempo agotado, abandono).</summary>
    public string RequestId { get; set; } = string.Empty;

    public int Seat { get; set; }

    /// <summary>Un <see cref="MoveKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Casilla (solo en <see cref="MoveKind.Reveal"/>); -1 en las demás.</summary>
    public int Cell { get; set; } = -1;

    public long AtUnixSeconds { get; set; }
}

/// <summary>Resultado de buscar un reintento: la jugada ya aplicada y el turno en que dejó la partida.</summary>
public class ProcessedRequest
{
    public string RequestId { get; set; } = string.Empty;
    public int ResultingTurnNumber { get; set; }
}

/// <summary>
/// Lo que se guarda en Cloud Save (custom data PRIVADO). Contiene la semilla de las minas, por
/// eso este objeto jamás se envía a un cliente: a los clientes solo les llega
/// <see cref="MatchView"/>.
///
/// No hay una foto del tablero. Hay una <see cref="Seed"/> secreta y la lista de
/// <see cref="Moves"/>; en cada llamada se REPRODUCE con un motor nuevo (ver
/// <c>MatchRules.Replay</c>) y se obtiene de forma determinista el tablero, el turno y las vidas.
/// </summary>
public class MatchState
{
    public string MatchCode { get; set; } = string.Empty;

    /// <summary>Jugador 1 (asiento 1): el que crea la sala. Es dueño de los niveles impares.</summary>
    public string HostPlayerId { get; set; } = string.Empty;

    /// <summary>Jugador 2 (asiento 2): el que se une. Es dueño de los niveles pares.</summary>
    public string GuestPlayerId { get; set; } = string.Empty;

    public string CreatedByRequestId { get; set; } = string.Empty;

    public string Status { get; set; } = MatchStatus.WaitingForGuest;

    /// <summary>SECRETO. De ella salen las minas de todos los niveles. 0 hasta que se une el rival.</summary>
    public int Seed { get; set; }

    public List<MoveRecord> Moves { get; set; } = new();

    /// <summary>Cuándo empezó el turno actual (reloj del servidor). De aquí sale el plazo.</summary>
    public long TurnStartedAtUnixSeconds { get; set; }

    /// <summary>Última vez que el anfitrión llamó al servidor. Sirve para detectar abandono.</summary>
    public long HostLastActivityAtUnixSeconds { get; set; }

    public long GuestLastActivityAtUnixSeconds { get; set; }

    /// <summary>0 mientras no haya ganador; si no, el asiento (1 o 2) que ganó.</summary>
    public int WinnerSeat { get; set; }

    /// <summary>"lives", "forfeit" o "abandoned".</summary>
    public string EndReason { get; set; } = string.Empty;

    public long CreatedAtUnixSeconds { get; set; }
    public long UpdatedAtUnixSeconds { get; set; }

    public int SeatOf(string playerId)
    {
        if (playerId.Length == 0) return 0;
        if (playerId == HostPlayerId) return 1;
        if (playerId == GuestPlayerId) return 2;
        return 0;
    }

    public bool IsParticipant(string playerId) => SeatOf(playerId) != 0;

    public string OpponentOf(string playerId)
    {
        return SeatOf(playerId) switch
        {
            1 => GuestPlayerId,
            2 => HostPlayerId,
            _ => string.Empty
        };
    }

    public long LastActivityOfSeat(int seat) => seat == 1 ? HostLastActivityAtUnixSeconds : GuestLastActivityAtUnixSeconds;

    public void SetLastActivityOfSeat(int seat, long unixSeconds)
    {
        if (seat == 1) HostLastActivityAtUnixSeconds = unixSeconds;
        else if (seat == 2) GuestLastActivityAtUnixSeconds = unixSeconds;
    }
}
