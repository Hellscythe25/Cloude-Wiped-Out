using System.Globalization;
using CloudGame.Gameplay;

namespace WipedOut;

/// <summary>Resultado de intentar aplicar una jugada.</summary>
/// <param name="Outcome">Ver <see cref="MatchOutcome"/>.</param>
/// <param name="Changed">
/// Hay que guardar el estado. Puede ser true aunque la jugada se rechace: el servidor no tiene
/// relojes, así que al recibir CUALQUIER petición aplica antes los plazos y abandonos que ya
/// vencieron, y eso también hay que persistirlo.
/// </param>
/// <param name="GameChanged">
/// La partida cambió de verdad (jugada, tiempo agotado, abandono) y por tanto hay que avisar al
/// rival. Si solo cambió la marca de actividad, no.
/// </param>
public readonly record struct MoveResult(string Outcome, bool Changed, bool GameChanged);

/// <summary>Resultado de <see cref="MatchRules.Observe"/>.</summary>
public readonly record struct ObserveResult(bool Changed, bool GameChanged);

/// <summary>El motor tras reproducir la lista de jugadas, y qué fue lo último que pasó.</summary>
public sealed record ReplayResult(MatchEngine? Engine, LastEvent? LastEvent);

/// <summary>
/// Las reglas de la sala online, sin ninguna dependencia de Cloud Code ni de Cloud Save, por lo
/// que se prueban sin servidor. Aquí es donde "el servidor es la autoridad": el motor
/// (<see cref="MatchEngine"/>) se ejecuta SOLO aquí, y al cliente solo le llega lo que
/// <see cref="BuildView"/> decide.
///
/// El estado de una partida es la semilla secreta más la lista de jugadas, solo de agregar.
/// Para saber cómo está la partida se REPRODUCE esa lista con un motor nuevo
/// (<see cref="Replay"/>); no se guarda una foto del tablero.
/// </summary>
public static class MatchRules
{
    /// <summary>
    /// Tiempo por turno en online. El diseño original baja a 2 s, pero con push + sondeo de
    /// respaldo de ~10 s y latencia real eso sería injugable.
    /// </summary>
    public const long TurnSeconds = 20;

    /// <summary>Margen para que una jugada enviada a tiempo no se pierda por la latencia.</summary>
    public const long GraceSeconds = 3;

    /// <summary>
    /// Sin actividad de un jugador durante este tiempo, quien consulte le gana por abandono.
    /// TTTXO lo lee de Remote Config; aquí es una constante hasta que el equipo decida.
    /// </summary>
    public const long AbandonTimeoutSeconds = 180;

    /// <summary>
    /// Cada consulta registraría actividad y obligaría a escribir en Cloud Save. Para no
    /// escribir en cada sondeo, la marca solo se actualiza si tiene más de estos segundos.
    /// Debe ser mucho menor que <see cref="AbandonTimeoutSeconds"/>.
    /// </summary>
    public const long ActivityResolutionSeconds = 30;

    /// <summary>Tope de timeouts encadenados que se aplican de golpe (cada uno cuesta una vida, así que acaba antes).</summary>
    private const int MaxChainedTimeouts = 20;

    public static long Unix(DateTime time) => new DateTimeOffset(time.ToUniversalTime()).ToUnixTimeSeconds();

    public static string Iso(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    public static string Iso(DateTime time) => time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static MatchState NewMatch(string code, string hostPlayerId, string requestId, DateTime now)
    {
        var at = Unix(now);
        return new MatchState
        {
            MatchCode = code,
            HostPlayerId = hostPlayerId,
            CreatedByRequestId = requestId,
            Status = MatchStatus.WaitingForGuest,
            HostLastActivityAtUnixSeconds = at,
            CreatedAtUnixSeconds = at,
            UpdatedAtUnixSeconds = at
        };
    }

    /// <summary>
    /// Entra el segundo jugador y arranca la partida. <paramref name="seed"/> es la semilla
    /// secreta de todas las minas: la elige el servidor, nunca el cliente. No puede ser 0
    /// (el motor la tomaría por "elige una al azar").
    /// </summary>
    public static void StartWithGuest(MatchState state, string guestPlayerId, int seed, DateTime now)
    {
        if (seed == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seed), "La semilla no puede ser 0.");
        }

        var at = Unix(now);
        state.GuestPlayerId = guestPlayerId;
        state.Seed = seed;
        state.Status = MatchStatus.Playing;
        state.TurnStartedAtUnixSeconds = at;

        // El tiempo que estuvo la sala esperando no cuenta como inactividad: si no, un anfitrión
        // que creó la sala hace 5 minutos se declararía "abandonado" en la primera llamada.
        state.HostLastActivityAtUnixSeconds = at;
        state.GuestLastActivityAtUnixSeconds = at;
        state.UpdatedAtUnixSeconds = at;
    }

    /// <summary>
    /// Reproduce la lista de jugadas con un motor nuevo y devuelve el estado resultante. Es
    /// determinista: la misma semilla y las mismas jugadas dan siempre la misma partida.
    /// </summary>
    public static ReplayResult Replay(MatchState state)
    {
        if (state.Seed == 0)
        {
            return new ReplayResult(null, null);
        }

        var engine = new MatchEngine(state.Seed);
        var last = new LastEvent { Kind = "joined", Seat = 2 };

        foreach (var move in state.Moves)
        {
            last = ApplyRecorded(engine, move);
        }

        return new ReplayResult(engine, last);
    }

    /// <summary>Busca si esta petición ya se aplicó (un reintento). Devuelve null si es nueva.</summary>
    public static ProcessedRequest? FindProcessed(MatchState state, string requestId)
    {
        var index = state.Moves.FindIndex(m => m.RequestId.Length > 0 && m.RequestId == requestId);
        if (index < 0)
        {
            return null;
        }

        // Cada jugada sube el número de turno exactamente una vez.
        return new ProcessedRequest { RequestId = requestId, ResultingTurnNumber = index + 1 };
    }

    /// <summary>
    /// Se llama en cada petición de un jugador (consultar o jugar): registra su actividad y
    /// resuelve lo que ya venció. El servidor no tiene temporizadores (Cloud Code solo ejecuta
    /// cuando alguien lo llama), así que se hace "perezosamente": se calcula qué habría pasado
    /// si el reloj hubiera corrido.
    /// </summary>
    public static ObserveResult Observe(MatchState state, string playerId, DateTime now)
    {
        var touched = Touch(state, playerId, Unix(now), force: false);
        var gameChanged = ResolveTime(state, playerId, now);
        return new ObserveResult(touched || gameChanged, gameChanged);
    }

    /// <summary>Anota la actividad del jugador. Devuelve true si cambió algo que haya que guardar.</summary>
    public static bool Touch(MatchState state, string playerId, long nowUnix, bool force)
    {
        var seat = state.SeatOf(playerId);
        if (seat == 0)
        {
            return false;
        }

        if (!force && nowUnix - state.LastActivityOfSeat(seat) < ActivityResolutionSeconds)
        {
            return false;
        }

        state.SetLastActivityOfSeat(seat, nowUnix);
        return true;
    }

    /// <summary>
    /// Resuelve lo que ya venció, en este orden:
    ///  1. <b>Abandono:</b> si el RIVAL de quien llama lleva más de <see cref="AbandonTimeoutSeconds"/>
    ///     sin actividad, pierde por abandono. Va primero a propósito: si los dos se fueron, gana
    ///     el primero que vuelve (si se aplicaran antes los plazos, perdería quien primero dejó
    ///     de jugar, aunque sea quien vuelve). Quien llama está activo por definición, así que
    ///     solo se puede declarar abandonado al rival.
    ///  2. <b>Plazos de turno:</b> cada uno cuesta una vida. Se cuentan desde el anterior
    ///     (<c>TurnStartedAt</c> avanza de <see cref="TurnSeconds"/> en <see cref="TurnSeconds"/>),
    ///     no desde "ahora", así que quien se desconectó sigue perdiendo una vida por turno
    ///     aunque nadie consulte durante un rato.
    /// </summary>
    /// <returns>true si la partida cambió.</returns>
    public static bool ResolveTime(MatchState state, string callerId, DateTime now)
    {
        if (state.Status != MatchStatus.Playing)
        {
            return false;
        }

        var engine = Replay(state).Engine!;
        var nowUnix = Unix(now);
        var changed = false;

        var callerSeat = state.SeatOf(callerId);
        var rivalSeat = 3 - callerSeat;
        if (callerSeat != 0 && nowUnix - state.LastActivityOfSeat(rivalSeat) > AbandonTimeoutSeconds)
        {
            engine.Forfeit(rivalSeat == 1 ? Player.PlayerOne : Player.PlayerTwo);
            state.Moves.Add(new MoveRecord { Seat = rivalSeat, Kind = MoveKind.Abandon, AtUnixSeconds = nowUnix });
            state.UpdatedAtUnixSeconds = nowUnix;
            Finish(state, engine, "abandoned");
            return true;
        }

        for (var i = 0;
             i < MaxChainedTimeouts
             && engine.Phase == MatchPhase.InProgress
             && nowUnix > state.TurnStartedAtUnixSeconds + TurnSeconds + GraceSeconds;
             i++)
        {
            var who = engine.CurrentPlayer;
            engine.ExpireTurn();

            state.TurnStartedAtUnixSeconds += TurnSeconds;
            state.Moves.Add(new MoveRecord
            {
                Seat = SeatOf(who),
                Kind = MoveKind.Timeout,
                AtUnixSeconds = state.TurnStartedAtUnixSeconds
            });
            changed = true;
        }

        if (changed)
        {
            state.UpdatedAtUnixSeconds = nowUnix;
            if (engine.Phase == MatchPhase.Finished)
            {
                Finish(state, engine, "lives");
            }
        }

        return changed;
    }

    /// <summary>
    /// Aplica una jugada pedida por un cliente. Orden de las comprobaciones: estado de la sala,
    /// plazos vencidos, versión del turno, turno, jugada válida según el motor.
    /// </summary>
    public static MoveResult ApplyPlay(
        MatchState state, string playerId, string requestId, int expectedTurnNumber,
        string kind, int cell, DateTime now)
    {
        if (state.Status == MatchStatus.WaitingForGuest)
        {
            return new MoveResult(MatchOutcome.NotStarted, false, false);
        }

        if (state.Status == MatchStatus.Finished)
        {
            return new MoveResult(MatchOutcome.MatchFinished, false, false);
        }

        if (kind is not (MoveKind.Reveal or MoveKind.Advance or MoveKind.Forfeit))
        {
            return new MoveResult(MatchOutcome.InvalidMove, false, false);
        }

        var nowUnix = Unix(now);
        var touched = Touch(state, playerId, nowUnix, force: false);
        var gameChanged = ResolveTime(state, playerId, now);
        var changed = touched || gameChanged;

        if (state.Status == MatchStatus.Finished)
        {
            return new MoveResult(MatchOutcome.MatchFinished, changed, gameChanged);
        }

        var engine = Replay(state).Engine!;
        var seat = state.SeatOf(playerId);

        // Abandonar se permite en cualquier momento, aunque no sea tu turno.
        if (kind == MoveKind.Forfeit)
        {
            engine.Forfeit(seat == 1 ? Player.PlayerOne : Player.PlayerTwo);
            Commit(state, requestId, seat, MoveKind.Forfeit, -1, nowUnix);
            Finish(state, engine, "forfeit");
            return new MoveResult(MatchOutcome.Applied, true, true);
        }

        if (expectedTurnNumber != engine.TurnNumber)
        {
            return new MoveResult(MatchOutcome.Stale, changed, gameChanged);
        }

        if (seat != SeatOf(engine.CurrentPlayer))
        {
            return new MoveResult(MatchOutcome.NotYourTurn, changed, gameChanged);
        }

        if (kind == MoveKind.Reveal)
        {
            var result = engine.Reveal(cell);
            switch (result.Outcome)
            {
                case RevealOutcome.InvalidCell:
                    return new MoveResult(MatchOutcome.InvalidCell, changed, gameChanged);
                case RevealOutcome.AlreadyRevealed:
                    return new MoveResult(MatchOutcome.AlreadyRevealed, changed, gameChanged);
                case RevealOutcome.MatchFinished:
                    return new MoveResult(MatchOutcome.MatchFinished, changed, gameChanged);
            }
        }
        else
        {
            var result = engine.AdvanceLevel();
            switch (result.Outcome)
            {
                case AdvanceOutcome.NotLevelOwner:
                    return new MoveResult(MatchOutcome.NotLevelOwner, changed, gameChanged);
                case AdvanceOutcome.MatchFinished:
                    return new MoveResult(MatchOutcome.MatchFinished, changed, gameChanged);
            }
        }

        Commit(state, requestId, seat, kind, kind == MoveKind.Reveal ? cell : -1, nowUnix);
        if (engine.Phase == MatchPhase.Finished)
        {
            Finish(state, engine, "lives");
        }

        return new MoveResult(MatchOutcome.Applied, true, true);
    }

    /// <summary>
    /// La vista que se envía al jugador <paramref name="viewerPlayerId"/>. Se construye desde
    /// cero y solo con datos públicos: no existe ningún camino por el que la semilla o una
    /// mina oculta lleguen aquí (hay una prueba que lo comprueba).
    /// </summary>
    public static MatchView BuildView(
        MatchState state, string viewerPlayerId, string outcome, string message, DateTime now)
    {
        var view = new MatchView
        {
            MatchCode = state.MatchCode,
            Status = state.Status,
            MySeat = state.SeatOf(viewerPlayerId),
            Outcome = outcome,
            Message = message,
            ServerTimeUtc = Iso(now),
            WinnerSeat = state.WinnerSeat,
            EndReason = state.EndReason
        };

        var replay = Replay(state);
        if (replay.Engine == null)
        {
            return view;
        }

        var engine = replay.Engine;
        view.LastEvent = replay.LastEvent;
        view.TurnNumber = engine.TurnNumber;
        view.Level = engine.Level;
        view.Size = engine.Board.Size;
        view.Cells = engine.Board.PublicCells();
        view.Lives1 = engine.PlayerOneLives;
        view.Lives2 = engine.PlayerTwoLives;
        view.LevelOwnerSeat = SeatOf(engine.LevelOwner);
        view.IsCleared = engine.Board.IsCleared;

        if (state.Status == MatchStatus.Playing)
        {
            view.CurrentSeat = SeatOf(engine.CurrentPlayer);
            view.TurnDeadlineUtc = Iso(state.TurnStartedAtUnixSeconds + TurnSeconds);
        }

        if (state.Status == MatchStatus.Finished)
        {
            view.Mines = engine.Board.MineIndices();
        }

        return view;
    }

    public static int SeatOf(Player player) => player == Player.PlayerOne ? 1 : 2;

    private static LastEvent ApplyRecorded(MatchEngine engine, MoveRecord move)
    {
        switch (move.Kind)
        {
            case MoveKind.Reveal:
                var reveal = engine.Reveal(move.Cell);
                return new LastEvent
                {
                    Kind = MoveKind.Reveal,
                    Seat = move.Seat,
                    Cell = move.Cell,
                    WasMine = reveal.Outcome == RevealOutcome.Mine,
                    LivesLost = reveal.LivesLost
                };

            case MoveKind.Advance:
                var advance = engine.AdvanceLevel();
                return new LastEvent { Kind = MoveKind.Advance, Seat = move.Seat, LivesLost = advance.LivesLost };

            case MoveKind.Timeout:
                engine.ExpireTurn();
                return new LastEvent { Kind = MoveKind.Timeout, Seat = move.Seat, LivesLost = 1 };

            case MoveKind.Forfeit:
            case MoveKind.Abandon:
                engine.Forfeit(move.Seat == 1 ? Player.PlayerOne : Player.PlayerTwo);
                return new LastEvent { Kind = move.Kind, Seat = move.Seat };

            default:
                throw new InvalidOperationException($"Jugada desconocida en el registro: '{move.Kind}'.");
        }
    }

    private static void Commit(MatchState state, string requestId, int seat, string kind, int cell, long nowUnix)
    {
        state.Moves.Add(new MoveRecord
        {
            RequestId = requestId,
            Seat = seat,
            Kind = kind,
            Cell = cell,
            AtUnixSeconds = nowUnix
        });

        state.TurnStartedAtUnixSeconds = nowUnix;
        state.UpdatedAtUnixSeconds = nowUnix;
        // Quien juega acaba de demostrar que está activo, sin esperar al margen de escritura.
        state.SetLastActivityOfSeat(seat, nowUnix);
    }

    private static void Finish(MatchState state, MatchEngine engine, string reason)
    {
        state.Status = MatchStatus.Finished;
        state.WinnerSeat = SeatOf(engine.Winner);
        state.EndReason = reason;
    }
}
